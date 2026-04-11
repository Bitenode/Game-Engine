#nullable enable
using System;
using System.IO;
using Game_Engine.Core.Networking;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// Network identity component — identifies a GameObject on the network.
    /// Must be attached to any GameObject that participates in networking.
    /// Handles state serialization/deserialization for network synchronization.
    /// For multiplayer, set <see cref="NetworkId"/> to a stable non-zero value in the scene so every peer maps the same object;
    /// use <see cref="NetworkGameplayRules"/> for authoritative vs client-local gameplay.
    /// </summary>
    [ComponentCategory("Networking")]
    public sealed class NetworkIdentity : Behavior
    {
        /// <summary>
        /// Unique ID for replication. Prefer a stable non-zero value saved in the scene; if zero while networking is active,
        /// <see cref="NetworkManager.RegisterObject"/> may auto-assign (risky if registration order differs between peers).
        /// </summary>
        [Persist] public uint NetworkId { get; set; }

        /// <summary>True if this object is owned by the local player.</summary>
        [Persist] public bool IsLocalPlayer { get; set; }

        /// <summary>Peer ID of the owner (-1 = server).</summary>
        [Persist] public int OwnerPeerId { get; set; } = -1;

        /// <summary>Has authority to make changes (server or owner).</summary>
        public bool HasAuthority => NetworkManager.IsServer || IsLocalPlayer;

        public override void OnEnable()
        {
            base.OnEnable();
            NetworkManager.RegisterObject(this);
        }

        public override void OnDisable()
        {
            NetworkManager.UnregisterObject(this);
            base.OnDisable();
        }

        /// <summary>
        /// Serialize the state of this object's synced components.
        /// Called by the server to broadcast state.
        /// </summary>
        public byte[] SerializeState()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Serialize transform
            var t = Transform;
            bw.Write((float)t.Position.X);
            bw.Write((float)t.Position.Y);
            bw.Write((float)t.Position.Z);
            bw.Write((float)t.Rotation.X);
            bw.Write((float)t.Rotation.Y);
            bw.Write((float)t.Rotation.Z);
            bw.Write((float)t.Scale.X);
            bw.Write((float)t.Scale.Y);
            bw.Write((float)t.Scale.Z);

            // Serialize NetworkTransform if present
            var netTransform = GetComponent<NetworkTransform>();
            if (netTransform != null)
            {
                bw.Write(true);
                bw.Write(netTransform.InterpolationSpeed);
            }
            else
            {
                bw.Write(false);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Deserialize state received from the network.
        /// Called by clients to apply server state.
        /// </summary>
        public void DeserializeState(byte[] data)
        {
            if (HasAuthority) return; // Don't overwrite our own state

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            try
            {
                float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                float rx = br.ReadSingle(), ry = br.ReadSingle(), rz = br.ReadSingle();
                float sx = br.ReadSingle(), sy = br.ReadSingle(), sz = br.ReadSingle();

                // Apply via NetworkTransform for interpolation, or directly
                var netTransform = GetComponent<NetworkTransform>();
                if (netTransform != null)
                {
                    netTransform.SetTargetState(
                        new Vector3(px, py, pz),
                        new Vector3(rx, ry, rz),
                        new Vector3(sx, sy, sz));
                }
                else
                {
                    Transform.Position = new Vector3(px, py, pz);
                    Transform.Rotation = new Vector3(rx, ry, rz);
                    Transform.Scale = new Vector3(sx, sy, sz);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NetworkIdentity] Deserialize error: {ex.Message}");
            }
        }
    }
}
