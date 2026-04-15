#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game_Engine.Core;
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Networking
{
    /// <summary>
    /// Partial <see cref="NetworkManager"/>: rate limits, interest-based replication, disconnect policy,
    /// reliable state snapshots, and optional unchanged-state omission.
    /// </summary>
    public static partial class NetworkManager
    {
        // ── Caps (documented in 09_Scene_And_Project_Management.md) ──
        internal const int MaxRpcPayloadBytes = 512 * 1024;
        internal const int MaxRpcMethodNameChars = 256;
        /// <summary>Max UTF-8 character length for spawn prefab keys (enforced on server spawn).</summary>
        public const int MaxSpawnPrefabKeyChars = 128;

        // ── Rate limits (server inbound only) ──
        static readonly Dictionary<int, (int Rpc, int Input, int Surface)> _rateWindowCounts = new();
        static DateTime _rateWindowUtc = DateTime.UtcNow;
        internal const double RateWindowSeconds = 1.0;
        /// <summary>Max inbound RPC messages per peer per second (server only).</summary>
        public static int MaxRpcMessagesPerPeerPerSecond { get; set; } = 200;
        /// <summary>Max inbound client input messages per peer per second (server only).</summary>
        public static int MaxClientInputMessagesPerPeerPerSecond { get; set; } = 120;

        // ── Interest management ──
        /// <summary>
        /// When non-null, server includes each <see cref="NetworkIdentity"/> in a peer's state broadcast only if this returns true.
        /// When null, all objects replicate to all clients (default).
        /// </summary>
        public static Func<uint, int, bool>? ShouldReplicateToPeer { get; set; }

        // ── Omit unchanged full snapshots ──
        static readonly Dictionary<uint, byte[]> _lastBroadcastState = new();
        /// <summary>
        /// When true, server skips including an object in <see cref="BroadcastState"/> if its serialized state is byte-identical to the last broadcast.</summary>
        public static bool OmitUnchangedStateInBroadcast { get; set; }

        // ── Disconnect policy ──
        /// <summary>When true, server destroys runtime-spawned objects whose <see cref="NetworkIdentity.OwnerPeerId"/> matches a disconnecting peer.</summary>
        public static bool DespawnOwnedRuntimeSpawnsOnDisconnect { get; set; }

        static void ResetReplicationState()
        {
            _rateWindowCounts.Clear();
            _rateWindowUtc = DateTime.UtcNow;
            _lastBroadcastState.Clear();
            ShouldReplicateToPeer = null;
            OmitUnchangedStateInBroadcast = false;
            DespawnOwnedRuntimeSpawnsOnDisconnect = false;
            ResetSurfaceStreamingState();
        }

        static bool TryConsumeInboundRate(int peerId, bool isClientInput)
        {
            if (!IsServer) return true;
            var now = DateTime.UtcNow;
            if ((now - _rateWindowUtc).TotalSeconds >= RateWindowSeconds)
            {
                _rateWindowUtc = now;
                _rateWindowCounts.Clear();
            }

            if (!_rateWindowCounts.TryGetValue(peerId, out var c))
                c = (0, 0, 0);

            if (isClientInput)
            {
                if (c.Input >= MaxClientInputMessagesPerPeerPerSecond)
                {
                    Log.Warning($"[Network] ClientInput rate limit peer {peerId}");
                    return false;
                }
                _rateWindowCounts[peerId] = (c.Rpc, c.Input + 1, c.Surface);
            }
            else
            {
                if (c.Rpc >= MaxRpcMessagesPerPeerPerSecond)
                {
                    Log.Warning($"[Network] RPC rate limit peer {peerId}");
                    return false;
                }
                _rateWindowCounts[peerId] = (c.Rpc + 1, c.Input, c.Surface);
            }

            return true;
        }

        static bool TryConsumeSurfaceChunkRequestRate(int peerId)
        {
            if (!IsServer) return true;
            var now = DateTime.UtcNow;
            if ((now - _rateWindowUtc).TotalSeconds >= RateWindowSeconds)
            {
                _rateWindowUtc = now;
                _rateWindowCounts.Clear();
            }

            if (!_rateWindowCounts.TryGetValue(peerId, out var c))
                c = (0, 0, 0);

            if (c.Surface >= MaxSurfaceChunkRequestsPerPeerPerSecond)
            {
                Log.Warning($"[Network] SurfaceChunkRequest rate limit peer {peerId}");
                return false;
            }

            _rateWindowCounts[peerId] = (c.Rpc, c.Input, c.Surface + 1);
            return true;
        }

        /// <summary>Drop inbound messages that the receiving role must never apply (authority rules).</summary>
        static bool ShouldDropInboundMessage(NetworkPeer peer, NetMessageType msgType)
        {
            if (IsClient)
            {
                if (msgType == NetMessageType.ClientInput)
                {
                    Log.Warning($"[Network] Ignoring ClientInput from peer {peer.PeerId} (clients do not process this message type)");
                    return true;
                }
            }

            if (IsServer)
            {
                switch (msgType)
                {
                    case NetMessageType.StateSync:
                    case NetMessageType.Spawn:
                    case NetMessageType.Despawn:
                        Log.Warning($"[Network] Ignoring {msgType} from peer {peer.PeerId} (server does not accept these from clients)");
                        return true;
                    case NetMessageType.SurfaceChunkData:
                        Log.Warning($"[Network] Ignoring SurfaceChunkData from peer {peer.PeerId} (only server sends surface payloads)");
                        return true;
                }
            }

            if (IsClient && msgType == NetMessageType.SurfaceChunkRequest)
            {
                Log.Warning($"[Network] Ignoring SurfaceChunkRequest (clients only send requests, not receive them)");
                return true;
            }

            return false;
        }

        static void HandleTransportPeerDisconnected(NetworkPeer peer, string reason)
        {
            if (IsServer && DespawnOwnedRuntimeSpawnsOnDisconnect)
                TryDespawnRuntimeObjectsOwnedBy(peer.PeerId);
            OnPlayerDisconnected?.Invoke(peer, reason);
        }

        static void TryDespawnRuntimeObjectsOwnedBy(int peerId)
        {
            var toDespawn = NetworkedObjects.Values
                .Where(n => n.OwnerPeerId == peerId && _spawnCatalog.ContainsKey(n.NetworkId))
                .ToList();
            foreach (var ni in toDespawn)
                ServerDespawn(ni);
        }

        /// <summary>Server: one reliable state snapshot for one net id (e.g. after spawn). Clients apply via normal deserialize.</summary>
        public static void BroadcastReliableStateSnapshotFor(uint netId)
        {
            if (!IsServer || _transport == null) return;
            if (!_networkedObjects.TryGetValue(netId, out var identity)) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.StateSync);
            bw.Write(1);
            bw.Write(netId);
            var state = identity.SerializeState();
            bw.Write(state.Length);
            bw.Write(state);
            _transport.SendToAll(ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        /// <summary>Per-peer state broadcast when <see cref="ShouldReplicateToPeer"/> is set; otherwise full broadcast.</summary>
        public static void BroadcastStateFiltered()
        {
            if (!IsServer || _transport == null) return;

            var filter = ShouldReplicateToPeer;
            if (filter == null)
            {
                BroadcastStateFullInternal();
                return;
            }

            foreach (var peer in _transport.Peers.Values)
            {
                if (peer.State != ConnectionState.Connected || peer.PeerId == 0)
                    continue;

                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8);
                bw.Write((byte)NetMessageType.StateSync);

                var included = new List<(uint netId, NetworkIdentity identity)>();
                foreach (var (netId, identity) in _networkedObjects)
                {
                    try
                    {
                        if (filter(netId, peer.PeerId))
                            included.Add((netId, identity));
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[Network] ShouldReplicateToPeer error: {ex.Message}");
                    }
                }

                bw.Write(included.Count);
                foreach (var (netId, identity) in included)
                {
                    bw.Write(netId);
                    var state = identity.SerializeState();
                    bw.Write(state.Length);
                    bw.Write(state);
                }

                _transport.Send(peer.PeerId, ms.ToArray(), 0, DeliveryMode.Unreliable);
            }
        }

        static void BroadcastStateFullInternal()
        {
            if (_transport == null) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.StateSync);

            if (!OmitUnchangedStateInBroadcast)
            {
                bw.Write(_networkedObjects.Count);
                foreach (var (netId, identity) in _networkedObjects)
                {
                    bw.Write(netId);
                    var state = identity.SerializeState();
                    bw.Write(state.Length);
                    bw.Write(state);
                }
            }
            else
            {
                var entries = new List<(uint netId, byte[] state)>();
                foreach (var (netId, identity) in _networkedObjects)
                {
                    var state = identity.SerializeState();
                    if (_lastBroadcastState.TryGetValue(netId, out var prev) && prev.AsSpan().SequenceEqual(state))
                        continue;
                    _lastBroadcastState[netId] = state;
                    entries.Add((netId, state));
                }

                bw.Write(entries.Count);
                foreach (var (netId, state) in entries)
                {
                    bw.Write(netId);
                    bw.Write(state.Length);
                    bw.Write(state);
                }
            }

            _transport.SendToAll(ms.ToArray(), 0, DeliveryMode.Unreliable);
        }
    }
}
