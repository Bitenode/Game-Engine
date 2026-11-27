#nullable enable
#if WINDOWS && !XBOX_UWP
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NAudio.Wave;

namespace Game_Engine.Core.Audio
{
    /// <summary>
    /// Zero-allocation-per-callback <see cref="ISampleProvider"/> that executes all enabled
    /// <see cref="AudioEffect"/> instances attached to an <see cref="AudioMixerGroup"/> in real time.
    /// Supports all 7 <see cref="AudioEffectType"/> processors:
    /// LowPassFilter, HighPassFilter, Echo, Reverb, Chorus, Distortion, and Compressor.
    /// </summary>
    public sealed class AudioMixerGroupProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioMixerGroup _group;
        private readonly int _sampleRate;

        // Pre-allocated slot list synced with _group.Effects only when effects are added/removed.
        private readonly List<EffectSlot> _slots = new(8);
        private int _syncedVersion = -1;

        public AudioMixerGroupProvider(ISampleProvider source, AudioMixerGroup group)
        {
            if (source.WaveFormat.Channels != 2)
                throw new ArgumentException("AudioMixerGroupProvider requires stereo (2-channel) IEEE float audio.", nameof(source));

            _source = source;
            _group = group;
            _sampleRate = source.WaveFormat.SampleRate;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;
        public AudioMixerGroup Group => _group;

        public int Read(float[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            int read = _source.Read(buffer);
            if (read <= 0) return read;

            // Sync DSP state slots only when the group's effect list changed.
            int ver = _group.EffectsVersion;
            if (_syncedVersion != ver || _slots.Count != _group.Effects.Count)
                SyncSlots(ver);

            int slotCount = _slots.Count;
            if (slotCount == 0)
                return read;

            var slice = buffer[..read];
            for (int i = 0; i < slotCount; i++)
            {
                var slot = _slots[i];
                var fx = slot.Effect;
                if (!fx.Enabled) continue;

                float wet = Math.Clamp(float.IsFinite(fx.WetMix) ? fx.WetMix : 0f, 0f, 1f);
                if (wet <= 1e-5f) continue;

                slot.Processor.Process(slice, fx, wet);
            }

            // Final safety guard: prevent any NaN/Infinity from poisoning the shared master mix.
            for (int i = 0; i < read; i++)
            {
                float s = slice[i];
                if (!float.IsFinite(s)) slice[i] = 0f;
            }

            return read;
        }

        private void SyncSlots(int version)
        {
            lock (_group.Effects)
            {
                var effects = _group.Effects;
                int count = effects.Count;

                // Fast check: exact same references in the same order -> nothing to rebuild.
                if (_slots.Count == count)
                {
                    bool same = true;
                    for (int i = 0; i < count; i++)
                    {
                        if (!ReferenceEquals(_slots[i].Effect, effects[i]) ||
                            _slots[i].Type != effects[i].Type)
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same)
                    {
                        _syncedVersion = version;
                        return;
                    }
                }

                // Reuse existing DSP processors for unchanged AudioEffect instances so delay/reverb tails persist.
                var next = new List<EffectSlot>(count);
                for (int i = 0; i < count; i++)
                {
                    var fx = effects[i];
                    EffectSlot? existing = null;
                    for (int j = 0; j < _slots.Count; j++)
                    {
                        if (ReferenceEquals(_slots[j].Effect, fx) && _slots[j].Type == fx.Type)
                        {
                            existing = _slots[j];
                            break;
                        }
                    }

                    next.Add(existing ?? new EffectSlot(fx, AudioEffectProcessor.Create(fx.Type, _sampleRate)));
                }

                _slots.Clear();
                _slots.AddRange(next);
                _syncedVersion = version;
            }
        }

        private sealed class EffectSlot
        {
            public readonly AudioEffect Effect;
            public readonly AudioEffectType Type;
            public readonly IDspEffect Processor;

            public EffectSlot(AudioEffect effect, IDspEffect processor)
            {
                Effect = effect;
                Type = effect.Type;
                Processor = processor;
            }
        }
    }

    /// <summary>Common interface for pre-allocated, zero-GC real-time stereo DSP processors.</summary>
    internal interface IDspEffect
    {
        void Process(Span<float> buffer, AudioEffect effect, float wet);
    }

    /// <summary>Factory and shared math helpers for the 7 real-time AudioMixer DSP effects.</summary>
    internal static class AudioEffectProcessor
    {
        public static IDspEffect Create(AudioEffectType type, int sampleRate) => type switch
        {
            AudioEffectType.LowPassFilter => new BiquadFilterDsp(sampleRate, isHighPass: false),
            AudioEffectType.HighPassFilter => new BiquadFilterDsp(sampleRate, isHighPass: true),
            AudioEffectType.Echo => new EchoDsp(sampleRate),
            AudioEffectType.Reverb => new ReverbDsp(sampleRate),
            AudioEffectType.Chorus => new ChorusDsp(sampleRate),
            AudioEffectType.Distortion => new DistortionDsp(),
            AudioEffectType.Compressor => new CompressorDsp(sampleRate),
            _ => new BypassDsp()
        };

        /// <summary>Guards DSP state variables against NaN, Infinity, and subnormal denormals.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sanitize(float x)
        {
            if (!float.IsFinite(x)) return 0f;
            if (MathF.Abs(x) < 1e-20f) return 0f;
            return x;
        }
    }

    internal sealed class BypassDsp : IDspEffect
    {
        public void Process(Span<float> buffer, AudioEffect effect, float wet) { }
    }

    /// <summary>
    /// Stereo biquad Low-Pass / High-Pass filter (Audio EQ Cookbook, Direct Form II Transposed).
    /// Parameters: CutoffHz, Resonance (Q).
    /// </summary>
    internal sealed class BiquadFilterDsp : IDspEffect
    {
        private readonly int _sampleRate;
        private readonly bool _isHighPass;

        private float _lastCutoff = -1f;
        private float _lastQ = -1f;
        private float _b0 = 1f, _b1, _b2, _a1, _a2;

        // Per-channel state
        private float _z1L, _z2L;
        private float _z1R, _z2R;

        public BiquadFilterDsp(int sampleRate, bool isHighPass)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            _isHighPass = isHighPass;
        }

        public void Process(Span<float> buffer, AudioEffect effect, float wet)
        {
            float defaultCutoff = _isHighPass ? 200f : 5000f;
            float maxCutoff = _sampleRate * 0.45f;
            float cutoff = Math.Clamp(effect.GetParameter("CutoffHz", defaultCutoff), 20f, maxCutoff);
            float q = Math.Clamp(effect.GetParameter("Resonance", 0.7071f), 0.1f, 20f);

            if (MathF.Abs(cutoff - _lastCutoff) > 0.01f || MathF.Abs(q - _lastQ) > 0.001f)
                UpdateCoefficients(cutoff, q);

            float dryMix = 1f - wet;
            float b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
            float z1L = _z1L, z2L = _z2L;
            float z1R = _z1R, z2R = _z2R;

            int end = buffer.Length & ~1;
            for (int i = 0; i < end; i += 2)
            {
                float inL = buffer[i];
                float yL = b0 * inL + z1L;
                z1L = AudioEffectProcessor.Sanitize(b1 * inL - a1 * yL + z2L);
                z2L = AudioEffectProcessor.Sanitize(b2 * inL - a2 * yL);
                buffer[i] = inL * dryMix + yL * wet;

                float inR = buffer[i + 1];
                float yR = b0 * inR + z1R;
                z1R = AudioEffectProcessor.Sanitize(b1 * inR - a1 * yR + z2R);
                z2R = AudioEffectProcessor.Sanitize(b2 * inR - a2 * yR);
                buffer[i + 1] = inR * dryMix + yR * wet;
            }

            _z1L = z1L; _z2L = z2L;
            _z1R = z1R; _z2R = z2R;
        }

        private void UpdateCoefficients(float cutoff, float q)
        {
            _lastCutoff = cutoff;
            _lastQ = q;

            float w0 = 2f * MathF.PI * cutoff / _sampleRate;
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / (2f * q);

            float b0, b1, b2;
            if (_isHighPass)
            {
                b0 = (1f + cosW0) * 0.5f;
                b1 = -(1f + cosW0);
                b2 = (1f + cosW0) * 0.5f;
            }
            else
            {
                b0 = (1f - cosW0) * 0.5f;
                b1 = 1f - cosW0;
                b2 = (1f - cosW0) * 0.5f;
            }

            float a0 = 1f + alpha;
            float a1 = -2f * cosW0;
            float a2 = 1f - alpha;
            float invA0 = 1f / MathF.Max(a0, 1e-6f);

            _b0 = b0 * invA0;
            _b1 = b1 * invA0;
            _b2 = b2 * invA0;
            _a1 = a1 * invA0;
            _a2 = a2 * invA0;
        }
    }

