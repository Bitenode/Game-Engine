#nullable enable
using System;
using System.Collections.Generic;
using Game_Engine.Core.Component;
using Silk.NET.OpenGL;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering.GPU;

/// <summary>
/// CPU tiled culling for point/spot lights; uploads R8UI/R32UI-style data as textures
/// so deferred lighting can loop only relevant lights per tile (GLES-friendly).
/// </summary>
public sealed class TiledLightTextureSystem : IDisposable
{
    public const int MaxDirLights = 4;
    public const int MaxLocalLights = 256;
    public const int MaxLightsPerTile = 32;
    public const int TileSize = 16;

    readonly GL _gl;
    GPUTexture? _metaTex;
    GPUTexture? _indexTex;
    GPUTexture? _localLightTex;
    int _tilesX, _tilesY;
    int _lastW, _lastH;
    bool _metaDirty = true;

    // Grow-only reusable CPU scratch buffers to avoid per-frame GC allocations.
    byte[] _metaScratch = Array.Empty<byte>();
    byte[] _indicesScratch = Array.Empty<byte>();
    int[] _countsScratch = Array.Empty<int>();
    float[] _lightDataScratch = Array.Empty<float>();

    public TiledLightTextureSystem(GL gl) => _gl = gl;

    public void Dispose()
    {
        _metaTex?.Dispose();
        _indexTex?.Dispose();
        _localLightTex?.Dispose();
        _metaTex = _indexTex = _localLightTex = null;
    }

    /// <summary>Rebuild tile textures and light data for the current frame.</summary>
    public void BuildAndUpload(
        int screenW,
        int screenH,
        in SN.Matrix4x4 view,
        in SN.Matrix4x4 proj,
        List<Light> dirOut,
        List<Light> localOut)
    {
        dirOut.Clear();
        localOut.Clear();
        foreach (var L in Light.AllLights)
        {
            if (L is not { Enabled: true, gameObject: not null }) continue;
            if (L.Type == LightType.Directional)
            {
                if (dirOut.Count < MaxDirLights)
                    dirOut.Add(L);
            }
            else
            {
                if (localOut.Count < MaxLocalLights)
                    localOut.Add(L);
            }
        }

        _tilesX = Math.Max(1, (screenW + TileSize - 1) / TileSize);
        _tilesY = Math.Max(1, (screenH + TileSize - 1) / TileSize);
        int numTiles = _tilesX * _tilesY;

        if (screenW != _lastW || screenH != _lastH)
        {
            _metaTex?.Dispose();
            _indexTex?.Dispose();
            _metaTex = new GPUTexture(_gl);
            _indexTex = new GPUTexture(_gl);
            _metaTex.CreateR8UNorm(_tilesX, _tilesY);
            _indexTex.CreateR8UNorm(MaxLightsPerTile, numTiles);
            _lastW = screenW;
            _lastH = screenH;
            _metaDirty = true;
        }

        if (_localLightTex == null || _localLightTex.Width < localOut.Count || _localLightTex.Height != 4)
        {
            _localLightTex?.Dispose();
            _localLightTex = new GPUTexture(_gl);
            _localLightTex.CreateRgbaFloat32(Math.Max(1, localOut.Count), 4);
        }

        int lc = localOut.Count;
        if (lc == 0)
        {
            // DeferredLighting skips tile reads when uLocalLightCount == 0, but ensure _metaTex
            // is zeroed once after resize or after a non-zero frame so stale counts never linger.
            if (_metaDirty)
            {
                if (_metaScratch.Length < numTiles)
                    _metaScratch = new byte[numTiles];
                Array.Clear(_metaScratch, 0, numTiles);
                _metaTex!.UploadR8UNormData(_metaScratch.AsSpan(0, numTiles));
                _metaDirty = false;
            }
            return;
        }

        if (_metaScratch.Length < numTiles)
            _metaScratch = new byte[numTiles];
        int indexCount = MaxLightsPerTile * numTiles;
        if (_indicesScratch.Length < indexCount)
            _indicesScratch = new byte[indexCount];
        if (_countsScratch.Length < numTiles)
            _countsScratch = new int[numTiles];
        int lightFloatCount = lc * 16;
        if (_lightDataScratch.Length < lightFloatCount)
            _lightDataScratch = new float[lightFloatCount];

        Array.Clear(_metaScratch, 0, numTiles);
        Array.Clear(_indicesScratch, 0, indexCount);
        Array.Clear(_countsScratch, 0, numTiles);

        var vp = view * proj;
        // Local light parameter rows (RGBA float)
        var lightData = _lightDataScratch;
        for (int i = 0; i < lc; i++)
        {
            var L = localOut[i];
            var p = L.GetWorldPosition();
            float range = Math.Max(0.001f, L.Range);
            int b = i * 16;
            lightData[b + 0] = p.X;
            lightData[b + 1] = p.Y;
            lightData[b + 2] = p.Z;
            lightData[b + 3] = range;
            lightData[b + 4] = L.Color.R / 255f;
            lightData[b + 5] = L.Color.G / 255f;
            lightData[b + 6] = L.Color.B / 255f;
            lightData[b + 7] = L.Intensity;
            var dir = SN.Vector3.Normalize(L.GetWorldDirection());
            float type = L.Type == LightType.Spot ? 1f : 0f;
            lightData[b + 8] = dir.X;
            lightData[b + 9] = dir.Y;
            lightData[b + 10] = dir.Z;
            lightData[b + 11] = type;
            float inner = MathF.Cos(Math.Clamp(L.InnerAngle, 0.5f, 89f) * MathF.PI / 180f);
            float outer = MathF.Cos(Math.Clamp(L.OuterAngle, 1f, 90f) * MathF.PI / 180f);
            lightData[b + 12] = inner;
            lightData[b + 13] = outer;
            lightData[b + 14] = 0f;
            lightData[b + 15] = 0f;
        }

        _localLightTex!.UploadRgbaFloatGridColumns(lightData, lc);

        for (int li = 0; li < lc; li++)
        {
            var L = localOut[li];
            var center = L.GetWorldPosition();
            float r = Math.Max(0.25f, L.Range);
            if (L.Type == LightType.Spot)
                r = Math.Max(r, L.Range);

            if (!ProjectLightBoundsToTiles(center, r, in vp, screenW, screenH, out int x0, out int y0, out int x1, out int y1))
                continue;

            for (int ty = y0; ty <= y1; ty++)
            {
                for (int tx = x0; tx <= x1; tx++)
                {
                    int tid = ty * _tilesX + tx;
                    int c = _countsScratch[tid];
                    if (c >= MaxLightsPerTile) continue;
                    _indicesScratch[tid * MaxLightsPerTile + c] = (byte)li;
                    _countsScratch[tid] = c + 1;
                    _metaScratch[tid] = (byte)(c + 1);
                }
            }
        }

        _metaTex!.UploadR8UNormData(_metaScratch.AsSpan(0, numTiles));
        _indexTex!.UploadR8UNormData(_indicesScratch.AsSpan(0, indexCount));
        _metaDirty = true;
    }

