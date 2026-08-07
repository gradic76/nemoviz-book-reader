# Building an audio-only libmpv for NBR

NBR plays audio. The stock `libmpv-2.dll` is a video player's engine: measured on
the LGPL build we ship today, **93.6 MB, of which 74 MB is executable code**, and
almost all of it is video — libplacebo + shaderc + glslang + SPIRV-Cross + Vulkan
+ D3D11, dav1d, libvpx, hevc, prores, zimg, swscale, lcms2, libarchive, libssh,
Lua. **510 decoders, and 296 of them are video or image.**

This folder holds what a cut-down build needs and, more importantly, **what
proves it did not lose anything**.

## The rule this was built under

> Keep every audio format and filter available; do not narrow it to the ones NBR
> currently uses. — Gordan, 2026-07-30

A hand-picked list can silently drop the one codec a reader's book needs, and it
would surface on their machine, not ours. So the decoder list here is **derived
mechanically from FFmpeg's own classification**, never typed out from
`LibraryScanner.AudioExtensions`.

## Where the list comes from

1. `libavcodec/codec_desc.c` assigns every codec an `AVMEDIA_TYPE`. That, and not
   the section comments in `allcodecs.c`, is FFmpeg's authoritative answer:
   **223 codecs are `AVMEDIA_TYPE_AUDIO`**.
   *`allcodecs.c`'s `/* audio codecs */` marker is not usable for this — its
   "video" block is really an alphabetical list, and `rka` and `dvaudio` are
   audio codecs declared inside it.*
2. The DLL's own `decoder-list` gives (codec, driver) pairs. Every pair whose
   **codec** is one of those 223 contributes its **driver** — 214 decoders.
3. mpv's driver spelling is not always FFmpeg's symbol name. Eleven had to be
   translated, all confirmed present in `allcodecs.c`:

   | mpv driver | FFmpeg symbol | | mpv driver | FFmpeg symbol |
   |---|---|---|---|---|
   | `8svx_exp` | `eightsvx_exp` | | `g726le` | `adpcm_g726le` |
   | `8svx_fib` | `eightsvx_fib` | | `interplayacm` | `interplay_acm` |
   | `atrac3plus` | `atrac3p` | | `real_144` | `ra_144` |
   | `atrac3plusal` | `atrac3pal` | | `real_288` | `ra_288` |
   | `g722` | `adpcm_g722` | | `wavesynth` | `ffwavesynth` |
   | `g726` | `adpcm_g726` | | `acelp.kelvin` | `acelp_kelvin` |

   Every one of the 214 final names is checked to exist as `ff_<name>_decoder`
   before it goes in the configure line. Nothing is guessed.

## Files

- **`ffmpeg-enable-decoders.txt`** — the 214 names, one per line
- **`ffmpeg-enable-decoders.line`** — the same as a ready `--enable-decoder=…`
- **`oracle-decoders-baseline.txt`** — every decoder the current DLL reports.
  This is the test oracle: the new build must keep the audio ones and lose the
  rest.
- **`MpvInventory.cs`** — reads `decoder-list` out of a `libmpv-2.dll` through
  the C API. Compile with `csc /platform:x64`, run it beside the DLL.
- **`MpvCheck.cs`** — loads real files, applies the whole §8d filter chain and
  reports what mpv says. Point it at a folder of audio.

## Build configuration

Fork [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake)
(what zhongfly builds on) and run its GitHub Actions — a local mingw cross
toolchain is a much larger lift for the same artifact.

**FFmpeg**: `--disable-everything`, then the enable-line from this folder, plus
demuxers and parsers. Encoders, muxers, hwaccels, bitstream filters and devices
all go — NBR only ever decodes. Keep `--disable-gpl` **and `--disable-version3`**,
which is what `patch/0099-NBR-LGPL-audio-only.patch` does and what the shipped
DLL was built with: FFmpeg here is **LGPL 2.1**, and the MSIX relink wrinkle
(CLAUDE.md §10e) never applied to it. NBR decodes AMR natively (`amr_nb`/`amr_wb`
are in the list above), so the usual reason for version3 does not apply.

**Dispatch the workflow with `lgpl=false`.** `lgpl=true` reaches zhongfly's
`compile-lgpl-libmpv.patch`, which configures the same two files as 0099 and
conflicts with it; that leg has never built. 0099 alone does the whole job.

**mpv**: `-Dgpl=false -Dcplayer=false -Dlua=disabled -Djavascript=disabled
-Dlibarchive=disabled -Ddvdnav=disabled -Duchardet=disabled -Dgl=disabled
-Dvulkan=disabled -Dd3d11=disabled`, hwaccels off, `-Dwasapi=enabled`.

**libplacebo** subproject: Vulkan, OpenGL, D3D11, shaderc, glslang and lcms all
disabled. This is the single biggest win — shaderc/glslang/SPIRV-Cross are
notoriously large and exist only to compile shaders for `vo=gpu`, an output NBR
never opens.

**Two things that cannot be removed**, confirmed in mpv's `meson.build`: `libass`
(`dependency('libass', version: '>= 0.12.2')`, no `required:` and no feature
option) and `libplacebo`. They and freetype/harfbuzz stay, which is why the
estimate is **~28–32 MB** rather than the ~20 MB it first looked like.

## Verifying a new build — do all of this

1. **GPL scan.** Read the DLL as ASCII and count `x264 - core`,
   `videolan.org/x264`, `libx264`, `libdvdnav`, `dvd://`, `cdda://`. The first
   may appear twice and that is fine — it is FFmpeg's H.264 **SEI parser**
   reading a tag an encoder left in the bitstream. `libx264` and
   `x264_encoder_open` must be absent.
2. **Decoder diff against the oracle.** Run `MpvInventory` on the new DLL and
   compare with `oracle-decoders-baseline.txt`: every audio decoder still there,
   no video or image decoder left.
3. **Filter chain.** Run `MpvCheck` — it hands mpv the whole §8d graph with every
   stage switched on. Enumeration proves a filter exists; only building the graph
   proves mpv will accept it.
4. **Play files, with a CONTROL RUN of the old DLL side by side.** There are
   samples for only 13 of NBR's 24 formats on this machine, so this cannot be the
   main test — but it is what catches harness bugs. It already caught one:
   `Marshal.StringToHGlobalAnsi` converts to the system code page while **mpv
   takes UTF-8, always**, and three samples with `Č` and `Đ` in their names
   looked exactly like unsupported formats until the old DLL failed on the same
   three and no others.
5. **`scaletempo2`, WASAPI device enumeration and `ao=null`** must all still
   work. The last one is `MpvDuration`, and without it books import at 0:00.
6. **By ear.** An audio book, a DAISY book, sound processing on and off, and a
   sound-card change.

## Regenerating the lists

```
codec_desc.c  → the 223 AVMEDIA_TYPE_AUDIO codec names
decoder-list  → (codec, driver) pairs from the DLL being replaced
                keep the drivers whose codec is in that set
allcodecs.c   → confirm every name exists as ff_<name>_decoder
```
