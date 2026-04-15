#nullable enable
using Game_Engine.Core.Component;

namespace Game_Engine.Core.Networking
{
    /// <summary>
    /// Helpers for deciding where authoritative simulation, input, and replication apply in multiplayer.
    /// Terrain/planet can be file-backed (same assets on every peer) or <strong>server-streamed</strong> when
    /// <see cref="PlanetTerrain.StreamSurfaceFromServerWhenClient"/> / <see cref="TerrainStreamer.StreamTilesFromServerWhenClient"/>
    /// are used on clients — only the server runs procedural mesh/tile generation. Use these rules for players and other
    /// <see cref="NetworkIdentity"/> objects.
    /// </summary>
    public static class NetworkGameplayRules
    {
        /// <summary>
        /// True when this process should run authoritative world rules: offline play, or the server peer.
        /// Use for physics, hit detection, pickups, and other server-authoritative gameplay when networked.
        /// </summary>
        public static bool IsAuthoritativePeer =>
            !NetworkManager.IsActive || NetworkManager.IsServer;

        /// <summary>
        /// True when this identity represents another peer's object on a client (state comes from replication).
        /// </summary>
        public static bool IsRemoteProxy(NetworkIdentity identity) =>
            NetworkManager.IsActive && NetworkManager.IsClient && !identity.HasAuthority;

        /// <summary>
        /// True when local input should drive this object (local player on a client, or any controlled object offline/single-player).
        /// </summary>
        public static bool IsLocallyControlledPlayer(NetworkIdentity identity) =>
            NetworkManager.IsActive && identity.IsLocalPlayer && identity.HasAuthority;
    }
}
