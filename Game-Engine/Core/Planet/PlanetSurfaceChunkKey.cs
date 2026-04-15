using System;
using System.IO;

namespace Game_Engine.Core.Planet;

/// <summary>Binary key for <see cref="Game_Engine.Core.Networking.NetworkManager.SurfaceKindPlanetChunk"/> requests.</summary>
public static class PlanetSurfaceChunkKey
{
    const byte Version = 1;

    /// <summary>
    /// <paramref name="planetNetId"/> is <see cref="Game_Engine.Core.Component.NetworkIdentity.NetworkId"/> on the planet (0 if none).
    /// </summary>
    public static byte[] Encode(uint planetNetId, int face, int lodLevel, float u0, float v0, float u1, float v1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        w.Write(planetNetId);
        w.Write((byte)Math.Clamp(face, 0, 255));
        w.Write(lodLevel);
        w.Write(u0);
        w.Write(v0);
        w.Write(u1);
        w.Write(v1);
        return ms.ToArray();
    }

    public static bool TryDecode(byte[] key, out uint planetNetId, out int face, out int lodLevel, out float u0, out float v0, out float u1, out float v1)
    {
        planetNetId = 0;
        face = 0;
        lodLevel = 0;
        u0 = v0 = u1 = v1 = 0;
        if (key == null || key.Length < 22) return false;
        try
        {
            using var ms = new MemoryStream(key);
            using var r = new BinaryReader(ms);
            if (r.ReadByte() != Version) return false;
            planetNetId = r.ReadUInt32();
            face = r.ReadByte();
            lodLevel = r.ReadInt32();
            u0 = r.ReadSingle();
            v0 = r.ReadSingle();
            u1 = r.ReadSingle();
            v1 = r.ReadSingle();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
