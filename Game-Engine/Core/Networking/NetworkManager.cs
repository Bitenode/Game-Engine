#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Networking
{
    /// <summary>Network role.</summary>
    public enum NetworkRole { None, Server, Client }

    /// <summary>
    /// Central network manager — manages the connection lifecycle,
    /// object spawning, state synchronization, and RPC dispatch.
    /// </summary>
    public static class NetworkManager
    {
        // ── State ──
        private static NetworkTransport? _transport;
        private static NetworkRole _role = NetworkRole.None;
        private static readonly Dictionary<uint, NetworkIdentity> _networkedObjects = new();
        private static readonly Dictionary<string, Action<int, byte[]>> _rpcHandlers = new();
        private static uint _nextNetId = 1;

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
            _transport.OnPeerConnected += peer => OnPlayerConnected?.Invoke(peer);
            _transport.OnPeerDisconnected += (peer, reason) => OnPlayerDisconnected?.Invoke(peer, reason);
            _transport.OnDataReceived += HandleNetworkData;
            _transport.StartServer(port);
            _role = NetworkRole.Server;
            Log.Info("[NetworkManager] Server started.");
        }

        /// <summary>Connect to a server as a client.</summary>
        public static void StartClient(string host = "127.0.0.1", int port = 7777)
        {
            if (IsActive) return;
            _transport = new NetworkTransport();
            _transport.OnPeerConnected += peer => OnPlayerConnected?.Invoke(peer);
            _transport.OnPeerDisconnected += (peer, reason) => OnPlayerDisconnected?.Invoke(peer, reason);
            _transport.OnDataReceived += HandleNetworkData;
            _transport.StartClient(host, port);
            _role = NetworkRole.Client;
            Log.Info($"[NetworkManager] Client connecting to {host}:{port}...");
        }

        /// <summary>Stop networking and disconnect.</summary>
        public static void Stop()
        {
            _transport?.Dispose();
            _transport = null;
            _role = NetworkRole.None;
            _networkedObjects.Clear();
            Log.Info("[NetworkManager] Network stopped.");
        }

        /// <summary>
        /// Process network messages. Call each frame from the game loop.
        /// </summary>
        public static void Update()
        {
            _transport?.Poll();
        }

        // ── Object registration ──

        /// <summary>Register a networked object.</summary>
        public static void RegisterObject(NetworkIdentity identity)
        {
            if (identity.NetworkId == 0)
                identity.NetworkId = _nextNetId++;
            _networkedObjects[identity.NetworkId] = identity;
        }

        /// <summary>Unregister a networked object.</summary>
        public static void UnregisterObject(NetworkIdentity identity)
        {
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
        /// </summary>
        public static void BroadcastState()
        {
            if (!IsServer || _transport == null) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.StateSync);
            bw.Write(_networkedObjects.Count);

            foreach (var (netId, identity) in _networkedObjects)
            {
                bw.Write(netId);
                var state = identity.SerializeState();
                bw.Write(state.Length);
                bw.Write(state);
            }

            _transport.SendToAll(ms.ToArray(), 0, DeliveryMode.Unreliable);
        }

        // ── Message handling ──

        private static void HandleNetworkData(NetworkPeer peer, byte[] data, byte channel)
        {
            if (data.Length == 0) return;

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms, Encoding.UTF8);

            var msgType = (NetMessageType)br.ReadByte();

            switch (msgType)
            {
                case NetMessageType.RPC:
                    HandleRPC(peer.PeerId, br);
                    break;
                case NetMessageType.StateSync:
                    HandleStateSync(br);
                    break;
            }
        }

        private static void HandleRPC(int peerId, BinaryReader br)
        {
            try
            {
                string methodName = br.ReadString();
                int len = br.ReadInt32();
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

        private enum NetMessageType : byte
        {
            RPC = 1,
            StateSync = 2,
            Spawn = 3,
            Despawn = 4
        }
    }
}