    /// <summary>
    /// Stereo circular-buffer Echo delay with feedback and wet/dry mix.
    /// Parameters: Delay (seconds, 0.005..2.0), Feedback (0..0.95).
    /// </summary>
    internal sealed class EchoDsp : IDspEffect
    {
        private readonly int _sampleRate;
        private readonly int _maxFrames;
        private readonly float[] _delayBuf; // interleaved L/R
        private int _writeFrame;

        public EchoDsp(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            _maxFrames = _sampleRate * 2; // up to 2 seconds
            _delayBuf = new float[_maxFrames * 2];
        }

        public void Process(Span<float> buffer, AudioEffect effect, float wet)
        {
            float delaySec = Math.Clamp(effect.GetParameter("Delay", 0.3f), 0.005f, 2.0f);
            float feedback = Math.Clamp(effect.GetParameter("Feedback", 0.5f), 0f, 0.95f);
            int delayFrames = Math.Clamp((int)(delaySec * _sampleRate), 1, _maxFrames - 1);

            float dryMix = 1f - wet;
            int write = _writeFrame;
            int max = _maxFrames;
            float[] delay = _delayBuf;

            int end = buffer.Length & ~1;
            for (int i = 0; i < end; i += 2)
            {
                int readFrame = write - delayFrames;
                if (readFrame < 0) readFrame += max;

                int rIdx = readFrame << 1;
                int wIdx = write << 1;

                float delL = delay[rIdx];
                float delR = delay[rIdx + 1];

                float inL = buffer[i];
                float inR = buffer[i + 1];

                delay[wIdx] = AudioEffectProcessor.Sanitize(inL + delL * feedback);
                delay[wIdx + 1] = AudioEffectProcessor.Sanitize(inR + delR * feedback);

                buffer[i] = inL * dryMix + delL * wet;
                buffer[i + 1] = inR * dryMix + delR * wet;

                write++;
                if (write >= max) write = 0;
            }

            _writeFrame = write;
        }
    }

