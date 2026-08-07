using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Nemoviz_Book_Reader
{
    /// <summary>Reading a Red Book audio CD, without asking anything else to do
    /// it for us.
    ///
    /// <para><b>Why NBR does this itself.</b> mpv can play <c>cdda://</c>, but
    /// only when it is built against <b>libcdio, which is GPL-3</b> — verified,
    /// not assumed. NBR's whole libmpv story (§10e) is that the build must stay
    /// LGPL, because a GPL libmpv would make NBR's own distribution a GPL matter;
    /// that is why libx264 and libdvdnav went. So the shipped DLL has no
    /// <c>cdda://</c> at all: <c>libcdio</c>, <c>cdio</c> and <c>cdparanoia</c>
    /// appear in it exactly zero times. Reading the disc here costs one file of
    /// P/Invoke against kernel32 and adds no dependency and no obligation.</para>
    ///
    /// <para><b>All of this was measured before it was written</b>, against a
    /// mounted audio CD: the table of contents comes back with eight tracks and
    /// their true lengths, <c>IOCTL_CDROM_RAW_READ</c> accepts at least 100
    /// sectors a call, the bytes are real music (peak 30572, 99 % not silence),
    /// and NBR's own libmpv opens the resulting WAV and reports 10.00 s at
    /// 44100 Hz. The one failure on the way was asking for 750 sectors at once —
    /// ERROR_INVALID_PARAMETER, which is what a transfer over the driver's limit
    /// looks like.</para>
    ///
    /// <para><b>THE DRIVE MUST BE AS QUIET AS POSSIBLE (Gordan, 2026-08-07)</b>,
    /// and that shapes everything here. An optical drive is audible on its own,
    /// and a worn disc makes it worse. The rule that follows is counterintuitive:
    /// <b>read fast, in one unbroken forward pass, and stop</b>. Reading slowly to
    /// be quieter per second keeps the motor running for half an hour; reading at
    /// the drive's own speed finishes a disc in a few minutes and then the drive
    /// spins down and is silent for the rest of the book. Seeking backwards is
    /// what makes the ugly noise, so the reader never does — it walks forward
    /// once, from the first sector to the last it needs.</para></summary>
    public static class OpticalDrive
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_STORAGE_CHECK_VERIFY = 0x2D4800;
        private const uint IOCTL_CDROM_READ_TOC_EX = 0x24054;
        /// <summary>CTL_CODE(FILE_DEVICE_CD_ROM, 0x0F, METHOD_OUT_DIRECT, FILE_READ_ACCESS)</summary>
        private const uint IOCTL_CDROM_RAW_READ = 0x2403E;

        /// <summary>Bytes in one CDDA sector — 588 stereo 16-bit frames.</summary>
        public const int RawSectorBytes = 2352;
        /// <summary>Sectors in one second. The whole format is built on it.</summary>
        public const int SectorsPerSecond = 75;
        /// <summary>Bytes of PCM in one second: 44100 × 2 channels × 2 bytes.</summary>
        public const int BytesPerSecond = 176400;

        /// <summary>How many sectors to ask for at once. The probe accepted 100
        /// on the test drive, but drivers differ and the failure mode is a flat
        /// ERROR_INVALID_PARAMETER rather than a short read, so
        /// <see cref="LargestChunk"/> finds the real ceiling per drive instead of
        /// trusting this. Sequential and large is what keeps the motor steady.</summary>
        private static readonly int[] ChunkLadder = { 100, 75, 55, 27, 26, 13, 8, 1 };

        [StructLayout(LayoutKind.Sequential)]
        private struct RawReadInfo
        {
            public long DiskOffset;      // LBA × 2048, by the API's own convention
            public uint SectorCount;
            public int TrackMode;        // 2 = CDDA
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share,
                                                 IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
                                                   byte[] outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, ref RawReadInfo inBuf, int inSize,
                                                   byte[] outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>One track: where it starts, how long it is, and whether it is
        /// audio at all — a mixed-mode disc has a data track we must not read as
        /// sound.</summary>
        public sealed class Track
        {
            public int Number;
            public int StartSector;
            public int Sectors;
            public bool IsAudio;
            public double Seconds { get { return Sectors / (double)SectorsPerSecond; } }
        }

        /// <summary>Drive letters that are optical drives, whether or not anything
        /// is in them. Cheap enough to call while a dialog is being built, and it
        /// is what decides whether the reader is offered the feature at all.</summary>
        public static List<string> Drives()
        {
            var found = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                    if (d.DriveType == DriveType.CDRom && d.Name.Length >= 2)
                        found.Add(d.Name.Substring(0, 2));
            }
            catch { }
            return found;
        }

        public static bool AnyDrive() { return Drives().Count > 0; }

        /// <summary>True if that drive currently holds readable media. Never
        /// throws; an empty drive answers false rather than failing.</summary>
        public static bool HasDisc(string drive)
        {
            IntPtr h = Open(drive);
            if (h == IntPtr.Zero) return false;
            try
            {
                int ret;
                return DeviceIoControl(h, IOCTL_STORAGE_CHECK_VERIFY, new byte[0], 0,
                                       new byte[0], 0, out ret, IntPtr.Zero);
            }
            catch { return false; }
            finally { CloseHandle(h); }
        }

        /// <summary>The disc's table of contents, or an empty list. The lead-out
        /// entry is not returned as a track — it is only there to give the last
        /// real track its length.</summary>
        public static List<Track> ReadToc(string drive)
        {
            var tracks = new List<Track>();
            IntPtr h = Open(drive);
            if (h == IntPtr.Zero) return tracks;
            try
            {
                int ret;
                if (!DeviceIoControl(h, IOCTL_STORAGE_CHECK_VERIFY, new byte[0], 0, new byte[0], 0, out ret, IntPtr.Zero))
                    return tracks;

                // Format 0 = TOC, addresses as LBA (the MSF bit stays clear).
                byte[] req = new byte[4];
                req[0] = 0;
                req[1] = 1;                      // session/track to start from
                byte[] buf = new byte[4 + 8 * 100];
                if (!DeviceIoControl(h, IOCTL_CDROM_READ_TOC_EX, req, req.Length, buf, buf.Length, out ret, IntPtr.Zero))
                    return tracks;

                int len = (buf[0] << 8) | buf[1];
                int entries = (len - 2) / 8;
                if (entries < 2) return tracks;  // nothing but a lead-out

                for (int i = 0; i < entries - 1; i++)
                {
                    int o = 4 + i * 8, p = 4 + (i + 1) * 8;
                    int number = buf[o + 2];
                    if (number == 0xAA) continue;                 // lead-out
                    int start = Lba(buf, o), next = Lba(buf, p);
                    if (next <= start) continue;
                    tracks.Add(new Track
                    {
                        Number = number,
                        StartSector = start,
                        Sectors = next - start,
                        IsAudio = (buf[o + 1] & 0x04) == 0        // control bit 2 set = data
                    });
                }
            }
            catch { tracks.Clear(); }
            finally { CloseHandle(h); }
            return tracks;
        }

        private static int Lba(byte[] b, int o)
        {
            return (b[o + 4] << 24) | (b[o + 5] << 16) | (b[o + 6] << 8) | b[o + 7];
        }

        private static IntPtr Open(string drive)
        {
            try
            {
                IntPtr h = CreateFileW(@"\\.\" + drive, GENERIC_READ,
                                       FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                                       OPEN_EXISTING, 0, IntPtr.Zero);
                return h == (IntPtr)(-1) ? IntPtr.Zero : h;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>The biggest chunk this drive will hand over in one call, found
        /// by asking rather than assumed: a request over the driver's limit fails
        /// outright with ERROR_INVALID_PARAMETER instead of returning less.</summary>
        private static int LargestChunk(IntPtr h, int atSector)
        {
            foreach (int n in ChunkLadder)
            {
                byte[] probe = new byte[n * RawSectorBytes];
                if (ReadRaw(h, atSector, n, probe) > 0) return n;
            }
            return 0;
        }

        private static int ReadRaw(IntPtr h, int lba, int sectors, byte[] outBuf)
        {
            var info = new RawReadInfo
            {
                DiskOffset = (long)lba * 2048,
                SectorCount = (uint)sectors,
                TrackMode = 2                     // CDDA
            };
            int got;
            if (!DeviceIoControl(h, IOCTL_CDROM_RAW_READ, ref info, Marshal.SizeOf(typeof(RawReadInfo)),
                                 outBuf, sectors * RawSectorBytes, out got, IntPtr.Zero))
                return 0;
            return got;
        }

        /// <summary>Writes one track to a 16-bit 44.1 kHz stereo WAV, in a single
        /// forward pass.
        ///
        /// <para><paramref name="progress"/> is called with the seconds written so
        /// far, so the caller can start playing before the read has finished — the
        /// whole reason the pass is forward-only and never revisits a sector.
        /// Returns false if the drive refused, and leaves no half file behind.</para></summary>
        public static bool RipTrack(string drive, Track track, string wavPath,
                                    Action<double> progress = null,
                                    Func<bool> cancelled = null)
        {
            if (track == null || !track.IsAudio || track.Sectors <= 0) return false;
            IntPtr h = Open(drive);
            if (h == IntPtr.Zero) return false;
            FileStream fs = null;
            try
            {
                int chunk = LargestChunk(h, track.StartSector);
                if (chunk == 0) return false;

                fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
                WriteWavHeader(fs, track.Sectors * RawSectorBytes);

                byte[] buf = new byte[chunk * RawSectorBytes];
                int end = track.StartSector + track.Sectors;
                for (int lba = track.StartSector; lba < end; lba += chunk)
                {
                    if (cancelled != null && cancelled()) { fs.Dispose(); fs = null; TryDelete(wavPath); return false; }
                    int n = Math.Min(chunk, end - lba);
                    int got = ReadRaw(h, lba, n, buf);
                    if (got <= 0)
                    {
                        // A read error mid-disc is a scratch, not a reason to lose
                        // what came before: the rest is written as silence so the
                        // timeline still matches the track, and playback goes on.
                        Array.Clear(buf, 0, n * RawSectorBytes);
                        got = n * RawSectorBytes;
                    }
                    fs.Write(buf, 0, got);
                    if (progress != null)
                        progress((lba + n - track.StartSector) / (double)SectorsPerSecond);
                }
                fs.Flush();
                return true;
            }
            catch { TryDelete(wavPath); return false; }
            finally
            {
                if (fs != null) fs.Dispose();
                CloseHandle(h);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void WriteWavHeader(Stream s, int dataBytes)
        {
            var w = new BinaryWriter(s);
            w.Write(new char[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write((short)2);          // stereo
            w.Write(44100);
            w.Write(44100 * 4);         // byte rate
            w.Write((short)4);          // block align
            w.Write((short)16);         // bits
            w.Write(new char[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
            w.Flush();
        }
    }
}
