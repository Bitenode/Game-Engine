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
        /// <summary>1 = full float payload; 2 = quantized int16 (smaller bandwidth). Must match on server and clients.</summary>
        public static byte StatePayloadFormatVersion { get; set; } = 1;

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

            byte ver = StatePayloadFormatVersion;
            if (ver != 1 && ver != 2) ver = 1;
            bw.Write(ver);

            if (ver == 1)
                SerializeStateV1(bw);
            else
                SerializeStateV2Quantized(bw);

            return ms.ToArray();
        }

        void SerializeStateV1(BinaryWriter bw)
        {
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

            var netTransform = GetComponent<NetworkTransform>();
            if (netTransform != null)
            {
                bw.Write(true);
                bw.Write(netTransform.InterpolationSpeed);
            }
            else
                bw.Write(false);
        }

        void SerializeStateV2Quantized(BinaryWriter bw)
        {
            var t = Transform;
            WriteQuantized(bw, t.Position.X);
            WriteQuantized(bw, t.Position.Y);
            WriteQuantized(bw, t.Position.Z);
            WriteQuantized(bw, t.Rotation.X);
            WriteQuantized(bw, t.Rotation.Y);
            WriteQuantized(bw, t.Rotation.Z);
            WriteQuantized(bw, t.Scale.X);
            WriteQuantized(bw, t.Scale.Y);
            WriteQuantized(bw, t.Scale.Z);

            var netTransform = GetComponent<NetworkTransform>();
            if (netTransform != null)
            {
                bw.Write(true);
                bw.Write(netTransform.InterpolationSpeed);
            }
            else
                bw.Write(false);
        }

        static void WriteQuantized(BinaryWriter bw, double d)
        {
            int q = (int)System.Math.Clamp(d * 1000.0, -32768, 32767);
            bw.Write((short)q);
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
                byte ver = br.ReadByte();
                if (ver == 1)
                    DeserializeV1(br);
                else if (ver == 2)
                    DeserializeV2(br);
                else
                    Log.Warning($"[NetworkIdentity] Unknown state format {ver}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[NetworkIdentity] Deserialize error: {ex.Message}");
            }
        }

        void DeserializeV1(BinaryReader br)
        {
            float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
            float rx = br.ReadSingle(), ry = br.ReadSingle(), rz = br.ReadSingle();
            float sx = br.ReadSingle(), sy = br.ReadSingle(), sz = br.ReadSingle();
            ApplyNetworkTransformOrDirect(px, py, pz, rx, ry, rz, sx, sy, sz);
            ReadNetworkTransformTail(br);
        }

        void DeserializeV2(BinaryReader br)
        {
            double px = ReadDequantized(br);
            double py = ReadDequantized(br);
            double pz = ReadDequantized(br);
            double rx = ReadDequantized(br);
            double ry = ReadDequantized(br);
            double rz = ReadDequantized(br);
            double sx = ReadDequantized(br);
            double sy = ReadDequantized(br);
            double sz = ReadDequantized(br);
            ApplyNetworkTransformOrDirect(px, py, pz, rx, ry, rz, sx, sy, sz);
            ReadNetworkTransformTail(br);
        }

        static double ReadDequantized(BinaryReader br) => br.ReadInt16() / 1000.0;

        void ApplyNetworkTransformOrDirect(double px, double py, double pz, double rx, double ry, double rz, double sx, double sy, double sz)
        {
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

        void ReadNetworkTransformTail(BinaryReader br)
        {
            bool hasNt = br.ReadBoolean();
            if (hasNt)
                br.ReadSingle(); // InterpolationSpeed — mirror SerializeState; not applied on client deserialize path for NT extras
        }
    }
}
