#nullable enable
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Networking
{
    /// <summary>Routes <see cref="NetworkManager.SurfaceChunkRequestHandler"/> to planet / terrain implementations.</summary>
    internal static class NetworkSurfaceDispatch
    {
        internal static void AttachServerHandler()
        {
            NetworkManager.SurfaceChunkRequestHandler = Handle;
        }

        static byte[]? Handle(int peerId, ushort kind, uint requestId, byte[] key)
        {
            if (kind == NetworkManager.SurfaceKindPlanetChunk)
                return PlanetTerrain.HandlePlanetChunkRequestForServer(key);
            if (kind == NetworkManager.SurfaceKindTerrainTile)
                return TerrainStreamer.HandleTerrainTileRequestForServer(peerId, key);
            return null;
        }
    }
}
