#nullable enable
using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Game_Engine.Core.Component
{
    /// <summary>
    /// A single tile in a tilemap.
    /// </summary>
    public struct TileData
    {
        /// <summary>Tile ID (-1 = empty/no tile).</summary>
        public int TileId;

        /// <summary>Tile color tint.</summary>
        public Color Color;

        /// <summary>Flip flags.</summary>
        public SpriteFlip Flip;

        /// <summary>Rotation in 90-degree increments (0, 1, 2, 3).</summary>
        public byte Rotation;

        /// <summary>Custom collision flag.</summary>
        public bool HasCollision;

        public static TileData Empty => new() { TileId = -1, Color = Colors.White };
    }

    /// <summary>
    /// Tileset — defines how tile IDs map to sprite atlas UV regions.
    /// </summary>
    public sealed class Tileset
    {
        public string Name { get; set; } = "Tileset";
        public string TexturePath { get; set; } = "";
        public int TileWidth { get; set; } = 32;
        public int TileHeight { get; set; } = 32;
        public int Columns { get; set; } = 16;
        public int Rows { get; set; } = 16;
        public int Spacing { get; set; } = 0;   // Pixels between tiles
        public int Margin { get; set; } = 0;     // Pixels around the edge

        /// <summary>Get the UV region for a tile ID.</summary>
        public (float uvX, float uvY, float uvW, float uvH) GetTileUV(int tileId)
        {
            if (tileId < 0 || Columns <= 0 || Rows <= 0) return (0, 0, 0, 0);

            int col = tileId % Columns;
            int row = tileId / Columns;

            float texW = Columns * TileWidth + (Columns - 1) * Spacing + 2 * Margin;
            float texH = Rows * TileHeight + (Rows - 1) * Spacing + 2 * Margin;

            float uvX = (Margin + col * (TileWidth + Spacing)) / texW;
            float uvY = (Margin + row * (TileHeight + Spacing)) / texH;
            float uvW = TileWidth / texW;
            float uvH = TileHeight / texH;

            return (uvX, uvY, uvW, uvH);
        }

        /// <summary>Total number of tiles in this tileset.</summary>
        public int TileCount => Columns * Rows;
    }

    /// <summary>
    /// Tilemap component — renders a grid of 2D tiles for level design.
    /// Supports multiple layers, auto-tiling, and collision generation.
    /// Uses a sparse representation (only stores non-empty tiles).
    /// </summary>
    [ComponentCategory("2D")]
    public sealed class Tilemap : Behavior
    {
        // ── Configuration ──
        /// <summary>Width of each cell in world units.</summary>
        [Persist] public float CellSize { get; set; } = 1f;

        /// <summary>Grid width in cells.</summary>
        [Persist] public int Width { get; set; } = 32;

        /// <summary>Grid height in cells.</summary>
        [Persist] public int Height { get; set; } = 32;

        /// <summary>Sorting layer name.</summary>
        [Persist] public string SortingLayer { get; set; } = "Default";

        /// <summary>Sorting order within the layer.</summary>
        [Persist] public int SortingOrder { get; set; } = 0;

        /// <summary>Path to the tileset texture.</summary>
        [Persist] public string TilesetPath { get; set; } = "";

        /// <summary>Number of tile columns in the tileset texture.</summary>
        [Persist] public int TilesetColumns { get; set; } = 16;

        /// <summary>Number of tile rows in the tileset texture.</summary>
        [Persist] public int TilesetRows { get; set; } = 16;

        /// <summary>Tint color for the entire tilemap.</summary>
        [Persist] public Color TintColor { get; set; } = Colors.White;

        // ── Tile data (sparse storage) ──
        private readonly Dictionary<(int x, int y), TileData> _tiles = new();

        /// <summary>Tileset associated with this tilemap.</summary>
        public Tileset? Tileset { get; set; }

        // ── Static registry ──
        private static readonly List<Tilemap> _all = new(16);
        public static IReadOnlyList<Tilemap> All => _all;

        public override void OnEnable()
        {
            base.OnEnable();
            if (!_all.Contains(this)) _all.Add(this);
        }

        public override void OnDisable()
        {
            _all.Remove(this);
            base.OnDisable();
        }

        // ── Tile operations ──

        /// <summary>Set a tile at the given grid position.</summary>
        public void SetTile(int x, int y, int tileId, Color? color = null, bool collision = false)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            _tiles[(x, y)] = new TileData
            {
                TileId = tileId,
                Color = color ?? Colors.White,
                HasCollision = collision
            };
        }

        /// <summary>Get the tile at the given grid position.</summary>
        public TileData GetTile(int x, int y)
        {
            return _tiles.TryGetValue((x, y), out var tile) ? tile : TileData.Empty;
        }

        /// <summary>Remove (clear) a tile at the given grid position.</summary>
        public void ClearTile(int x, int y)
        {
            _tiles.Remove((x, y));
        }

        /// <summary>Clear all tiles.</summary>
        public void ClearAll()
        {
            _tiles.Clear();
        }

        /// <summary>Get all non-empty tile positions.</summary>
        public IEnumerable<(int x, int y, TileData tile)> GetAllTiles()
        {
            foreach (var ((x, y), tile) in _tiles)
                yield return (x, y, tile);
        }

        /// <summary>Number of non-empty tiles.</summary>
        public int TileCount => _tiles.Count;

        /// <summary>Convert a grid position to world space.</summary>
        public Vector3 GridToWorld(int gridX, int gridY)
        {
            var pos = Transform.Position;
            return new Vector3(
                pos.X + gridX * CellSize,
                pos.Y + gridY * CellSize,
                pos.Z);
        }

        /// <summary>Convert a world position to the nearest grid cell.</summary>
        public (int x, int y) WorldToGrid(float worldX, float worldY)
        {
            var pos = Transform.Position;
            int gx = (int)MathF.Floor((worldX - (float)pos.X) / CellSize);
            int gy = (int)MathF.Floor((worldY - (float)pos.Y) / CellSize);
            return (gx, gy);
        }

        /// <summary>Check if a grid position has a collision tile.</summary>
        public bool HasCollisionAt(int x, int y)
        {
            return _tiles.TryGetValue((x, y), out var tile) && tile.HasCollision;
        }

        /// <summary>
        /// Fill a rectangular region with the same tile.
        /// </summary>
        public void FillRect(int x, int y, int width, int height, int tileId, bool collision = false)
        {
            for (int dy = 0; dy < height; dy++)
                for (int dx = 0; dx < width; dx++)
                    SetTile(x + dx, y + dy, tileId, collision: collision);
        }

        /// <summary>
        /// Check for collision with a world-space AABB (for 2D physics).
        /// Returns true if any collision tile overlaps the given bounds.
        /// </summary>
        public bool CheckCollision(float minX, float minY, float maxX, float maxY)
        {
            var (gMinX, gMinY) = WorldToGrid(minX, minY);
            var (gMaxX, gMaxY) = WorldToGrid(maxX, maxY);

            for (int gy = gMinY; gy <= gMaxY; gy++)
                for (int gx = gMinX; gx <= gMaxX; gx++)
                    if (HasCollisionAt(gx, gy)) return true;

            return false;
        }
    }
}
