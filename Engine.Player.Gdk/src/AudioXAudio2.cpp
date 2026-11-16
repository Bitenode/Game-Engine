#include "AudioXAudio2.h"

#include <cmath>
#include <vector>

#include <xaudio2.h>
#include <wrl/client.h>

#pragma comment(lib, "xaudio2.lib")

using Microsoft::WRL::ComPtr;

namespace EnginePlayerGdk::Audio
{
    static ComPtr<IXAudio2> s_xaudio;
    static IXAudio2MasteringVoice* s_master = nullptr;
    static bool s_ready = false;

    bool Initialize()
    {
        if (s_ready) return true;

        HRESULT hr = XAudio2Create(s_xaudio.GetAddressOf(), 0, XAUDIO2_DEFAULT_PROCESSOR);
        if (FAILED(hr) || !s_xaudio)
            return false;

        hr = s_xaudio->CreateMasteringVoice(&s_master);
        if (FAILED(hr) || !s_master)
        {
            s_xaudio.Reset();
            return false;
        }

        s_ready = true;
        return true;
    }

    void Shutdown()
    {
        if (s_master)
        {
            s_master->DestroyVoice();
            s_master = nullptr;
        }
        s_xaudio.Reset();
        s_ready = false;
    }

    bool PlayTestTone(float seconds, float frequencyHz)
    {
        if (!Initialize() || !s_xaudio || !s_master)
            return false;

        constexpr int sampleRate = 48000;
        const int sampleCount = static_cast<int>(seconds * sampleRate);
        if (sampleCount <= 0) return false;

        std::vector<int16_t> pcm(static_cast<size_t>(sampleCount));
        for (int i = 0; i < sampleCount; ++i)
        {
            const float t = static_cast<float>(i) / static_cast<float>(sampleRate);
            const float s = std::sin(2.f * 3.14159265f * frequencyHz * t);
            pcm[static_cast<size_t>(i)] = static_cast<int16_t>(s * 0.2f * 32767.f);
        }

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_PCM;
        format.nChannels = 1;
        format.nSamplesPerSec = sampleRate;
        format.wBitsPerSample = 16;
        format.nBlockAlign = format.nChannels * format.wBitsPerSample / 8;
        format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

        IXAudio2SourceVoice* voice = nullptr;
        HRESULT hr = s_xaudio->CreateSourceVoice(&voice, &format);
        if (FAILED(hr) || !voice)
            return false;

        XAUDIO2_BUFFER buffer{};
        buffer.AudioBytes = static_cast<UINT32>(pcm.size() * sizeof(int16_t));
        buffer.pAudioData = reinterpret_cast<const BYTE*>(pcm.data());
        buffer.Flags = XAUDIO2_END_OF_STREAM;

        hr = voice->SubmitSourceBuffer(&buffer);
        if (FAILED(hr))
        {
            voice->DestroyVoice();
            return false;
        }

        voice->Start();
        // Keep the buffer alive while the voice plays (short tone).
        Sleep(static_cast<DWORD>(seconds * 1000.f) + 50);
        voice->DestroyVoice();
        return true;
    }

    bool PlayClipPlaceholder(const char* logicalName)
    {
        (void)logicalName;
        return PlayTestTone(0.15f, 660.f);
    }
}