    /// <summary>
    /// Lightweight Schroeder/Freeverb-style stereo reverb (4 damped comb filters + 2 series all-pass filters per channel).
    /// Parameters: DecayTime (0.1..10s), Density (0..1), Diffusion (0..1), optional RoomSize (0..1), Damping (0..0.95).
    /// </summary>
    internal sealed class ReverbDsp : IDspEffect
    {
        private const int NumCombs = 4;
        private const int NumAllPass = 2;
        private const int StereoSpread = 23;

        private static readonly int[] BaseCombLengths = { 1116, 1188, 1277, 1356 };
        private static readonly int[] BaseAllPassLengths = { 556, 441 };

        private readonly CombFilter[] _combsL = new CombFilter[NumCombs];
        private readonly CombFilter[] _combsR = new CombFilter[NumCombs];
        private readonly AllPassFilter[] _allPassL = new AllPassFilter[NumAllPass];
        private readonly AllPassFilter[] _allPassR = new AllPassFilter[NumAllPass];

        public ReverbDsp(int sampleRate)
        {
            double scale = Math.Max(8000, sampleRate) / 44100.0;
            for (int i = 0; i < NumCombs; i++)
            {
                int lenL = Math.Max(16, (int)(BaseCombLengths[i] * scale));
                int lenR = Math.Max(16, (int)((BaseCombLengths[i] + StereoSpread) * scale));
                _combsL[i] = new CombFilter(lenL);
                _combsR[i] = new CombFilter(lenR);
            }
            for (int i = 0; i < NumAllPass; i++)
            {
                int lenL = Math.Max(16, (int)(BaseAllPassLengths[i] * scale));
                int lenR = Math.Max(16, (int)((BaseAllPassLengths[i] + StereoSpread) * scale));
                _allPassL[i] = new AllPassFilter(lenL);
                _allPassR[i] = new AllPassFilter(lenR);
            }
        }

