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
    public static partial class NetworkManager
    {
        static readonly Dictionary<string, Func<GameObject>> _spawnFactories = new();
        static readonly Dictionary<uint, string> _spawnCatalog = new();
        static Action<int, byte[]>? _clientInputHandler;

        static void ClearSpawnState()
        {
            _spawnCatalog.Clear();
            _clientInputHandler = null;
        }

        static void OnTransportPeerConnected(NetworkPeer peer)
        {
            OnPlayerConnected?.Invoke(peer);
            if (IsServer)
                SyncSpawnsToPeer(peer.PeerId);
        }

        /// <summary>
        /// Register a factory that builds the visual/logic hierarchy for a spawn key.
        /// Do not add <see cref="NetworkIdentity"/> in the factory — the manager adds it with the server-assigned <see cref="NetworkIdentity.NetworkId"/>.
        /// Call with the same keys on server and all clients before gameplay so spawn replication can instantiate.
        /// </summary>
        public static void RegisterSpawnPrefab(string key, Func<GameObject> create)
        {
            if (string.IsNullOrWhiteSpace(key) || create == null) return;
            _spawnFactories[key.Trim()] = create;
        }

        public static bool UnregisterSpawnPrefab(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            return _spawnFactories.Remove(key.Trim());
        }

        /// <summary>Server-only: instantiate a registered prefab, assign a <see cref="NetworkIdentity"/>, replicate to clients, and return the identity.</summary>
        /// <param name="ownerPeerId">Set to the controlling client peer id for <see cref="NetworkIdentity.OwnerPeerId"/> (used with disconnect despawn); default -1 (server-owned).</param>
        public static NetworkIdentity? ServerSpawn(string prefabKey, Vector3 position, Vector3 rotation, Vector3 scale, int ownerPeerId = -1)
        {
            if (!IsServer || _transport == null) return null;
            var key = prefabKey?.Trim() ?? "";
            if (key.Length > MaxSpawnPrefabKeyChars)
            {
                Log.Error($"[NetworkManager] Spawn prefab key too long (max {MaxSpawnPrefabKeyChars})");
                return null;
            }
            if (string.IsNullOrEmpty(key) || !_spawnFactories.TryGetValue(key, out var factory))
            {
                Log.Error($"[NetworkManager] Unknown spawn prefab key: {prefabKey}");
                return null;
            }

            GameObject go;
            try { go = factory(); }
            catch (Exception ex)
            {
                Log.Error($"[NetworkManager] Spawn factory failed: {ex.Message}");
                return null;
            }
            if (go == null) return null;

            uint id = AllocateUniqueNetId();
            var t = go.Transform;
            t.Position = position;
            t.Rotation = rotation;
            t.Scale = scale;

            var ni = new NetworkIdentity { NetworkId = id, OwnerPeerId = ownerPeerId };
            go.AddBehavior(ni);

            SceneService.Add(go);
            _spawnCatalog[id] = key;

            BroadcastSpawnPacket(id, key, ni);
            BroadcastReliableStateSnapshotFor(id);
            return ni;
        }

        /// <summary>Server-only: destroy the object and notify clients.</summary>
        public static void ServerDespawn(NetworkIdentity? identity)
        {
            if (identity == null || !IsServer || _transport == null) return;
            uint id = identity.NetworkId;
            BroadcastDespawnPacket(id);
            _spawnCatalog.Remove(id);
            var go = identity.gameObject;
            if (go != null)
                DestroyNetworkedGameObject(go);
        }

        static void SyncSpawnsToPeer(int peerId)
        {
            if (_transport == null || !IsServer) return;
            foreach (var kv in _spawnCatalog)
            {
                uint id = kv.Key;
                string key = kv.Value;
                if (!_networkedObjects.TryGetValue(id, out var identity)) continue;
                SendSpawnPacketToPeer(peerId, id, key, identity);
            }
        }

        static void BroadcastSpawnPacket(uint netId, string prefabKey, NetworkIdentity identity)
        {
            if (_transport == null) return;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.Spawn);
            WriteSpawnBody(bw, netId, prefabKey, identity);
            _transport.SendToAll(ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        static void SendSpawnPacketToPeer(int peerId, uint netId, string prefabKey, NetworkIdentity identity)
        {
            if (_transport == null) return;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.Spawn);
            WriteSpawnBody(bw, netId, prefabKey, identity);
            _transport.Send(peerId, ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        static void WriteSpawnBody(BinaryWriter bw, uint netId, string prefabKey, NetworkIdentity identity)
        {
            bw.Write(netId);
            bw.Write(prefabKey);
            var tr = identity.Transform;
            bw.Write((float)tr.Position.X);
            bw.Write((float)tr.Position.Y);
            bw.Write((float)tr.Position.Z);
            bw.Write((float)tr.Rotation.X);
            bw.Write((float)tr.Rotation.Y);
            bw.Write((float)tr.Rotation.Z);
            bw.Write((float)tr.Scale.X);
            bw.Write((float)tr.Scale.Y);
            bw.Write((float)tr.Scale.Z);
        }

        static void BroadcastDespawnPacket(uint netId)
        {
            if (_transport == null) return;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.Despawn);
            bw.Write(netId);
            _transport.SendToAll(ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        static void HandleSpawnMessage(BinaryReader br)
        {
            try
            {
                uint netId = br.ReadUInt32();
                string prefabKey = br.ReadString();
                if (prefabKey.Length > MaxSpawnPrefabKeyChars)
                    return;
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float rx = br.ReadSingle(), ry = br.ReadSingle(), rz = br.ReadSingle();
                float sx = br.ReadSingle(), sy = br.ReadSingle(), sz = br.ReadSingle();

                if (_networkedObjects.ContainsKey(netId))
                {
                    Log.Warning($"[NetworkManager] Spawn ignored for existing NetworkId {netId}");
                    return;
                }
                if (!_spawnFactories.TryGetValue(prefabKey, out var factory))
                {
                    Log.Error($"[NetworkManager] Client spawn: unknown prefab key '{prefabKey}' (register with RegisterSpawnPrefab on the client).");
                    return;
                }

                GameObject go;
                try { go = factory(); }
                catch (Exception ex)
                {
                    Log.Error($"[NetworkManager] Client spawn factory failed: {ex.Message}");
                    return;
                }
                if (go == null) return;

                go.Transform.Position = new Vector3(px, py, pz);
                go.Transform.Rotation = new Vector3(rx, ry, rz);
                go.Transform.Scale = new Vector3(sx, sy, sz);

                var ni = new NetworkIdentity { NetworkId = netId };
                go.AddBehavior(ni);
                SceneService.Add(go);
            }
            catch (Exception ex)
            {
                Log.Warning($"[NetworkManager] Spawn parse error: {ex.Message}");
            }
        }

        static void HandleDespawnMessage(BinaryReader br)
        {
            try
            {
                uint netId = br.ReadUInt32();
                if (!_networkedObjects.TryGetValue(netId, out var identity)) return;
                var go = identity.gameObject;
                if (go != null)
                    DestroyNetworkedGameObject(go);
            }
            catch (Exception ex)
            {
                Log.Warning($"[NetworkManager] Despawn error: {ex.Message}");
            }
        }

        static void DestroyNetworkedGameObject(GameObject go)
        {
            foreach (var child in go.Children.ToList())
                DestroyNetworkedGameObject(child);
            foreach (var b in go.Behaviors.ToList())
            {
                try { b.__OnDestroy(); } catch { /* ignore */ }
            }
            if (go.Parent == null)
                SceneService.Remove(go);
            else
                go.RemoveFromParent();
            SceneService.NotifyChanged();
        }

        /// <summary>Server receives client input payloads (e.g. movement vectors). Only invoked when <see cref="IsServer"/>.</summary>
        public static void RegisterClientInputHandler(Action<int, byte[]>? handler) => _clientInputHandler = handler;

        /// <summary>Client sends a reliable payload to the server (peer 0). Server dispatches via <see cref="RegisterClientInputHandler"/>.</summary>
        public static void SendClientInputToServer(byte[] data)
        {
            if (!IsClient || _transport == null || data == null || data.Length == 0) return;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.ClientInput);
            bw.Write(data.Length);
            bw.Write(data);
            _transport.Send(0, ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        static void HandleClientInputMessage(int peerId, BinaryReader br)
        {
            try
            {
                int len = br.ReadInt32();
                if (len < 0 || len > 1024 * 1024) return;
                byte[] payload = br.ReadBytes(len);
                _clientInputHandler?.Invoke(peerId, payload);
            }
            catch (Exception ex)
            {
                Log.Warning($"[NetworkManager] ClientInput error: {ex.Message}");
            }
        }
    }
}
