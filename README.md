# Nemoviz Book Reader

A Windows book reader built first and foremost for blind and partially sighted
readers — audiobooks, DAISY, EPUB, electronic braille and plain text, read by
speech, shown on screen, or felt on a braille display.

**[Download the latest release](https://github.com/gradic76/nemoviz-book-reader/releases/latest)**

---

## What it reads

**Audio** — a folder of MP3s as one continuous book, M4B with its chapters,
audio CDs, and 24 audio formats in all. A `.cue` sheet beside a single long
file becomes the chapter list.

**DAISY** — 2.02 and 3, audio, text, and the narrated text+audio hybrid where
the words follow the voice.

**Text** — EPUB (including narrated EPUB 3 with media overlays), FB2, MOBI and
AZW3, PDF, DOCX, ODT, RTF, HTML and plain text, read aloud by any speech voice
on the machine.

**Braille files** — `.brf`, `.brl`, `.bra` and Duxbury `.dxb`, back-translated
to text through liblouis. Croatian needed tables that did not exist, so NBR
ships two written from the official standard.

**Scans** — an image-only PDF or a photographed page is read by Windows' own
OCR, with no extra engine to install.

## How it is read

Speech through any SAPI 5, OneCore or 32-bit voice installed on the machine,
and optionally through Google Cloud or Azure. On screen in three modes, in a
font and colours the reader chooses. On a braille display, through the screen
reader the reader already uses — no drivers, no configuration.

All three follow **one position**, so what is spoken, what is highlighted and
what stands on the display are the same words.

## Accessibility is not a feature here, it is the design

Everything is reachable from the keyboard. Every announcement reaches JAWS and
NVDA without stealing focus. The interface speaks eleven languages and picks
yours from Windows on the first run: English, Croatian, Serbian in both
scripts, German, Russian, Spanish, Italian, Esperanto, Latin and Ancient Greek.

Press **F1** in the program for the manual, which is in all eleven too.

## Installing

Download the setup from the releases page and run it. Windows 10 or newer,
64-bit.

Windows SmartScreen will warn that the publisher is unknown — the installer is
not code-signed yet. Choose *More info* and then *Run anyway*.

## Building

Visual Studio with .NET Framework 4.8, built **x64** (libmpv is 64-bit and a
32-bit build cannot start). The installer is Inno Setup:

    ISCC.exe installer\nbr.iss

## Licence

GNU General Public License v3 — see [COPYING](COPYING).

NBR stands on other people's work, each used unmodified and each replaceable by
a compatible build of its own: **libmpv** and the FFmpeg inside it, **liblouis**
and its braille tables, the **NVDA Controller Client**, **TagLib#**, **PdfPig**
and **SharpCompress**, with the Andika, Atkinson Hyperlegible, Lexend, Luciole
and OpenDyslexic typefaces. Versions, copyright holders and where each licence
was read from are in `THIRD-PARTY-NOTICES.txt` beside the program.

---

Written by Gordan Radić — *Nemoguća vizija*, [nemoviz.org](https://nemoviz.org)

Built with Claude (Anthropic). Much of the code was written by it, to the
author's design, decisions and measurements; the copyright and the licence are
his.
