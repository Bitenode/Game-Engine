#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Networking
{
    /// <summary>Network role.</summary>
    public enum NetworkRole { None, Server, Client }

    /// <summary>
    /// Central network manager — manages the connection lifecycle,
    /// object spawning, state synchronization, and RPC dispatch.
    /// </summary>
    public static partial class NetworkManager
    {
        // ── State ──
        private static NetworkTransport? _transport;
        private static NetworkRole _role = NetworkRole.None;
        private static readonly Dictionary<uint, NetworkIdentity> _networkedObjects = new();
        private static readonly Dictionary<string, Action<int, byte[]>> _rpcHandlers = new();
        private static uint _nextNetId = 1;
        private static float _stateBroadcastAccum;
        private const float StateBroadcastInterval = 1f / 20f; // 20 Hz transform/state sync
        private static bool _loggedSharedWorldSnapshot;

        static NetworkManager()
        {
            SceneService.SceneReplaced += () => { _loggedSharedWorldSnapshot = false; };
        }

        /// <summary>Current network role.</summary>
        public static NetworkRole Role => _role;

        /// <summary>Is this instance a server?</summary>
        public static bool IsServer => _role == NetworkRole.Server;

        /// <summary>Is this instance a client?</summary>
        public static bool IsClient => _role == NetworkRole.Client;

        /// <summary>Is networking active?</summary>
        public static bool IsActive => _transport != null && _transport.IsRunning;

        /// <summary>The transport layer.</summary>
        public static NetworkTransport? Transport => _transport;

        /// <summary>All networked objects.</summary>
        public static IReadOnlyDictionary<uint, NetworkIdentity> NetworkedObjects => _networkedObjects;

        // ── Events ──
        public static event Action<NetworkPeer>? OnPlayerConnected;
        public static event Action<NetworkPeer, string>? OnPlayerDisconnected;

        /// <summary>Start as a server on the given port.</summary>
        public static void StartServer(int port = 7777)
        {
            if (IsActive) return;
            _transport = new NetworkTransport();
            _transport.OnPeerConnected += OnTransportPeerConnected;
            _transport.OnPeerDisconnected += HandleTransportPeerDisconnected;
            _transport.OnDataReceived += HandleNetworkData;
            _transport.StartServer(port);
            _role = NetworkRole.Server;
            NetworkSurfaceDispatch.AttachServerHandler();
            PlanetTerrain.EnsurePlanetNetworkRpcsRegistered();
            Log.Info("[NetworkManager] Server started.");
        }

        /// <summary>Connect to a server as a client.</summary>
        public static void StartClient(string host = "127.0.0.1", int port = 7777)
        {
            if (IsActive) return;
            _transport = new NetworkTransport();
            _transport.OnPeerConnected += OnTransportPeerConnected;
            _transport.OnPeerDisconnected += HandleTransportPeerDisconnected;
            _transport.OnDataReceived += HandleNetworkData;
            _transport.StartClient(host, port);
            _role = NetworkRole.Client;
            PlanetTerrain.EnsurePlanetNetworkRpcsRegistered();
            Log.Info($"[NetworkManager] Client connecting to {host}:{port}...");
        }

        /// <summary>Stop networking and disconnect.</summary>
        public static void Stop()
        {
            _transport?.Dispose();
            _transport = null;
            _role = NetworkRole.None;
            _networkedObjects.Clear();
            _stateBroadcastAccum = 0f;
            _loggedSharedWorldSnapshot = false;
            ClearSpawnState();
            ResetReplicationState();
            PlanetTerrain.ResetPlanetNetworkStatics();
            Log.Info("[NetworkManager] Network stopped.");
        }

        /// <summary>
        /// Process network messages. Call each frame from the game loop.
        /// </summary>
        public static void Update()
        {
            _transport?.Poll();

            if (IsActive && !_loggedSharedWorldSnapshot && SceneService.Root.Count > 0)
            {
                NetworkWorldDiagnostics.LogSharedWorldSnapshot();
                _loggedSharedWorldSnapshot = true;
            }

            // Server: broadcast NetworkIdentity state to all peers (Time.deltaTime valid after Time.BeginUpdate in game loop)
            if (IsServer && _transport != null && _networkedObjects.Count > 0)
            {
                _stateBroadcastAccum += Game_Engine.Core.Time.deltaTime;
                if (_stateBroadcastAccum >= StateBroadcastInterval)
                {
                    _stateBroadcastAccum %= StateBroadcastInterval;
                    BroadcastState();
                }
            }
        }

        // ── Object registration ──

        /// <summary>Register a networked object.</summary>
        public static void RegisterObject(NetworkIdentity identity)
        {
            uint id = identity.NetworkId;

            if (id == 0)
            {
                if (IsActive)
                    Log.Warning("[NetworkIdentity] NetworkId is 0 while networking is active. Assign stable non-zero IDs in the scene for multiplayer; auto-assigned IDs can desync if registration order differs between peers.");
                id = AllocateUniqueNetId();
                identity.NetworkId = id;
            }
            else if (_networkedObjects.TryGetValue(id, out var existing) && !ReferenceEquals(existing, identity))
            {
                Log.Error($"[NetworkIdentity] Duplicate NetworkId {id} on '{identity.gameObject?.Name}' and '{existing.gameObject?.Name}'. Replication will be wrong until IDs are unique.");
                return;
            }

            _nextNetId = System.Math.Max(_nextNetId, id + 1);
            _networkedObjects[id] = identity;
        }

        static uint AllocateUniqueNetId()
        {
            while (_networkedObjects.ContainsKey(_nextNetId)) _nextNetId++;
            return _nextNetId++;
        }

        /// <summary>Unregister a networked object.</summary>
        public static void UnregisterObject(NetworkIdentity identity)
        {
            if (_networkedObjects.TryGetValue(identity.NetworkId, out var reg) && ReferenceEquals(reg, identity))
                _networkedObjects.Remove(identity.NetworkId);
        }

        // ── RPC System ──

        /// <summary>Register an RPC handler by method name.</summary>
        public static void RegisterRPC(string methodName, Action<int, byte[]> handler)
        {
            _rpcHandlers[methodName] = handler;
        }

        /// <summary>Send an RPC to a specific peer.</summary>
        public static void SendRPC(int peerId, string methodName, byte[] data)
        {
            if (_transport == null) return;
            if (data.Length > MaxRpcPayloadBytes)
            {
                Log.Warning($"[Network] RPC payload too large ({data.Length} > {MaxRpcPayloadBytes})");
                return;
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.RPC);
            bw.Write(methodName);
            bw.Write(data.Length);
            bw.Write(data);

            _transport.Send(peerId, ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        /// <summary>Send an RPC to all connected peers.</summary>
        public static void SendRPCAll(string methodName, byte[] data)
        {
            if (_transport == null) return;
            if (data.Length > MaxRpcPayloadBytes)
            {
                Log.Warning($"[Network] RPC payload too large ({data.Length} > {MaxRpcPayloadBytes})");
                return;
            }

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.RPC);
            bw.Write(methodName);
            bw.Write(data.Length);
            bw.Write(data);

            _transport.SendToAll(ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        // ── State Sync ──

        /// <summary>
        /// Broadcast the state of all networked objects to all peers.
        /// Call at a fixed rate (e.g., 20Hz) from the server.
        /// Uses <see cref="ShouldReplicateToPeer"/> when set; otherwise full broadcast (with optional unchanged omission).
        /// </summary>
        public static void BroadcastState()
        {
            if (!IsServer || _transport == null) return;
            if (ShouldReplicateToPeer != null)
                BroadcastStateFiltered();
            else
                BroadcastStateFullInternal();
        }

        // ── Message handling ──

        private static void HandleNetworkData(NetworkPeer peer, byte[] data, byte channel)
        {
            if (data.Length == 0) return;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms, Encoding.UTF8);

            var msgType = (NetMessageType)br.ReadByte();
            if (ShouldDropInboundMessage(peer, msgType)) return;

            switch (msgType)
            {
                case NetMessageType.RPC:
                    if (IsServer && !TryConsumeInboundRate(peer.PeerId, false)) return;
                    HandleRPC(peer.PeerId, br);
                    break;
                case NetMessageType.StateSync:
                    if (IsClient)
                        HandleStateSync(br);
                    break;
                case NetMessageType.Spawn:
                    if (IsClient)
                        HandleSpawnMessage(br);
                    break;
                case NetMessageType.Despawn:
                    if (IsClient)
                        HandleDespawnMessage(br);
                    break;
                case NetMessageType.ClientInput:
                    if (IsServer && !TryConsumeInboundRate(peer.PeerId, true)) return;
                    if (IsServer)
                        HandleClientInputMessage(peer.PeerId, br);
                    break;
                case NetMessageType.SurfaceChunkRequest:
                    if (IsServer && !TryConsumeSurfaceChunkRequestRate(peer.PeerId)) return;
                    if (IsServer)
                        HandleSurfaceChunkRequest(peer.PeerId, br);
                    break;
                case NetMessageType.SurfaceChunkData:
                    if (IsClient)
                        HandleSurfaceChunkData(br);
                    break;
            }
        }

        private static void HandleRPC(int peerId, BinaryReader br)
        {
            try
            {
                string methodName = br.ReadString();
                if (methodName.Length > MaxRpcMethodNameChars)
                {
                    Log.Warning($"[Network] RPC method name too long");
                    return;
                }
                int len = br.ReadInt32();
                if (len < 0 || len > MaxRpcPayloadBytes)
                {
                    Log.Warning($"[Network] RPC payload length invalid: {len}");
                    return;
                }
                byte[] rpcData = br.ReadBytes(len);

                if (_rpcHandlers.TryGetValue(methodName, out var handler))
                    handler.Invoke(peerId, rpcData);
                else
                    Log.Warning($"[Network] Unhandled RPC: {methodName}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Network] RPC error: {ex.Message}");
            }
        }

        private static void HandleStateSync(BinaryReader br)
        {
            try
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    uint netId = br.ReadUInt32();
                    int len = br.ReadInt32();
                    byte[] state = br.ReadBytes(len);

                    if (_networkedObjects.TryGetValue(netId, out var identity))
                        identity.DeserializeState(state);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[Network] State sync error: {ex.Message}");
            }
        }

        internal enum NetMessageType : byte
        {
            RPC = 1,
            StateSync = 2,
            Spawn = 3,
            Despawn = 4,
            ClientInput = 5,
            SurfaceChunkRequest = 6,
            SurfaceChunkData = 7
        }
    }
}
