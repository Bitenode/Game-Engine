using System;

namespace Game_Engine.Core.Noise;

public enum FractalMode { FBM, Ridged, Billow }

/// <summary>
/// Fractal noise built on top of <see cref="SimplexNoise"/>. Supports FBM,
/// ridged multifractal, and billow modes with configurable octaves,
/// lacunarity, persistence, and domain warping.
/// </summary>
public sealed class FractalNoise
{
    public SimplexNoise Source { get; }
    public int Octaves { get; set; } = 6;
    public float Frequency { get; set; } = 1f;
    public float Amplitude { get; set; } = 1f;
    public float Lacunarity { get; set; } = 2.0f;
    public float Persistence { get; set; } = 0.5f;
    public FractalMode Mode { get; set; } = FractalMode.FBM;
    public float DomainWarpStrength { get; set; } = 0f;

    public FractalNoise(int seed = 0)
    {
        Source = new SimplexNoise(seed);
    }

    public FractalNoise(SimplexNoise source)
    {
        Source = source;
    }

    public float Sample2D(float x, float y)
    {
        if (DomainWarpStrength > 0f)
        {
            float wx = Source.Noise2D(x + 5.2f, y + 1.3f) * DomainWarpStrength;
            float wy = Source.Noise2D(x + 9.1f, y + 3.7f) * DomainWarpStrength;
            x += wx;
            y += wy;
        }

        return Mode switch
        {
            FractalMode.FBM => FBM2D(x, y),
            FractalMode.Ridged => Ridged2D(x, y),
            FractalMode.Billow => Billow2D(x, y),
            _ => FBM2D(x, y),
        };
    }

    public float Sample3D(float x, float y, float z)
    {
        if (DomainWarpStrength > 0f)
        {
            float wx = Source.Noise3D(x + 5.2f, y + 1.3f, z + 8.4f) * DomainWarpStrength;
            float wy = Source.Noise3D(x + 9.1f, y + 3.7f, z + 2.8f) * DomainWarpStrength;
            float wz = Source.Noise3D(x + 1.7f, y + 6.5f, z + 4.2f) * DomainWarpStrength;
            x += wx;
            y += wy;
            z += wz;
        }

        return Mode switch
        {
            FractalMode.FBM => FBM3D(x, y, z),
            FractalMode.Ridged => Ridged3D(x, y, z),
            FractalMode.Billow => Billow3D(x, y, z),
            _ => FBM3D(x, y, z),
        };
    }

    float FBM2D(float x, float y)
    {
        float sum = 0f, amp = Amplitude, freq = Frequency;
        for (int i = 0; i < Octaves; i++)
        {
            sum += Source.Noise2D(x * freq, y * freq) * amp;
            freq *= Lacunarity;
            amp *= Persistence;
        }
        return sum;
    }

    float FBM3D(float x, float y, float z)
    {
        float sum = 0f, amp = Amplitude, freq = Frequency;
        for (int i = 0; i < Octaves; i++)
        {
            sum += Source.Noise3D(x * freq, y * freq, z * freq) * amp;
            freq *= Lacunarity;
            amp *= Persistence;
        }
        return sum;
    }

    float Ridged2D(float x, float y)
    {
        float sum = 0f, amp = Amplitude, freq = Frequency;
        float weight = 1f;
        for (int i = 0; i < Octaves; i++)
        {
            float n = 1f - MathF.Abs(Source.Noise2D(x * freq, y * freq));
            n *= n * weight;
            weight = Math.Clamp(n * 2f, 0f, 1f);
            sum += n * amp;
            freq *= Lacunarity;
            amp *= Persistence;
        }
        return sum;
    }

    float Ridged3D(float x, float y, float z)
    {
        float sum = 0f, amp = Amplitude, freq = Frequency;
        float weight = 1f;
        for (int i = 0; i < Octaves; i++)
        {
            float n = 1f - MathF.Abs(Source.Noise3D(x * freq, y * freq, z * freq));
            n *= n * weight;
            weight = Math.Clamp(n * 2f, 0f, 1f);
            sum += n * amp;
            freq *= Lacunarity;
            amp *= Persistence;
        }
        return sum;
    }

    float Billow2D(float x, float y)
    {
        float sum = 0f, amp = Amplitude, freq = Frequency;
        for (int i = 0; i < Octaves; i++)
        {
            sum += (MathF.Abs(Source.Noise2D(x * freq, y * freq)) * 2f - 1f) * amp;
            freq *= Lacunarity;
            amp *= Persistence;
        }
        return sum;
    }

    float Billow3D(float x, float y, float z)
    {
        float sum = 0f, amp = Amplitude, freq = Frequency;
        for (int i = 0; i < Octaves; i++)
        {
            sum += (MathF.Abs(Source.Noise3D(x * freq, y * freq, z * freq)) * 2f - 1f) * amp;
            freq *= Lacunarity;
            amp *= Persistence;
        }
        return sum;
    }
}