        public void Process(Span<float> buffer, AudioEffect effect, float wet)
        {
            float decayTime = Math.Clamp(effect.GetParameter("DecayTime", 1.5f), 0.1f, 10f);
            float roomSize = effect.GetParameter("RoomSize", -1f);
            float feedback = roomSize >= 0f
                ? Math.Clamp(0.5f + roomSize * 0.44f, 0.5f, 0.94f)
                : Math.Clamp(0.55f + 0.39f * (1f - MathF.Exp(-decayTime * 0.45f)), 0.5f, 0.94f);

            float density = Math.Clamp(effect.GetParameter("Density", 1f), 0.1f, 1f);
            float diffusion = Math.Clamp(effect.GetParameter("Diffusion", 1f), 0f, 1f);
            float damp = Math.Clamp(effect.GetParameter("Damping", 0.35f), 0.02f, 0.95f);
            float apGain = 0.5f * diffusion;

            float dryMix = 1f - wet;
            float combGain = 0.25f * density;

            int end = buffer.Length & ~1;
            for (int i = 0; i < end; i += 2)
            {
                float inL = buffer[i];
                float inR = buffer[i + 1];
                float monoIn = (inL + inR) * 0.5f;

                float sumL = 0f;
                float sumR = 0f;
                for (int c = 0; c < NumCombs; c++)
                {
                    sumL += _combsL[c].Process(monoIn, feedback, damp);
                    sumR += _combsR[c].Process(monoIn, feedback, damp);
                }

                float wetL = sumL * combGain;
                float wetR = sumR * combGain;

                for (int a = 0; a < NumAllPass; a++)
                {
                    wetL = _allPassL[a].Process(wetL, apGain);
                    wetR = _allPassR[a].Process(wetR, apGain);
                }

                buffer[i] = Math.Clamp(inL * dryMix + wetL * wet, -4f, 4f);
                buffer[i + 1] = Math.Clamp(inR * dryMix + wetR * wet, -4f, 4f);
            }
        }

        private sealed class CombFilter
        {
            private readonly float[] _buf;
            private int _idx;
            private float _filterStore;

            public CombFilter(int length) => _buf = new float[length];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float Process(float input, float feedback, float damp)
            {
                float output = _buf[_idx];
                _filterStore = AudioEffectProcessor.Sanitize(output * (1f - damp) + _filterStore * damp);
                _buf[_idx] = AudioEffectProcessor.Sanitize(input + _filterStore * feedback);
                _idx++;
                if (_idx >= _buf.Length) _idx = 0;
                return output;
            }
        }

        private sealed class AllPassFilter
        {
            private readonly float[] _buf;
            private int _idx;

            public AllPassFilter(int length) => _buf = new float[length];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public float Process(float input, float gain)
            {
                float bufOut = _buf[_idx];
                float output = -input + bufOut;
                _buf[_idx] = AudioEffectProcessor.Sanitize(input + bufOut * gain);
                _idx++;
                if (_idx >= _buf.Length) _idx = 0;
                return output;
            }
        }
    }

    /// <summary>
    /// Stereo modulated short delay Chorus with quadrature LFO (sine L / cosine R).
    /// Parameters: Rate / RateHz (Hz), Depth / DepthMs (ms), DelayMs (ms).
    /// </summary>
    internal sealed class ChorusDsp : IDspEffect
    {
        private readonly int _sampleRate;
        private readonly int _maxFrames;
        private readonly float[] _delayBuf; // interleaved L/R
        private int _writeFrame;
        private double _lfoPhase;

        public ChorusDsp(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            _maxFrames = (int)(_sampleRate * 0.06) + 8; // 60 ms max delay
            _delayBuf = new float[_maxFrames * 2];
        }

        public void Process(Span<float> buffer, AudioEffect effect, float wet)
        {
            float rate = effect.GetParameter("Rate", effect.GetParameter("RateHz", 1.5f));
            rate = Math.Clamp(rate, 0.05f, 10f);
            float depthMs = effect.GetParameter("Depth", effect.GetParameter("DepthMs", 3.5f));
            depthMs = Math.Clamp(depthMs, 0.1f, 15f);
            float baseDelayMs = Math.Clamp(effect.GetParameter("DelayMs", 15f), 5f, 35f);

            float baseDelayFrames = baseDelayMs * 0.001f * _sampleRate;
            float depthFrames = depthMs * 0.001f * _sampleRate;
            double phaseInc = 2.0 * Math.PI * rate / _sampleRate;

            float dryMix = 1f - wet;
            int write = _writeFrame;
            int max = _maxFrames;
            float[] delay = _delayBuf;
            double phase = _lfoPhase;

            int end = buffer.Length & ~1;
            for (int i = 0; i < end; i += 2)
            {
                float inL = buffer[i];
                float inR = buffer[i + 1];

                int wIdx = write << 1;
                delay[wIdx] = AudioEffectProcessor.Sanitize(inL);
                delay[wIdx + 1] = AudioEffectProcessor.Sanitize(inR);

                float lfoL = (float)Math.Sin(phase);
                float lfoR = (float)Math.Cos(phase);

                float dL = Math.Clamp(baseDelayFrames + depthFrames * lfoL, 1f, max - 2f);
                float dR = Math.Clamp(baseDelayFrames + depthFrames * lfoR, 1f, max - 2f);

                float wetL = ReadInterpolated(delay, write, dL, max, channelOffset: 0);
                float wetR = ReadInterpolated(delay, write, dR, max, channelOffset: 1);

                buffer[i] = inL * dryMix + wetL * wet;
                buffer[i + 1] = inR * dryMix + wetR * wet;

                write++;
                if (write >= max) write = 0;

                phase += phaseInc;
                if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }

            _writeFrame = write;
            _lfoPhase = phase;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ReadInterpolated(float[] delay, int writeFrame, float delayFrames, int maxFrames, int channelOffset)
        {
            float readPos = writeFrame - delayFrames;
            if (readPos < 0f) readPos += maxFrames;

            int idx0 = (int)readPos;
            float frac = readPos - idx0;
            int idx1 = idx0 + 1;
            if (idx1 >= maxFrames) idx1 = 0;

            float s0 = delay[(idx0 << 1) + channelOffset];
            float s1 = delay[(idx1 << 1) + channelOffset];
            return s0 + (s1 - s0) * frac;
        }
    }

