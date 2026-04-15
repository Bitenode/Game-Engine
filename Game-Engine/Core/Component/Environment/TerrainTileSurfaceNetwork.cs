#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Game_Engine.Core.Networking;

namespace Game_Engine.Core.Component;

/// <summary>
/// Keys and payloads for <see cref="NetworkManager.SurfaceKindTerrainTile"/> (server-authoritative terrain tiles).
/// </summary>
public static class TerrainTileSurfaceNetwork
{
    const byte KeyVersion = 1;
    const byte PayloadVersion = 1;

    public static byte[] EncodeKey(uint streamerNetId, int tx, int tz)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(KeyVersion);
        w.Write(streamerNetId);
        w.Write(tx);
        w.Write(tz);
        return ms.ToArray();
    }

    public static bool TryDecodeKey(byte[] key, out uint streamerNetId, out int tx, out int tz)
    {
        streamerNetId = 0;
        tx = tz = 0;
        if (key == null || key.Length < 13) return false;
        try
        {
            using var ms = new MemoryStream(key);
            using var r = new BinaryReader(ms);
            if (r.ReadByte() != KeyVersion) return false;
            streamerNetId = r.ReadUInt32();
            tx = r.ReadInt32();
            tz = r.ReadInt32();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static uint GetStreamerNetId(TerrainStreamer s)
    {
        if (s.gameObject == null) return 0;
        foreach (var b in s.gameObject.Behaviors)
        {
            if (b is NetworkIdentity ni)
                return ni.NetworkId;
        }
        return 0;
    }

    /// <summary>Server: build tile payload for the given streamer tile coordinates.</summary>
    public static byte[]? TryBuildTilePayload(int peerId, byte[] key)
    {
        _ = peerId;
        if (!TryDecodeKey(key, out uint netId, out int tx, out int tz))
            return null;

        foreach (var streamer in SceneQuery.FindBehaviors<TerrainStreamer>())
        {
            if (!streamer.IsActiveAndEnabled) continue;
            uint sid = GetStreamerNetId(streamer);
            if (netId != 0)
            {
                if (sid != netId) continue;
            }
            else
            {
                if (sid != 0) continue;
            }

            string rel = streamer.BuildTileRelativePath(tx, tz);
            var proj = ProjectService.Current;
            if (proj == null) return null;
            string abs = Path.Combine(proj.RootPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                // Flat default matching streamer settings
                return BuildPayloadFromDefaults(streamer, tx, tz);
            }

            if (abs.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var text = File.ReadAllText(abs);
                    var dto = JsonSerializer.Deserialize<TerrainTileNetworkDto>(text);
                    if (dto?.Heights == null) return null;
                    return SerializePayload(dto);
                }
                catch
                {
                    return null;
                }
            }

            if (abs.EndsWith(".terrain.bin", StringComparison.OrdinalIgnoreCase))
                return TryPayloadFromGterBinary(abs);

            return null;
        }

        return null;
    }

    static byte[] BuildPayloadFromDefaults(TerrainStreamer streamer, int tx, int tz)
    {
        _ = tx;
        _ = tz;
        int rx = Math.Max(2, streamer.TileResolutionX);
        int rz = Math.Max(2, streamer.TileResolutionZ);
        float T = Math.Max(0.001f, streamer.TileWorldSize);
        int n = rx * rz;
        var dto = new TerrainTileNetworkDto
        {
            ResX = rx,
            ResZ = rz,
            SizeX = T,
            SizeZ = T,
            HeightScale = streamer.TileHeightScale,
            Heights = Enumerable.Repeat(0.5f, n).ToArray(),
        };
        return SerializePayload(dto);
    }

    static byte[]? TryPayloadFromGterBinary(string abs)
    {
        try
        {
            using var fs = File.OpenRead(abs);
            Span<byte> m = stackalloc byte[4];
            if (fs.Read(m) != 4 || m[0] != (byte)'G' || m[1] != (byte)'T' || m[2] != (byte)'E' || m[3] != (byte)'R')
                return null;
            int ver = ReadI32(fs);
            if (ver != 2) return null;
            int rx = ReadI32(fs);
            int rz = ReadI32(fs);
            float sx = ReadF32(fs);
            float sz = ReadF32(fs);
            float hs = ReadF32(fs);
            uint flags = ReadU32Local(fs);
            int chunkSz = ReadI32(fs);
            int ub = fs.ReadByte();
            if (ub < 0) return null;
            bool useChunk = ub != 0;
            int lodB = fs.ReadByte();
            if (lodB < 0) return null;
            int lodLv = lodB;
            int colStep = ReadI32(fs);
            float lodN = ReadF32(fs);
            float lodM = ReadF32(fs);
            float lodH = ReadF32(fs);

            int rx2 = Math.Max(2, rx);
            int rz2 = Math.Max(2, rz);
            int need = rx2 * rz2;
            var heights = new float[need];
            if (!ReadF32Array(fs, heights, need)) return null;

            bool[]? holes = null;
            if ((flags & 1) != 0)
            {
                holes = new bool[need];
                for (int i = 0; i < need; i++)
                {
                    int b = fs.ReadByte();
                    if (b < 0) return null;
                    holes[i] = b != 0;
                }
            }

            if ((flags & 2) != 0)
            {
                var tmp = new float[need * 4];
                if (!ReadF32Array(fs, tmp, need * 4)) return null;
            }
            if ((flags & 4) != 0)
            {
                var tmp = new float[need * 4];
                if (!ReadF32Array(fs, tmp, need * 4)) return null;
            }
            if ((flags & 8) != 0)
            {
                int jsonLen = ReadI32(fs);
                long rem = fs.Length - fs.Position;
                if (jsonLen < 0 || jsonLen > rem || jsonLen > 64 * 1024 * 1024) return null;
                fs.Position += jsonLen;
            }

            var dto = new TerrainTileNetworkDto
            {
                ResX = rx2,
                ResZ = rz2,
                SizeX = sx,
                SizeZ = sz,
                HeightScale = hs,
                Heights = heights,
                Holes = holes,
                ChunkSize = chunkSz >= 65 ? chunkSz : null,
                UseChunking = useChunk,
                LodLevels = lodLv >= 1 ? lodLv : null,
                CollisionLodStep = colStep >= 1 ? colStep : null,
                LodDistanceNearChunks = float.IsFinite(lodN) && lodN > 0f ? lodN : null,
                LodDistanceMidChunks = float.IsFinite(lodM) && lodM > 0f ? lodM : null,
                LodHysteresisWorld = float.IsFinite(lodH) && lodH >= 0f ? lodH : null,
            };
            return SerializePayload(dto);
        }
        catch
        {
            return null;
        }

        static int ReadI32(FileStream s)
        {
            Span<byte> b = stackalloc byte[4];
            if (s.Read(b) != 4) throw new EndOfStreamException();
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b);
        }

        static uint ReadU32Local(FileStream s)
        {
            Span<byte> b = stackalloc byte[4];
            if (s.Read(b) != 4) throw new EndOfStreamException();
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        static float ReadF32(FileStream s)
        {
            Span<byte> b = stackalloc byte[4];
            if (s.Read(b) != 4) throw new EndOfStreamException();
            return System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(b);
        }

        static bool ReadF32Array(FileStream s, float[] arr, int count)
        {
            Span<byte> b = stackalloc byte[4];
            for (int i = 0; i < count; i++)
            {
                if (s.Read(b) != 4) return false;
                arr[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(b);
            }
            return true;
        }
    }

    static byte[] SerializePayload(TerrainTileNetworkDto dto)
    {
        var json = JsonSerializer.Serialize(dto);
        var utf8 = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(PayloadVersion);
        w.Write(utf8.Length);
        w.Write(utf8);
        return ms.ToArray();
    }

    /// <summary>Apply a server payload to a <see cref="Terrain"/> (client).</summary>
    public static bool TryApplyPayloadToTerrain(Terrain terr, byte[]? payload)
    {
        if (payload == null || payload.Length < 3) return false;
        try
        {
            using var ms = new MemoryStream(payload);
            using var r = new BinaryReader(ms);
            if (r.ReadByte() != PayloadVersion) return false;
            int len = r.ReadInt32();
            if (len < 0 || len > NetworkManager.MaxSurfaceChunkAssembledBytes) return false;
            var utf8 = r.ReadBytes(len);
            var json = Encoding.UTF8.GetString(utf8);
            var dto = JsonSerializer.Deserialize<TerrainTileNetworkDto>(json);
            if (dto?.Heights == null) return false;

            terr.ResX = Math.Max(2, dto.ResX);
            terr.ResZ = Math.Max(2, dto.ResZ);
            terr.SizeX = dto.SizeX;
            terr.SizeZ = dto.SizeZ;
            terr.HeightScale = dto.HeightScale;
            int need = terr.ResX * terr.ResZ;
            if (dto.Heights.Length != need) return false;
            terr.Heights = (float[])dto.Heights.Clone();
            terr.Holes = (dto.Holes != null && dto.Holes.Length == need) ? dto.Holes : null;
            if (dto.ChunkSize is >= 3) terr.ChunkSize = dto.ChunkSize.Value;
            if (dto.UseChunking.HasValue) terr.UseChunking = dto.UseChunking.Value;
            if (dto.LodLevels is >= 1 and <= 8) terr.LodLevels = dto.LodLevels.Value;
            if (dto.CollisionLodStep is >= 1) terr.CollisionLodStep = dto.CollisionLodStep.Value;
            if (dto.LodDistanceNearChunks is float f1 && float.IsFinite(f1) && f1 > 0f) terr.LodDistanceNearChunks = f1;
            if (dto.LodDistanceMidChunks is float f2 && float.IsFinite(f2) && f2 > 0f) terr.LodDistanceMidChunks = f2;
            if (dto.LodHysteresisWorld is float fh && float.IsFinite(fh) && fh >= 0f) terr.LodHysteresisWorld = fh;
            terr.RebuildMesh();
            SceneService.NotifyChanged();
            return true;
        }
        catch
        {
            return false;
        }
    }

    sealed class TerrainTileNetworkDto
    {
        public int ResX { get; set; }
        public int ResZ { get; set; }
        public float SizeX { get; set; }
        public float SizeZ { get; set; }
        public float HeightScale { get; set; }
        public float[]? Heights { get; set; }
        public bool[]? Holes { get; set; }
        public int? ChunkSize { get; set; }
        public bool? UseChunking { get; set; }
        public int? LodLevels { get; set; }
        public int? CollisionLodStep { get; set; }
        public float? LodDistanceNearChunks { get; set; }
        public float? LodDistanceMidChunks { get; set; }
        public float? LodHysteresisWorld { get; set; }
    }
}
