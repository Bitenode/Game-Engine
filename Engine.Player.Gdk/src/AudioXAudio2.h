#pragma once

#include <cstdint>

namespace EnginePlayerGdk::Audio
{
    /// <summary>Initialize XAudio2 and a mastering voice. Returns false on failure.</summary>
    bool Initialize();

    void Shutdown();

    /// <summary>Play a short debug sine tone (proves the XAudio2 path).</summary>
    bool PlayTestTone(float seconds = 0.25f, float frequencyHz = 440.f);

    /// <summary>
    /// Placeholder for clip playback. Full PCM/WAV loading can share the same
    /// conceptual path as C# AudioBackend once assets are streamed to the GDK host.
    /// </summary>
    bool PlayClipPlaceholder(const char* logicalName);
}