    /// <summary>
    /// Soft-clipping <c>tanh</c> waveshaper distortion.
    /// Parameters: Drive (1..50), OutputGain (0.1..2.0).
    /// </summary>
    internal sealed class DistortionDsp : IDspEffect
    {
        public void Process(Span<float> buffer, AudioEffect effect, float wet)
        {
            float drive = Math.Clamp(effect.GetParameter("Drive", 4f), 1f, 50f);
            float outGain = Math.Clamp(effect.GetParameter("OutputGain", 1f), 0.1f, 2f);
            float norm = 1f / MathF.Max(MathF.Tanh(drive), 1e-4f);
            float scale = norm * outGain;
            float dryMix = 1f - wet;

            for (int i = 0; i < buffer.Length; i++)
            {
                float dry = buffer[i];
                float shaped = MathF.Tanh(dry * drive) * scale;
                buffer[i] = dry * dryMix + shaped * wet;
            }
        }
    }

    /// <summary>
    /// Stereo-linked dynamic range compressor with attack/release envelope follower and makeup gain.
    /// Parameters: Threshold (dB, -60..0), Ratio (1..20), AttackMs (0.1..500), ReleaseMs (5..2000), MakeupGain (dB, -12..24).
    /// </summary>
    internal sealed class CompressorDsp : IDspEffect
    {
        private readonly int _sampleRate;
        private float _envelope;

        public CompressorDsp(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
        }

        public void Process(Span<float> buffer, AudioEffect effect, float wet)
        {
            float thresholdDb = Math.Clamp(effect.GetParameter("Threshold", -20f), -60f, 0f);
            float ratio = Math.Clamp(effect.GetParameter("Ratio", 4f), 1f, 20f);
            float attackMs = Math.Clamp(effect.GetParameter("AttackMs", 10f), 0.1f, 500f);
            float releaseMs = Math.Clamp(effect.GetParameter("ReleaseMs", 100f), 5f, 2000f);
            float makeupDb = Math.Clamp(effect.GetParameter("MakeupGain", 0f), -12f, 24f);

            float attackCoeff = MathF.Exp(-1f / (attackMs * 0.001f * _sampleRate));
            float releaseCoeff = MathF.Exp(-1f / (releaseMs * 0.001f * _sampleRate));
            float invRatio = 1f / ratio;
            float dryMix = 1f - wet;
            float env = _envelope;

            int end = buffer.Length & ~1;
            for (int i = 0; i < end; i += 2)
            {
                float inL = buffer[i];
                float inR = buffer[i + 1];
                float peak = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));

                float coeff = peak > env ? attackCoeff : releaseCoeff;
                env = AudioEffectProcessor.Sanitize(coeff * env + (1f - coeff) * peak);

                float envDb = 20f * MathF.Log10(MathF.Max(env, 1e-6f));
                float gainReductionDb = 0f;
                if (envDb > thresholdDb)
                {
                    float overDb = envDb - thresholdDb;
                    gainReductionDb = overDb * (invRatio - 1f);
                }

                float totalGain = MathF.Pow(10f, (gainReductionDb + makeupDb) * 0.05f);
                float finalGain = dryMix + totalGain * wet;

                buffer[i] = inL * finalGain;
                buffer[i + 1] = inR * finalGain;
            }

            _envelope = env;
        }
    }
}
#endif
