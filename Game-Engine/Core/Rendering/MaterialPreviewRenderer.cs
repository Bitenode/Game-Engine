using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Game_Engine.Core.Rendering.ShaderGraph;
using SN = System.Numerics;

namespace Game_Engine.Core.Rendering;

/// <summary>
/// Shared CPU-based PBR sphere raytracer used by both the Visual Shader Editor
/// and the Material Inspector to render material preview spheres.
/// </summary>
public static class MaterialPreviewRenderer
{
    public const int DefaultSize = 220;

    public struct PBRParams
    {
        public SN.Vector3 Albedo;
        public float Metallic;
        public float Roughness;
        public SN.Vector3 Emission;
        public float AO;

        // Fresnel glow (from shader graph)
        public bool HasFresnel;
        public SN.Vector3 FresnelColor;
        public float FresnelPower;

        // Per-pixel noise variation (from shader graph)
        public bool HasNoiseAlbedo;
        public SN.Vector3 AlbedoBase;
        public float NoiseScale;

        // Texture maps (for material inspector)
        public int[]? AlbedoPixels;
        public int AlbedoWidth, AlbedoHeight;
        public int[]? NormalPixels;
        public int NormalWidth, NormalHeight;
        public int[]? RoughnessPixels;
        public int RoughnessWidth, RoughnessHeight;
        public int[]? MetallicPixels;
        public int MetallicWidth, MetallicHeight;
        public int[]? EmissivePixels;
        public int EmissiveWidth, EmissiveHeight;
        public int[]? AOPixels;
        public int AOWidth, AOHeight;
        public int[]? SpecularPixels;
        public int SpecularWidth, SpecularHeight;

        // Sphere rotation (radians) for interactive preview
        public float RotationY;
        public float RotationX;
    }

