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

        /// <summary>Voices with their vendor string (for grouping by engine) and the
        /// language they speak (a culture name like "hr-HR"). Either is "" when the
        /// voice exposes none.</summary>
        List<(string Name, string Vendor, string Language)> GetVoiceInfos();

        /// <summary>Friendly name of the voice currently selected.</summary>
        string CurrentVoiceName { get; }

        /// <summary>Selects a voice by friendly name (no-op if not found).</summary>
        void SelectVoice(string name);

        void SetRate(int rate);          // -10..10 (SAPI-style)
        void SetVolume(int volume);      // 0..100
        void SetPitch(int pitchPercent); // -50..50

        /// <summary>Routes speech to a specific output device. The id is the
        /// libmpv-style "wasapi/{guid}" (the guid is the shared WASAPI endpoint id,
        /// so it maps to the matching SAPI audio-output token); empty/"auto" means
        /// the system default. A backend that can't select a device ignores it.</summary>
        void SetAudioDevice(string deviceId);

        /// <summary>Starts speaking one chunk asynchronously.</summary>
        void Speak(string text);

        /// <summary>Hints the chunk that will be spoken next, so a backend that
        /// renders audio before playing it can have it ready and start without a
        /// gap. Backends that speak straight to the device ignore this.</summary>
        void PreRender(string text);

        void Pause();
        void Resume();
        /// <summary>Cancels the current utterance (Completed fires with cancelled=true).</summary>
        void Cancel();

        bool IsPaused { get; }

        /// <summary>Fires when an utterance ends; the bool is true if it was
        /// cancelled (a stop/seek) rather than finishing naturally.</summary>
        event Action<bool> Completed;
    }

    /// <summary>A backend that can keep what it makes.
    ///
    /// <para>Deliberately NOT part of <see cref="ISpeechBackend"/>. Speaking has
    /// no business knowing what a book is — three of the four backends neither
    /// need this nor could use it, and a synthesiser that is free and faster than
    /// listening has nothing worth storing. It is the cloud voices that are paid
    /// for once and should not be paid for twice.</para></summary>
    public interface ISpeechCacheAware
    {
        /// <summary>Where the book being read lives, so its speech can be kept
        /// beside it. Empty or null means keep nothing.</summary>
        string BookFolder { get; set; }
    }
}
