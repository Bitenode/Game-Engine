#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Game_Engine.Core.Planet;

/// <summary>
/// In-memory mesh cache keyed by (face, lod, uv, seed, recipeHash, editStamp).
/// Revisiting a leaf uploads GPU mesh instead of remeshing when key matches.
/// </summary>
public sealed class PlanetChunkMeshCache
{
    public readonly struct Key : IEquatable<Key>
    {
        public readonly int Face;
        public readonly int Lod;
        public readonly int UQ;
        public readonly int VQ;
        public readonly int Seed;
        public readonly ulong RecipeHash;
        public readonly ulong EditStamp;

        public Key(int face, int lod, float u0, float v0, float u1, float v1, int seed, ulong recipeHash, ulong editStamp)
        {
            Face = face;
            Lod = lod;
            // Quantize UV corners so floating noise does not thrash the cache.
            UQ = HashUv(u0, u1);
            VQ = HashUv(v0, v1);
            Seed = seed;
            RecipeHash = recipeHash;
            EditStamp = editStamp;
        }

        static int HashUv(float a, float b)
        {
            unchecked
            {
                int ha = (int)(a * 4096f);
                int hb = (int)(b * 4096f);
                return (ha * 73856093) ^ (hb * 19349663);
            }
        }

        public bool Equals(Key other) =>
            Face == other.Face && Lod == other.Lod && UQ == other.UQ && VQ == other.VQ
            && Seed == other.Seed && RecipeHash == other.RecipeHash && EditStamp == other.EditStamp;

        public override bool Equals(object? obj) => obj is Key k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Face;
                h = (h * 397) ^ Lod;
                h = (h * 397) ^ UQ;
                h = (h * 397) ^ VQ;
                h = (h * 397) ^ Seed;
                h = (h * 397) ^ RecipeHash.GetHashCode();
                h = (h * 397) ^ EditStamp.GetHashCode();
                return h;
            }
        }
    }

    readonly ConcurrentDictionary<Key, object> _entries = new();
    readonly ConcurrentQueue<Key> _order = new();
    readonly int _capacity;

    public PlanetChunkMeshCache(int capacity = 256)
    {
        _capacity = Math.Max(32, capacity);
    }

    public int Count => _entries.Count;

    public bool TryGet(in Key key, out object? meshData)
    {
        if (_entries.TryGetValue(key, out meshData))
            return true;
        meshData = null;
        return false;
    }

    public void Put(in Key key, object meshData)
    {
        if (_entries.TryAdd(key, meshData))
            _order.Enqueue(key);
        else
            _entries[key] = meshData;

        while (_entries.Count > _capacity && _order.TryDequeue(out var old))
            _entries.TryRemove(old, out _);
    }

    public void Clear()
    {
        _entries.Clear();
        while (_order.TryDequeue(out _)) { }
    }

    public void InvalidateRecipe(ulong recipeHash)
    {
        foreach (var kv in _entries)
        {
            if (kv.Key.RecipeHash != recipeHash)
                _entries.TryRemove(kv.Key, out _);
        }
    }
}