    /// <summary>
    /// Render a PBR sphere preview to a WriteableBitmap and return it.
    /// </summary>
    public static WriteableBitmap Render(PBRParams pbr, int size = DefaultSize)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var buf = bitmap.Lock();
        RaytraceSpherePBR(buf.Address, buf.RowBytes, size, size, pbr);
        return bitmap;
    }

    /// <summary>
    /// Load an image file and extract its pixels as BGRA int array for texture sampling.
    /// Returns null if the file doesn't exist or can't be loaded.
    /// </summary>
    public static int[]? LoadTexturePixels(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var bmp = new Bitmap(path);
            width = bmp.PixelSize.Width;
            height = bmp.PixelSize.Height;

            // Convert the Bitmap to a WriteableBitmap in BGRA8888 for pixel access
            var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var dst = wb.Lock())
            {
                bmp.CopyPixels(
                    new PixelRect(0, 0, width, height),
                    dst.Address,
                    dst.RowBytes * height,
                    dst.RowBytes);
            }

            var pixels = new int[width * height];
            using (var locked = wb.Lock())
            {
                unsafe
                {
                    var ptr = (int*)locked.Address;
                    int stride = locked.RowBytes / 4;
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            pixels[y * width + x] = ptr[y * stride + x];
                }
            }
            return pixels;
        }
        catch
        {
            return null;
        }
    }

    // ── Texture Sampling ──

    private static SN.Vector3 SampleTextureRGB(int[]? pixels, int w, int h, float u, float v)
    {
        if (pixels == null || w == 0 || h == 0) return SN.Vector3.One;

        // Wrap UVs
        u = u - MathF.Floor(u);
        v = v - MathF.Floor(v);

        int px = Math.Clamp((int)(u * w), 0, w - 1);
        int py = Math.Clamp((int)(v * h), 0, h - 1);
        int bgra = pixels[py * w + px];

        float b = (bgra & 0xFF) / 255f;
        float g = ((bgra >> 8) & 0xFF) / 255f;
        float r = ((bgra >> 16) & 0xFF) / 255f;
        return new SN.Vector3(r, g, b);
    }

    private static float SampleTextureGray(int[]? pixels, int w, int h, float u, float v)
    {
        if (pixels == null || w == 0 || h == 0) return 1f;
        var rgb = SampleTextureRGB(pixels, w, h, u, v);
        return (rgb.X + rgb.Y + rgb.Z) / 3f;
    }

    /// <summary>Convert a sphere surface normal to UV coordinates (spherical mapping) with rotation offset.</summary>
    private static (float u, float v) NormalToUV(SN.Vector3 N, float rotY = 0f, float rotX = 0f)
    {
        // Apply Y rotation (horizontal spin)
        float cosY = MathF.Cos(rotY), sinY = MathF.Sin(rotY);
        float rx = N.X * cosY + N.Z * sinY;
        float rz = -N.X * sinY + N.Z * cosY;

        // Apply X rotation (vertical tilt)
        float cosX = MathF.Cos(rotX), sinX = MathF.Sin(rotX);
        float ry = N.Y * cosX - rz * sinX;
        float rz2 = N.Y * sinX + rz * cosX;

        float u = 0.5f + MathF.Atan2(rz2, rx) / (2f * MathF.PI);
        float v = 0.5f - MathF.Asin(Math.Clamp(ry, -1f, 1f)) / MathF.PI;
        return (u, v);
    }

    /// <summary>Perturb a surface normal using a tangent-space normal map sample.</summary>
    private static SN.Vector3 PerturbNormal(SN.Vector3 N, SN.Vector3 normalMapSample)
    {
        // Convert from [0,1] to [-1,1]
        var tanNormal = new SN.Vector3(
            normalMapSample.X * 2f - 1f,
            normalMapSample.Y * 2f - 1f,
            normalMapSample.Z * 2f - 1f);

        // Build TBN from sphere normal
        var up = MathF.Abs(N.Y) < 0.999f ? SN.Vector3.UnitY : SN.Vector3.UnitX;
        var T = SN.Vector3.Normalize(SN.Vector3.Cross(up, N));
        var B = SN.Vector3.Cross(N, T);

        // Transform from tangent to world space
        var worldNormal = T * tanNormal.X + B * tanNormal.Y + N * tanNormal.Z;
        return SN.Vector3.Normalize(worldNormal);
    }

    // ── Raytracer ──

    private static unsafe void RaytraceSpherePBR(IntPtr address, int rowBytes, int w, int h, PBRParams pbr)
    {
        var ptr = (byte*)address;
        int stride = rowBytes;

        var camPos = new SN.Vector3(0, 0, 2.8f);
        float fov = 35f * MathF.PI / 180f;
        float aspect = (float)w / h;
        float tanHalf = MathF.Tan(fov / 2f);

        var lightDir1 = SN.Vector3.Normalize(new SN.Vector3(1f, 1f, 1.5f));
        var lightColor1 = new SN.Vector3(1f, 0.98f, 0.95f) * 3.0f;
        var lightDir2 = SN.Vector3.Normalize(new SN.Vector3(-0.5f, 0.3f, -0.5f));
        var lightColor2 = new SN.Vector3(0.4f, 0.5f, 0.7f) * 1.0f;
        var lightDir3 = SN.Vector3.Normalize(new SN.Vector3(-0.3f, -0.8f, 0.5f));
        var lightColor3 = new SN.Vector3(0.3f, 0.25f, 0.2f) * 0.5f;

        var envTop = new SN.Vector3(0.15f, 0.18f, 0.3f);
        var envBot = new SN.Vector3(0.04f, 0.04f, 0.06f);

        bool hasTextures = pbr.AlbedoPixels != null || pbr.NormalPixels != null ||
                           pbr.RoughnessPixels != null || pbr.MetallicPixels != null ||
                           pbr.EmissivePixels != null || pbr.AOPixels != null ||
                           pbr.SpecularPixels != null;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float u = (2f * x / w - 1f) * aspect * tanHalf;
                float v = (1f - 2f * y / h) * tanHalf;
                var rayDir = SN.Vector3.Normalize(new SN.Vector3(u, v, -1f));

                float bCoeff = 2f * SN.Vector3.Dot(camPos, rayDir);
                float cCoeff = SN.Vector3.Dot(camPos, camPos) - 1f;
                float disc = bCoeff * bCoeff - 4f * cCoeff;

                SN.Vector3 color;
                if (disc >= 0)
                {
                    float t = (-bCoeff - MathF.Sqrt(disc)) / 2f;
                    var hitPos = camPos + rayDir * t;
                    var N = SN.Vector3.Normalize(hitPos);
                    var V = SN.Vector3.Normalize(camPos - hitPos);

                    var pixelPbr = pbr;

                    // Sample textures if available
                    if (hasTextures)
                    {
                        var (tu, tv) = NormalToUV(N, pbr.RotationY, pbr.RotationX);

                        if (pbr.AlbedoPixels != null)
                        {
                            var texCol = SampleTextureRGB(pbr.AlbedoPixels, pbr.AlbedoWidth, pbr.AlbedoHeight, tu, tv);
                            pixelPbr.Albedo = pbr.Albedo * texCol;
                        }
                        if (pbr.NormalPixels != null)
                        {
                            var nSample = SampleTextureRGB(pbr.NormalPixels, pbr.NormalWidth, pbr.NormalHeight, tu, tv);
                            N = PerturbNormal(N, nSample);
                        }
                        if (pbr.RoughnessPixels != null)
                        {
                            float texRough = SampleTextureGray(pbr.RoughnessPixels, pbr.RoughnessWidth, pbr.RoughnessHeight, tu, tv);
                            pixelPbr.Roughness = Math.Clamp(texRough, 0.04f, 1f);
                        }
                        if (pbr.SpecularPixels != null)
                        {
                            float texSpec = SampleTextureGray(pbr.SpecularPixels, pbr.SpecularWidth, pbr.SpecularHeight, tu, tv);
                            pixelPbr.Roughness = Math.Clamp(1f - texSpec, 0.04f, 1f);
                        }
                        if (pbr.MetallicPixels != null)
                        {
                            float texMetal = SampleTextureGray(pbr.MetallicPixels, pbr.MetallicWidth, pbr.MetallicHeight, tu, tv);
                            pixelPbr.Metallic = Math.Clamp(texMetal, 0f, 1f);
                        }
                        if (pbr.EmissivePixels != null)
                        {
                            var texEmit = SampleTextureRGB(pbr.EmissivePixels, pbr.EmissiveWidth, pbr.EmissiveHeight, tu, tv);
                            pixelPbr.Emission = pbr.Emission + texEmit;
                        }
                        if (pbr.AOPixels != null)
                        {
                            float texAO = SampleTextureGray(pbr.AOPixels, pbr.AOWidth, pbr.AOHeight, tu, tv);
                            pixelPbr.AO = pbr.AO * texAO;
                        }
                    }

                    // Per-pixel noise albedo variation (shader graph feature)
                    if (pbr.HasNoiseAlbedo)
                    {
                        float noiseVal = SimpleNoise(hitPos.X * pbr.NoiseScale, hitPos.Y * pbr.NoiseScale, hitPos.Z * pbr.NoiseScale);
                        noiseVal = noiseVal * 0.5f + 0.5f;
                        noiseVal = 0.6f + noiseVal * 0.8f;
                        pixelPbr.Albedo = pbr.AlbedoBase * noiseVal;
                    }

                    color = ShadePBR(N, V, pixelPbr, lightDir1, lightColor1, lightDir2, lightColor2, lightDir3, lightColor3, envTop);
                }
                else
                {
                    float envT = rayDir.Y * 0.5f + 0.5f;
                    color = SN.Vector3.Lerp(envBot, envTop, envT);

                    float cx = (x - w * 0.5f) / (w * 0.5f);
                    float cy = (y - h * 0.5f) / (h * 0.5f);
                    float vignette = 1f - (cx * cx + cy * cy) * 0.3f;
                    color *= Math.Max(vignette, 0.3f);
                }

                // Tone mapping (Reinhard)
                color = color / (color + SN.Vector3.One);

                // Gamma correction
                float gamma = 1f / 2.2f;
                int rr = Math.Clamp((int)(MathF.Pow(color.X, gamma) * 255), 0, 255);
                int gg = Math.Clamp((int)(MathF.Pow(color.Y, gamma) * 255), 0, 255);
                int bb = Math.Clamp((int)(MathF.Pow(color.Z, gamma) * 255), 0, 255);

                int offset = y * stride + x * 4;
                ptr[offset + 0] = (byte)bb;
                ptr[offset + 1] = (byte)gg;
                ptr[offset + 2] = (byte)rr;
                ptr[offset + 3] = 255;
            }
        }
    }

    // ── PBR Shading ──

    internal static SN.Vector3 ShadePBR(SN.Vector3 N, SN.Vector3 V, PBRParams pbr,
        SN.Vector3 lightDir1, SN.Vector3 lightColor1,
        SN.Vector3 lightDir2, SN.Vector3 lightColor2,
        SN.Vector3 lightDir3, SN.Vector3 lightColor3,
        SN.Vector3 envColor)
    {
        float roughness = Math.Clamp(pbr.Roughness, 0.04f, 1f);
        float metallic = Math.Clamp(pbr.Metallic, 0f, 1f);

        var F0 = SN.Vector3.Lerp(new SN.Vector3(0.04f), pbr.Albedo, metallic);
        var diffuseColor = pbr.Albedo * (1f - metallic);

        var result = SN.Vector3.Zero;

        result += CalculateLight(N, V, lightDir1, lightColor1, diffuseColor, F0, roughness);
        result += CalculateLight(N, V, lightDir2, lightColor2, diffuseColor, F0, roughness);
        result += CalculateLight(N, V, lightDir3, lightColor3, diffuseColor, F0, roughness);

        float NdotV = Math.Max(SN.Vector3.Dot(N, V), 0f);
        var fresnelEnv = FresnelSchlick(NdotV, F0, roughness);

        var ambient = diffuseColor * envColor * 0.3f * pbr.AO;
        result += ambient;

        float smoothness = 1f - roughness;
        float envBrightness = 0.4f + smoothness * smoothness * 1.5f;
        var envReflection = fresnelEnv * envColor * envBrightness;
        result += envReflection;

        if (metallic > 0.3f)
        {
            var R = SN.Vector3.Reflect(-V, N);
            float skyFactor = Math.Clamp(R.Y * 0.5f + 0.5f, 0f, 1f);
            var skyColor = SN.Vector3.Lerp(
                new SN.Vector3(0.12f, 0.1f, 0.08f),
                new SN.Vector3(0.7f, 0.75f, 0.9f),
                skyFactor);

            float horizonBand = 1f - MathF.Abs(R.Y);
            horizonBand = horizonBand * horizonBand * horizonBand;
            skyColor += new SN.Vector3(0.4f, 0.35f, 0.3f) * horizonBand;

            float reflStrength = metallic * smoothness * smoothness * 1.5f;
            result += F0 * skyColor * reflStrength;
        }

        float rim = 1f - NdotV;
        rim = rim * rim * rim;
        result += envColor * rim * 0.2f;

        if (pbr.HasFresnel && pbr.FresnelColor.LengthSquared() > 0.001f)
        {
            float power = Math.Max(pbr.FresnelPower, 0.1f);
            float rawFresnel = Math.Clamp(1f - NdotV, 0f, 1f);

            float rimFresnel = MathF.Pow(rawFresnel, power);
            result += pbr.FresnelColor * rimFresnel * 4.0f;

            float softPower = Math.Max(power * 0.4f, 0.3f);
            float softFresnel = MathF.Pow(rawFresnel, softPower);
            result += pbr.FresnelColor * softFresnel * 0.5f;
        }
        else
        {
            result += pbr.Emission;
        }

        return result;
    }

    internal static SN.Vector3 CalculateLight(SN.Vector3 N, SN.Vector3 V, SN.Vector3 L,
        SN.Vector3 lightColor, SN.Vector3 diffuseColor, SN.Vector3 F0, float roughness)
    {
        var H = SN.Vector3.Normalize(V + L);
        float NdotL = Math.Max(SN.Vector3.Dot(N, L), 0f);
        float NdotH = Math.Max(SN.Vector3.Dot(N, H), 0f);
        float NdotV = Math.Max(SN.Vector3.Dot(N, V), 0.001f);
        float HdotV = Math.Max(SN.Vector3.Dot(H, V), 0f);

        if (NdotL <= 0) return SN.Vector3.Zero;

        float D = DistributionGGX(NdotH, roughness);
        float G = GeometrySmith(NdotV, NdotL, roughness);
        var F = FresnelSchlick(HdotV, F0);

        var spec = F * D * G / Math.Max(4f * NdotV * NdotL, 0.001f);
        var kD = (SN.Vector3.One - F);
        var diffuse = kD * diffuseColor / MathF.PI;

        return (diffuse + spec) * lightColor * NdotL;
    }

    internal static float DistributionGGX(float NdotH, float roughness)
    {
        float a = roughness * roughness;
        float a2 = a * a;
        float d = NdotH * NdotH * (a2 - 1f) + 1f;
        return a2 / (MathF.PI * d * d + 0.0001f);
    }

    internal static float GeometrySmith(float NdotV, float NdotL, float roughness)
    {
        float r = roughness + 1f;
        float k = r * r / 8f;
        float g1 = NdotV / (NdotV * (1f - k) + k);
        float g2 = NdotL / (NdotL * (1f - k) + k);
        return g1 * g2;
    }

    internal static SN.Vector3 FresnelSchlick(float cosTheta, SN.Vector3 F0, float roughness = 0f)
    {
        float t = MathF.Pow(Math.Clamp(1f - cosTheta, 0f, 1f), 5f);
        if (roughness > 0)
        {
            var maxReflect = SN.Vector3.Max(new SN.Vector3(1f - roughness), F0);
            return F0 + (maxReflect - F0) * t;
        }
        return F0 + (SN.Vector3.One - F0) * t;
    }

    internal static float SimpleNoise(float x, float y, float z)
    {
        static float Hash(float a, float b, float c)
        {
            float h = a * 127.1f + b * 311.7f + c * 74.7f;
            return MathF.Sin(h) * 43758.5453f % 1f;
        }

        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
        float fx = x - ix, fy = y - iy, fz = z - iz;

        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        fz = fz * fz * (3f - 2f * fz);

        float n000 = Hash(ix, iy, iz);
        float n100 = Hash(ix + 1, iy, iz);
        float n010 = Hash(ix, iy + 1, iz);
        float n110 = Hash(ix + 1, iy + 1, iz);
        float n001 = Hash(ix, iy, iz + 1);
        float n101 = Hash(ix + 1, iy, iz + 1);
        float n011 = Hash(ix, iy + 1, iz + 1);
        float n111 = Hash(ix + 1, iy + 1, iz + 1);

        float n00 = n000 + (n100 - n000) * fx;
        float n01 = n001 + (n101 - n001) * fx;
        float n10 = n010 + (n110 - n010) * fx;
        float n11 = n011 + (n111 - n011) * fx;

        float n0 = n00 + (n10 - n00) * fy;
        float n1 = n01 + (n11 - n01) * fy;

        return n0 + (n1 - n0) * fz;
    }

    // ── Shader Graph Evaluation ──

    /// <summary>
    /// Try to load a .shadergraph file and extract PBR parameters from the node graph.
    /// Searches multiple locations for the .shadergraph. If not found, falls back to
    /// parsing the compiled .shader GLSL to extract basic PBR values.
    /// </summary>
    public static PBRParams? ExtractPBRFromShaderGraph(string shaderPath)
    {
        if (string.IsNullOrWhiteSpace(shaderPath)) return null;

        // Try to find the .shadergraph file in multiple locations
        string sgPath = FindShaderGraph(shaderPath);

        if (sgPath != null)
        {
            var result = LoadShaderGraphPBR(sgPath);
            if (result.HasValue) return result;
        }

        // Fallback: parse the compiled .shader file's GLSL to extract PBR values
        return ParseShaderFilePBR(shaderPath);
    }

    /// <summary>
    /// Search multiple locations for a .shadergraph file matching a .shader file.
    /// </summary>
    private static string? FindShaderGraph(string shaderPath)
    {
        // 1. Same directory, same name with .shadergraph extension
        string sgPath = System.IO.Path.ChangeExtension(shaderPath, ".shadergraph");
        if (File.Exists(sgPath)) return sgPath;

        string shaderName = System.IO.Path.GetFileNameWithoutExtension(shaderPath);

        // 2. Search the project root
        try
        {
            var projRoot = GetProjectRoot();
            if (!string.IsNullOrWhiteSpace(projRoot) && Directory.Exists(projRoot))
            {
                // Check project root directly
                string rootSg = System.IO.Path.Combine(projRoot, shaderName + ".shadergraph");
                if (File.Exists(rootSg)) return rootSg;

                // Search project recursively (limited depth to avoid long searches)
                var found = SearchForFile(projRoot, shaderName + ".shadergraph", 3);
                if (found != null) return found;
            }
        }
        catch { }

        // 3. Search parent directories of the .shader file
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(shaderPath);
            if (dir != null)
            {
                string? parent = System.IO.Path.GetDirectoryName(dir);
                if (parent != null)
                {
                    string parentSg = System.IO.Path.Combine(parent, shaderName + ".shadergraph");
                    if (File.Exists(parentSg)) return parentSg;
                }
            }
        }
        catch { }

        return null;
    }

    private static string? GetProjectRoot()
    {
        try
        {
            var t = Type.GetType("Game_Engine.Core.ProjectService, Game_Engine");
            if (t == null) return null;
            var currentProp = t.GetProperty("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (currentProp == null) return null;
            var current = currentProp.GetValue(null);
            if (current == null) return null;
            var rootProp = current.GetType().GetProperty("RootPath");
            if (rootProp == null) return null;
            return rootProp.GetValue(current) as string;
        }
        catch { return null; }
    }

    private static string? SearchForFile(string directory, string fileName, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        try
        {
            string candidate = System.IO.Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;

            foreach (var subDir in Directory.GetDirectories(directory))
            {
                string dirName = System.IO.Path.GetFileName(subDir);
                if (dirName.StartsWith(".") || dirName == "node_modules" || dirName == "bin" || dirName == "obj")
                    continue;
                var found = SearchForFile(subDir, fileName, maxDepth - 1);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Load a .shadergraph file and extract PBR parameters from it.
    /// </summary>
    private static PBRParams? LoadShaderGraphPBR(string sgPath)
    {
        try
        {
            var graph = ShaderGraph.ShaderGraph.LoadFromFile(sgPath);
            if (graph?.Output == null) return null;

            var output = graph.Output;
            var albedo = EvalVec3(output.Inputs[0], new SN.Vector3(1f));
            float metallic = EvalFloat(output.Inputs[2], 0f);
            float roughness = EvalFloat(output.Inputs[3], 0.5f);
            var emission = EvalVec3(output.Inputs[4], SN.Vector3.Zero);
            float ao = EvalFloat(output.Inputs[6], 1f);

            // Detect noise in albedo chain
            bool hasNoiseAlbedo = false;
            var albedoBase = albedo;
            float noiseScale = 10f;
            var albedoConn = output.Inputs[0].Connection;
            if (albedoConn?.Owner is MathNode albedoMath)
            {
                bool aIsNoise = albedoMath.Inputs[0].Connection?.Owner is NoiseNode;
                bool bIsNoise = albedoMath.Inputs[1].Connection?.Owner is NoiseNode;
                if (aIsNoise || bIsNoise)
                {
                    hasNoiseAlbedo = true;
                    var colorPort = aIsNoise ? albedoMath.Inputs[1] : albedoMath.Inputs[0];
                    var noisePort = aIsNoise ? albedoMath.Inputs[0] : albedoMath.Inputs[1];
                    albedoBase = EvalVec3(colorPort, new SN.Vector3(0.5f));
                    var noiseNode = (NoiseNode)noisePort.Connection!.Owner;
                    noiseScale = EvalFloat(noiseNode.Inputs[1], 10f);
                }
            }

            // Detect Fresnel in emission chain
            bool hasFresnel = false;
            var fresnelColor = SN.Vector3.Zero;
            float fresnelPower = 5f;

            if (output.Inputs[4].Connection != null)
            {
                var fresnelNode = FindFresnelNodeInChain(output.Inputs[4]);
                if (fresnelNode != null)
                {
                    hasFresnel = true;
                    fresnelPower = EvalFloat(fresnelNode.Inputs[0], 5f);
                    fresnelColor = FindFresnelColor(output.Inputs[4]);
                    if (fresnelColor == SN.Vector3.One && emission.Length() > 0.01f)
                        fresnelColor = emission * 2f;
                }
            }

            return new PBRParams
            {
                Albedo = albedo,
                Metallic = Math.Clamp(metallic, 0f, 1f),
                Roughness = Math.Clamp(roughness, 0f, 1f),
                Emission = hasFresnel ? SN.Vector3.Zero : emission,
                AO = Math.Clamp(ao, 0f, 1f),
                HasFresnel = hasFresnel,
                FresnelColor = fresnelColor,
                FresnelPower = fresnelPower,
                HasNoiseAlbedo = hasNoiseAlbedo,
                AlbedoBase = albedoBase,
                NoiseScale = noiseScale
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fallback: parse the compiled .shader file's GLSL fragment source to extract
    /// basic PBR values (albedo, metallic, roughness, emission) from the generated code.
    /// </summary>
    private static PBRParams? ParseShaderFilePBR(string shaderPath)
    {
        if (!File.Exists(shaderPath)) return null;

        try
        {
            string content = File.ReadAllText(shaderPath);

            // Find the FRAGMENT block
            int fragIdx = content.IndexOf("FRAGMENT", StringComparison.Ordinal);
            if (fragIdx < 0) return null;

            int braceStart = content.IndexOf('{', fragIdx);
            if (braceStart < 0) return null;

            // Extract fragment source (rough extraction, just need the main() body)
            string fragSource = content.Substring(braceStart);

            // Parse PBR values from the generated GLSL assignments
            var albedo = ParseVec3Assignment(fragSource, "albedoVal");
            float metallic = ParseFloatAssignment(fragSource, "metallicVal", 0f);
            float roughness = ParseFloatAssignment(fragSource, "roughnessVal", 0.5f);
            var emission = ParseVec3Assignment(fragSource, "emissionVal");

            // Only return if we actually found meaningful values
            if (albedo == SN.Vector3.One && metallic == 0f && roughness == 0.5f && emission == SN.Vector3.Zero)
                return null; // All defaults, probably didn't parse anything useful

            return new PBRParams
            {
                Albedo = albedo,
                Metallic = Math.Clamp(metallic, 0f, 1f),
                Roughness = Math.Clamp(roughness, 0f, 1f),
                Emission = emission,
                AO = 1f
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse a vec3 value from GLSL like "vec3 albedoVal = vec3(0.8, 0.7, 0.3);"</summary>
    private static SN.Vector3 ParseVec3Assignment(string glsl, string varName)
    {
        // Look for patterns like: vec3(X, Y, Z) or vec3(X) after the variable name
        int idx = glsl.IndexOf(varName, StringComparison.Ordinal);
        if (idx < 0) return SN.Vector3.One;

        // Find vec3( after the variable name
        int vec3Idx = glsl.IndexOf("vec3(", idx, StringComparison.Ordinal);
        if (vec3Idx < 0 || vec3Idx > idx + 100) return SN.Vector3.One;

        int parenStart = vec3Idx + 5;
        int parenEnd = glsl.IndexOf(')', parenStart);
        if (parenEnd < 0) return SN.Vector3.One;

        string inner = glsl.Substring(parenStart, parenEnd - parenStart).Trim();

        // Handle vec3(X) - single component
        if (!inner.Contains(','))
        {
            if (float.TryParse(inner, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v))
                return new SN.Vector3(v);
            return SN.Vector3.One;
        }

        // Handle vec3(X, Y, Z)
        var parts = inner.Split(',');
        if (parts.Length >= 3 &&
            float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float z))
        {
            return new SN.Vector3(x, y, z);
        }

        return SN.Vector3.One;
    }

    /// <summary>Parse a float value from GLSL like "float metallicVal = 0.9;"</summary>
    private static float ParseFloatAssignment(string glsl, string varName, float fallback)
    {
        int idx = glsl.IndexOf(varName, StringComparison.Ordinal);
        if (idx < 0) return fallback;

        // Find '=' after the variable name
        int eqIdx = glsl.IndexOf('=', idx);
        if (eqIdx < 0 || eqIdx > idx + varName.Length + 5) return fallback;

        // Read until ';'
        int semiIdx = glsl.IndexOf(';', eqIdx);
        if (semiIdx < 0) return fallback;

        string valueStr = glsl.Substring(eqIdx + 1, semiIdx - eqIdx - 1).Trim();

        if (float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float val))
            return val;

        return fallback;
    }

    private static FresnelNode? FindFresnelNodeInChain(ShaderPort input, int depth = 0)
    {
        if (depth > 10) return null;
        var conn = input.Connection;
        if (conn == null) return null;
        if (conn.Owner is FresnelNode fn) return fn;
        foreach (var inp in conn.Owner.Inputs)
        {
            var found = FindFresnelNodeInChain(inp, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static SN.Vector3 FindFresnelColor(ShaderPort input, int depth = 0)
    {
        if (depth > 10) return SN.Vector3.One;
        var conn = input.Connection;
        if (conn == null) return SN.Vector3.One;

        if (conn.Owner is MathNode mathNode)
        {
            bool aIsFresnel = HasFresnelInChain(mathNode.Inputs[0]);
            bool bIsFresnel = HasFresnelInChain(mathNode.Inputs[1]);
            if (aIsFresnel && !bIsFresnel)
                return EvalVec3(mathNode.Inputs[1], SN.Vector3.One);
            if (bIsFresnel && !aIsFresnel)
                return EvalVec3(mathNode.Inputs[0], SN.Vector3.One);
        }
        if (conn.Owner is ColorNode c)
            return new SN.Vector3(c.R, c.G, c.B);

        return SN.Vector3.One;
    }

    private static bool HasFresnelInChain(ShaderPort input, int depth = 0)
    {
        if (depth > 10) return false;
        var conn = input.Connection;
        if (conn == null) return false;
        if (conn.Owner is FresnelNode) return true;
        foreach (var inp in conn.Owner.Inputs)
            if (HasFresnelInChain(inp, depth + 1)) return true;
        return false;
    }

    private static float EvalFloat(ShaderPort input, float fallback)
    {
        var conn = input.Connection;
        if (conn == null) return fallback;
        var node = conn.Owner;
        return node switch
        {
            FloatNode f => f.Value,
            ColorNode c => conn.Name switch
            {
                "R" => c.R, "G" => c.G, "B" => c.B, "A" => c.A,
                _ => (c.R + c.G + c.B) / 3f
            },
            MathNode m => EvalMathFloat(m),
            FresnelNode => 0.5f,
            NoiseNode => 0.5f,
            _ => fallback
        };
    }

    private static SN.Vector3 EvalVec3(ShaderPort input, SN.Vector3 fallback)
    {
        var conn = input.Connection;
        if (conn == null) return fallback;
        var node = conn.Owner;
        return node switch
        {
            ColorNode c => conn.Name switch
            {
                "R" => new SN.Vector3(c.R), "G" => new SN.Vector3(c.G),
                "B" => new SN.Vector3(c.B), "A" => new SN.Vector3(c.A),
                _ => new SN.Vector3(c.R, c.G, c.B)
            },
            FloatNode f => new SN.Vector3(f.Value),
            MathNode m => EvalMathVec3(m),
            FresnelNode => new SN.Vector3(0.5f),
            NoiseNode => new SN.Vector3(0.5f),
            _ => fallback
        };
    }

    private static float EvalMathFloat(MathNode m)
    {
        float a = EvalFloat(m.Inputs[0], 0f);
        float b = EvalFloat(m.Inputs[1], 0f);
        return m.Operation switch
        {
            MathNode.MathOp.Add => a + b,
            MathNode.MathOp.Subtract => a - b,
            MathNode.MathOp.Multiply => a * b,
            MathNode.MathOp.Divide => b != 0 ? a / b : 0f,
            MathNode.MathOp.Power => MathF.Pow(Math.Abs(a), b),
            MathNode.MathOp.Min => Math.Min(a, b),
            MathNode.MathOp.Max => Math.Max(a, b),
            _ => a
        };
    }

    private static SN.Vector3 EvalMathVec3(MathNode m)
    {
        var a = EvalVec3(m.Inputs[0], SN.Vector3.Zero);
        var b = EvalVec3(m.Inputs[1], SN.Vector3.Zero);
        return m.Operation switch
        {
            MathNode.MathOp.Add => a + b,
            MathNode.MathOp.Subtract => a - b,
            MathNode.MathOp.Multiply => a * b,
            MathNode.MathOp.Divide => new SN.Vector3(
                b.X != 0 ? a.X / b.X : 0f,
                b.Y != 0 ? a.Y / b.Y : 0f,
                b.Z != 0 ? a.Z / b.Z : 0f),
            MathNode.MathOp.Power => new SN.Vector3(
                MathF.Pow(Math.Abs(a.X), b.X),
                MathF.Pow(Math.Abs(a.Y), b.Y),
                MathF.Pow(Math.Abs(a.Z), b.Z)),
            MathNode.MathOp.Min => SN.Vector3.Min(a, b),
            MathNode.MathOp.Max => SN.Vector3.Max(a, b),
            _ => a
        };
    }
}
