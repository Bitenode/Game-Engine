#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Game_Engine.Core.Networking
{
    /// <summary>
    /// Fragmented server→client surface payloads and client→server chunk requests (planet meshes, terrain tiles).
    /// </summary>
    public static partial class NetworkManager
    {
        /// <summary>Planet quadtree leaf mesh (TransvoxelMeshData binary).</summary>
        public const ushort SurfaceKindPlanetChunk = 1;

        /// <summary>Terrain streamer tile heightmap / layer payload.</summary>
        public const ushort SurfaceKindTerrainTile = 2;

        /// <summary>Max assembled surface payload per transfer (reassembly cap).</summary>
        public const int MaxSurfaceChunkAssembledBytes = 16 * 1024 * 1024;

        /// <summary>Payload bytes per fragment (excluding variable header).</summary>
        internal const int SurfaceFragmentPayloadMax = 48 * 1024;

        /// <summary>Server: max inbound surface chunk request messages per peer per second.</summary>
        public static int MaxSurfaceChunkRequestsPerPeerPerSecond { get; set; } = 80;

        /// <summary>
        /// Server-only: handle a client's chunk request. Return null to deny or if generation failed.
        /// Peer id is the requesting client; key is opaque (surface-kind-specific).
        /// </summary>
        public static Func<int, ushort, uint, byte[], byte[]?>? SurfaceChunkRequestHandler { get; set; }

        static void ResetSurfaceStreamingState()
        {
            SurfaceChunkRequestHandler = null;
            _pendingSurfaceAssemblies.Clear();
            _clientSurfaceCallbacks.Clear();
            _nextClientSurfaceRequestId = 1;
        }

        static int _nextClientSurfaceRequestId = 1;

        sealed class SurfaceAssembly
        {
            public ushort SurfaceKind;
            public uint RequestId;
            public int FragmentCount;
            public int TotalPayloadLength;
            public byte[]? Buffer;
            public int FragmentsReceived;
        }

        static readonly Dictionary<uint, SurfaceAssembly> _pendingSurfaceAssemblies = new();
        static readonly Dictionary<uint, Action<uint, ushort, byte[]?>> _clientSurfaceCallbacks = new();

        /// <summary>
        /// Client: enqueue a surface chunk request. <paramref name="onComplete"/> is invoked when the full payload arrives or on error (null payload).
        /// Called from the same thread as <see cref="Update"/> (main thread) after the transport delivers fragments.
        /// </summary>
        public static void RequestSurfaceChunk(ushort surfaceKind, byte[] key, Action<uint, ushort, byte[]?> onComplete)
        {
            if (!IsClient || _transport == null)
            {
                onComplete(0, surfaceKind, null);
                return;
            }

            uint requestId = (uint)Interlocked.Increment(ref _nextClientSurfaceRequestId);
            _clientSurfaceCallbacks[requestId] = onComplete;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);
            bw.Write((byte)NetMessageType.SurfaceChunkRequest);
            bw.Write(surfaceKind);
            bw.Write(requestId);
            bw.Write(key.Length);
            bw.Write(key);

            _transport.Send(0, ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
        }

        /// <summary>
        /// Server: send an assembled payload to one peer as one or more reliable fragments.
        /// </summary>
        public static void SendSurfaceChunkResponse(int peerId, ushort surfaceKind, uint requestId, byte[]? payload)
        {
            if (!IsServer || _transport == null) return;

            payload ??= Array.Empty<byte>();
            if (payload.Length > MaxSurfaceChunkAssembledBytes)
            {
                Log.Warning($"[Network] Surface chunk response too large ({payload.Length} > {MaxSurfaceChunkAssembledBytes})");
                payload = Array.Empty<byte>();
            }

            int total = payload.Length;
            int fragSize = SurfaceFragmentPayloadMax;
            int fragCount = total == 0 ? 1 : (int)Math.Ceiling((double)total / fragSize);
            if (fragCount > ushort.MaxValue)
            {
                Log.Warning("[Network] Surface chunk fragment count overflow");
                return;
            }

            for (int fi = 0; fi < fragCount; fi++)
            {
                int offset = fi * fragSize;
                int len = Math.Min(fragSize, total - offset);
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.UTF8);
                bw.Write((byte)NetMessageType.SurfaceChunkData);
                bw.Write(surfaceKind);
                bw.Write(requestId);
                bw.Write((ushort)fi);
                bw.Write((ushort)fragCount);
                if (fi == 0)
                    bw.Write(total);
                bw.Write(payload, offset, len);

                _transport.Send(peerId, ms.ToArray(), 1, DeliveryMode.ReliableOrdered);
            }
        }

        static void HandleSurfaceChunkRequest(int peerId, BinaryReader br)
        {
            try
            {
                ushort surfaceKind = br.ReadUInt16();
                uint requestId = br.ReadUInt32();
                int keyLen = br.ReadInt32();
                if (keyLen < 0 || keyLen > MaxSurfaceChunkAssembledBytes)
                {
                    Log.Warning($"[Network] SurfaceChunkRequest invalid key length {keyLen}");
                    return;
                }

                byte[] key = br.ReadBytes(keyLen);
                var handler = SurfaceChunkRequestHandler;
                byte[]? payload = handler != null ? handler(peerId, surfaceKind, requestId, key) : null;
                SendSurfaceChunkResponse(peerId, surfaceKind, requestId, payload);
            }
            catch (Exception ex)
            {
                Log.Warning($"[Network] SurfaceChunkRequest error: {ex.Message}");
            }
        }

        static void HandleSurfaceChunkData(BinaryReader br)
        {
            try
            {
                ushort surfaceKind = br.ReadUInt16();
                uint requestId = br.ReadUInt32();
                ushort fragIndex = br.ReadUInt16();
                ushort fragCount = br.ReadUInt16();
                int totalPayloadLength = 0;
                if (fragIndex == 0)
                    totalPayloadLength = br.ReadInt32();
                int remaining = (int)(br.BaseStream.Length - br.BaseStream.Position);
                byte[] fragPayload = remaining > 0 ? br.ReadBytes(remaining) : Array.Empty<byte>();

                if (fragCount == 0 || fragIndex >= fragCount)
                    return;

                if (!_pendingSurfaceAssemblies.TryGetValue(requestId, out var asm))
                {
                    asm = new SurfaceAssembly { RequestId = requestId, SurfaceKind = surfaceKind };
                    _pendingSurfaceAssemblies[requestId] = asm;
                }

                if (fragIndex == 0)
                {
                    if (totalPayloadLength < 0 || totalPayloadLength > MaxSurfaceChunkAssembledBytes)
                    {
                        _pendingSurfaceAssemblies.Remove(requestId);
                        TryInvokeClientSurfaceCallback(requestId, surfaceKind, null);
                        return;
                    }

                    asm.TotalPayloadLength = totalPayloadLength;
                    asm.FragmentCount = fragCount;
                    asm.Buffer = totalPayloadLength == 0 ? Array.Empty<byte>() : new byte[totalPayloadLength];
                    asm.FragmentsReceived = 0;
                }

                if (asm.Buffer == null || asm.FragmentCount != fragCount)
                {
                    _pendingSurfaceAssemblies.Remove(requestId);
                    TryInvokeClientSurfaceCallback(requestId, surfaceKind, null);
                    return;
                }

                int offset = fragIndex * SurfaceFragmentPayloadMax;
                if (offset + fragPayload.Length > asm.Buffer.Length)
                {
                    _pendingSurfaceAssemblies.Remove(requestId);
                    TryInvokeClientSurfaceCallback(requestId, surfaceKind, null);
                    return;
                }

                if (fragPayload.Length > 0)
                    Buffer.BlockCopy(fragPayload, 0, asm.Buffer, offset, fragPayload.Length);
                asm.FragmentsReceived++;

                if (asm.FragmentsReceived < fragCount)
                    return;

                _pendingSurfaceAssemblies.Remove(requestId);
                TryInvokeClientSurfaceCallback(requestId, surfaceKind, asm.Buffer);
            }
            catch (Exception ex)
            {
                Log.Warning($"[Network] SurfaceChunkData error: {ex.Message}");
            }
        }

        static void TryInvokeClientSurfaceCallback(uint requestId, ushort surfaceKind, byte[]? payload)
        {
            if (!_clientSurfaceCallbacks.TryGetValue(requestId, out var cb))
                return;
            _clientSurfaceCallbacks.Remove(requestId);
            try { cb(requestId, surfaceKind, payload); }
            catch (Exception ex)
            {
                Log.Warning($"[Network] Surface chunk callback error: {ex.Message}");
            }
        }
    }
}
