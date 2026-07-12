using System;
using System.Collections.Generic;

namespace Nemoviz_Book_Reader
{
    /// <summary>A text-to-speech backend: one source of voices plus the ability
    /// to speak a chunk of text and control playback. This is the abstraction
    /// that lets NBR mirror the way JAWS exposes several speech backends side by
    /// side — "SAPI 5 x64" (in-process), OneCore natural voices (WinRT), and a
    /// 32-bit "SAPI 5" host for legacy voices — behind one interface. Only the
    /// in-process SAPI5 backend exists so far; the others slot in here later.
    ///
    /// The <see cref="TtsReader"/> orchestrates sentence/paragraph navigation and
    /// position on top of whichever backend owns the selected voice; a backend
    /// only ever speaks one chunk at a time and reports when it finishes.</summary>
    public interface ISpeechBackend : IDisposable
    {
        /// <summary>Friendly names of the voices this backend offers.</summary>
        List<string> GetVoices();

        /// <summary>Friendly name of the voice currently selected.</summary>
        string CurrentVoiceName { get; }

        /// <summary>Selects a voice by friendly name (no-op if not found).</summary>
        void SelectVoice(string name);

        void SetRate(int rate);          // -10..10 (SAPI-style)
        void SetVolume(int volume);      // 0..100
        void SetPitch(int pitchPercent); // -50..50

        /// <summary>Starts speaking one chunk asynchronously.</summary>
        void Speak(string text);

        void Pause();
        void Resume();
        /// <summary>Cancels the current utterance (Completed fires with cancelled=true).</summary>
        void Cancel();

        bool IsPaused { get; }

        /// <summary>Fires when an utterance ends; the bool is true if it was
        /// cancelled (a stop/seek) rather than finishing naturally.</summary>
        event Action<bool> Completed;
    }
}
