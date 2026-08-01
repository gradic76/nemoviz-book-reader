using System;
using System.Runtime.InteropServices;

namespace Nemoviz_Book_Reader
{
    /// <summary>Holds the chosen sound card awake, so it cannot power down
    /// between sentences and swallow the start of the next one.
    ///
    /// <para><b>Why this exists (2026-08-01, §10f).</b> Gordan lost the first
    /// word of sentence after sentence, and every measurement said the software
    /// was correct. His HDMI output powers down almost the instant the signal
    /// stops, and it had been kept awake all along by NVDA and JAWS, which were
    /// on the same card. Moving the screen readers to another card removed that
    /// protection and the endpoint began sleeping in the gaps. A player must not
    /// depend on a screen reader for this.</para>
    ///
    /// <para><b>Why waveOut and not SAPI.</b> The first version rode on the same
    /// <see cref="SapiWavPlayer"/> the reading uses, and Gordan heard it
    /// immediately: it took TURNS with the reading instead of lying underneath
    /// it — a keep-alive inserted between sentences rather than mixed with them.
    /// Two SAPI voices pointed at one output token queue behind each other
    /// rather than mixing, so anything built on SAPI would do the same. waveOut
    /// opens a stream of its own that the Windows mixer combines with everything
    /// else, which is the whole requirement.</para>
    ///
    /// <para><b>One buffer, looped by the driver.</b> <c>WHDR_BEGINLOOP</c> with
    /// an infinite loop count means the audio never stops and nothing has to
    /// re-queue it — no timer, and no seam that could itself become the gap
    /// being prevented.</para>
    ///
    /// <para><b>Not digital silence.</b> A run of zeroes is, to a device deciding
    /// whether anything is happening, indistinguishable from nothing at all. This
    /// is a 40 Hz sine at one part in 4 000 of full scale — around −72 dBFS and
    /// below 50 Hz, so it is real signal on the wire and under the floor of
    /// anything a person would hear a book through.</para></summary>
    internal sealed class AudioKeepAlive : IDisposable
    {
        private const int Rate = 8000;          // plenty for a sub-bass tone
        private const int Seconds = 1;          // the driver loops it forever

        [DllImport("winmm.dll")] private static extern int waveOutOpen(
            out IntPtr hwo, int deviceId, byte[] fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int waveOutGetDevCapsW(IntPtr id, out WAVEOUTCAPS caps, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEOUTCAPS
        {
            public short wMid, wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats;
            public short wChannels;
            public short wReserved1;
            public int dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        private const int WAVE_MAPPER = -1;
        private const int WHDR_BEGINLOOP = 0x00000004, WHDR_ENDLOOP = 0x00000008;

        private IntPtr device, header, audio;
        private string deviceDescription;
        private bool running;

        /// <summary>The card to hold open, named the way the rest of NBR names
        /// one. waveOut knows devices by index and by a product name truncated to
        /// 31 characters, so the match is by name against what mpv reported —
        /// imperfect by construction, and it falls back to the default device,
        /// which is the right answer when there is only one.</summary>
        public void SetDeviceDescription(string description)
        {
            string want = string.IsNullOrEmpty(description) ? null : description;
            if (deviceDescription == want) return;
            deviceDescription = want;
            if (running) { Stop(); Start(); }        // move it to the new card
        }

        private int FindDevice()
        {
            if (string.IsNullOrEmpty(deviceDescription)) return WAVE_MAPPER;
            try
            {
                int n = waveOutGetNumDevs();
                for (int i = 0; i < n; i++)
                {
                    WAVEOUTCAPS c;
                    if (waveOutGetDevCapsW((IntPtr)i, out c, Marshal.SizeOf(typeof(WAVEOUTCAPS))) != 0) continue;
                    string name = c.szPname ?? "";
                    // waveOut truncates to 31 characters, so compare on the part
                    // that survives rather than expecting the two to be equal.
                    if (name.Length > 0 &&
                        (deviceDescription.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf(deviceDescription, StringComparison.OrdinalIgnoreCase) >= 0))
                        return i;
                }
            }
            catch { }
            return WAVE_MAPPER;
        }

        public void Start()
        {
            if (running) return;
            try
            {
                byte[] fmt = WaveFormat(Rate);
                if (waveOutOpen(out device, FindDevice(), fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                { device = IntPtr.Zero; return; }

                byte[] pcm = BuildTone();
                audio = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, audio, pcm.Length);

                WAVEHDR h = new WAVEHDR
                {
                    lpData = audio,
                    dwBufferLength = pcm.Length,
                    dwFlags = WHDR_BEGINLOOP | WHDR_ENDLOOP,
                    dwLoops = int.MaxValue,          // the driver repeats it; we never re-queue
                };
                header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                Marshal.StructureToPtr(h, header, false);

                int size = Marshal.SizeOf(typeof(WAVEHDR));
                if (waveOutPrepareHeader(device, header, size) != 0) { Cleanup(); return; }
                if (waveOutWrite(device, header, size) != 0) { Cleanup(); return; }
                running = true;
            }
            catch { Cleanup(); }
        }

        public void Stop()
        {
            if (!running && device == IntPtr.Zero) return;
            running = false;
            Cleanup();
        }

        private void Cleanup()
        {
            try { if (device != IntPtr.Zero) waveOutReset(device); } catch { }
            try
            {
                if (device != IntPtr.Zero && header != IntPtr.Zero)
                    waveOutUnprepareHeader(device, header, Marshal.SizeOf(typeof(WAVEHDR)));
            }
            catch { }
            try { if (device != IntPtr.Zero) waveOutClose(device); } catch { }
            try { if (header != IntPtr.Zero) Marshal.FreeHGlobal(header); } catch { }
            try { if (audio != IntPtr.Zero) Marshal.FreeHGlobal(audio); } catch { }
            device = header = audio = IntPtr.Zero;
        }

        /// <summary>WAVEFORMATEX for 16-bit mono PCM, as the 18 bytes waveOutOpen
        /// wants.</summary>
        private static byte[] WaveFormat(int rate)
        {
            var b = new byte[18];
            BitConverter.GetBytes((short)1).CopyTo(b, 0);        // WAVE_FORMAT_PCM
            BitConverter.GetBytes((short)1).CopyTo(b, 2);        // mono
            BitConverter.GetBytes(rate).CopyTo(b, 4);
            BitConverter.GetBytes(rate * 2).CopyTo(b, 8);        // byte rate
            BitConverter.GetBytes((short)2).CopyTo(b, 12);       // block align
            BitConverter.GetBytes((short)16).CopyTo(b, 14);      // bits
            BitConverter.GetBytes((short)0).CopyTo(b, 16);       // cbSize
            return b;
        }

        /// <summary>A whole number of 40 Hz cycles, so the loop joins without a
        /// click.</summary>
        internal static byte[] BuildTone()
        {
            int samples = Rate * Seconds;
            var pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                short v = (short)(8.0 * Math.Sin(2.0 * Math.PI * 40.0 * i / Rate));
                pcm[i * 2] = (byte)(v & 0xFF);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return pcm;
        }

        public void Dispose() { Stop(); }
    }
}