    bool ProjectLightBoundsToTiles(
        SN.Vector3 centerW,
        float radius,
        in SN.Matrix4x4 vp,
        int screenW,
        int screenH,
        out int x0,
        out int y0,
        out int x1,
        out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        // AABB of sphere in clip space (cheap 8-corner omit — use center ± basis)
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        bool any = false;
        for (int i = 0; i < 8; i++)
        {
            float sx = (i & 1) == 0 ? -1f : 1f;
            float sy = (i & 2) == 0 ? -1f : 1f;
            float sz = (i & 4) == 0 ? -1f : 1f;
            var w = centerW + new SN.Vector3(sx, sy, sz) * (radius * 0.70710677f);
            var clip = SN.Vector4.Transform(new SN.Vector4(w, 1f), vp);
            if (clip.W <= 0.0001f) continue;
            float nx = clip.X / clip.W;
            float ny = clip.Y / clip.W;
            minX = Math.Min(minX, nx);
            maxX = Math.Max(maxX, nx);
            minY = Math.Min(minY, ny);
            maxY = Math.Max(maxY, ny);
            any = true;
        }
        if (!any) return false;

        int px0 = (int)Math.Floor((minX * 0.5f + 0.5f) * screenW);
        int px1 = (int)Math.Ceiling((maxX * 0.5f + 0.5f) * screenW);
        int py0 = (int)Math.Floor((minY * 0.5f + 0.5f) * screenH);
        int py1 = (int)Math.Ceiling((maxY * 0.5f + 0.5f) * screenH);
        px0 = Math.Clamp(px0, 0, screenW - 1);
        px1 = Math.Clamp(px1, 0, screenW - 1);
        py0 = Math.Clamp(py0, 0, screenH - 1);
        py1 = Math.Clamp(py1, 0, screenH - 1);

        x0 = Math.Clamp(px0 / TileSize, 0, _tilesX - 1);
        x1 = Math.Clamp(px1 / TileSize, 0, _tilesX - 1);
        y0 = Math.Clamp(py0 / TileSize, 0, _tilesY - 1);
        y1 = Math.Clamp(py1 / TileSize, 0, _tilesY - 1);
        return x1 >= x0 && y1 >= y0;
    }

    public int TilesX => _tilesX;
    public int TilesY => _tilesY;

    public void BindToShader(ShaderProgram sh, int baseUnit)
    {
        if (_metaTex == null || _indexTex == null || _localLightTex == null) return;
        var u0 = TextureUnit.Texture0 + baseUnit;
        var u1 = TextureUnit.Texture0 + baseUnit + 1;
        var u2 = TextureUnit.Texture0 + baseUnit + 2;
        _metaTex.Bind(u0);
        sh.SetTexture("uTileMeta", baseUnit);
        _indexTex.Bind(u1);
        sh.SetTexture("uTileLightIdx", baseUnit + 1);
        _localLightTex.Bind(u2);
        sh.SetTexture("uLocalLightTex", baseUnit + 2);
    }
}
