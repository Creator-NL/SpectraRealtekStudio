# Changelog

All notable changes to **Spectra Realtek Studio** are documented in this file.

## [1.1.0] — 2026-11-23

Major Studio Expansion adding **Tab 8 (Lossless Studio Audio Recorder & Dedicated Sample/Loop Editor)**, **Tab 9 (Dual-Deck Pro DJ Mixing Studio with Bit-Pure Native Playback)**, **Full-Spectrum Frequency Sweeps (`5 Hz – 48 kHz`)**, and major **Tab 7 Visualizer** upgrades!

### Added
- **Tab 8 (`8. Record`) — Native WASAPI Studio Audio Recorder**:
  - Record **"🔊 What You Hear" (WASAPI Loopback)** from any playback endpoint or any hardware capture source (*Line In*, *Microphone*, *Stereo Mix*, USB interfaces).
  - Streams lossless **`16-bit PCM`**, **`24-bit PCM (Studio)`**, or **`32-bit IEEE Float`** WAV directly to disk at native sample rate (up to **`192 kHz`**) with Input Gain, Soft Limiter, and live waveform monitor.
  - **`Wait for Sound` Armed Trigger (`-48 / -40 / -30 / -20 dB`)** with a **30 ms pre-roll ring buffer** so the first transient is never clipped, plus `✕ Cancel` and `↻ New` instant restart.
- **Dedicated `Spectra Sample & Loop Studio` Popup Editor** (accessible from Tab 8 via `✂ Edit & Loop Selected`, `📂 Load & Edit Track...`, or right-clicking a recorded clip):
  - Loads recordings or any track from disk (`.wav`, `.flac`, `.mp3`, `.m4a`, `.aac`, `.ogg`, `.wma`, `.mp4`, etc.) at **100% original native sample rate** (up to `192 kHz`).
  - 60 FPS interactive stereo waveform with draggable green **`IN`** and amber **`OUT`** handles, mouse-wheel zoom, millisecond fine-tune nudges (`±1 / ±10 / ±50 ms`), and **`Zero-Crossing Snap`** for click-free loops.
  - Real-time gapless **`🔁 Loop Selection`** auditioning while live-adjusting `IN`/`OUT` markers, plus **`💾 Save Selected Loop / Part As...`**, **`💾 Save Full Edited Audio As...`**, and **`💾 Overwrite Current WAV`**.
  - Non-destructive **`✂ Cut Start & End (Keep Selection)`**, **`✂ Cut Start`**, **`✂ Cut End`**, **`🗑 Cut Selected Middle`** (with zero-crossing splice), **`◢ Fade In`**, **`◣ Fade Out`**, **`⚡ Normalize`**, and 18-level **`↶ Undo` / `↷ Redo` / `↺ Reset Original`**.
- **Tab 9 (`9. DJ`) — Dual-Deck Pro DJ Mixing Studio**:
  - **`☑ Load As-Is (Pure Native)`** playback engine: loads tracks at 100% native sample rate (up to `192,000 Hz`) with 1:1 integer sample pass-through at 0% pitch and unity gain.
  - 60 FPS dual stacked beat-matching waveforms (Deck A Cyan / Deck B Crimson) with mouse drag scrubbing, rotating Vinyl Scratch Platters (`VINYL` / `SLIP`), Pitch Fader & Keylock (`MT`), `CUE` / `▶ PLAY` / `SYNC`.
  - High-precision v2 continuous fractional-period Auto-BPM (`1T, 2T, 4T, 8T` comb filter on 200 Hz kick envelope) & Musical Key chroma analyzer with local cache.
  - 2-channel Pro Mixer with rotary `GAIN`, 3-Band **12 dB/oct Isolator EQ** (`HIGH`/`MID`/`LOW` with total kill), Bipolar `FILTER`, Volume Faders, 16-segment LED VU meters, `🎧` PFL, Crossfader, 3-Slot FX Rack (`Flanger`, `Reverb`, `WahWah`), 4 Hot Cues, Beat Loop rack, 6 Stem/Isolator Pads, `⇄ Auto-Mix`, `● REC MIX`, Local Music Crate Browser, and `⛶ Focus` cinema mode.
- **Tab 3 & Tab 7 Full-Spectrum Acoustic Test Generator**:
  - Added logarithmic **`5 Hz – 20 kHz`** and **`5 Hz – 48 kHz`** frequency sweeps (up to 96/192 kHz), **Pink & White Noise**, and a logarithmic **Pure Tone Generator (`5 Hz – 48 kHz`)** plus quick `▶ Sweep` button on Tab 7.
- **Tab 7 (`7. Visualizer`) Major Upgrades**:
  - Expanded from 6 to **10 real-time 60 FPS modes** (*Spectrum Bars*, *Color Waveform Bars*, *Neon Liquid Aurora*, *Peak Contour*, *Starfield Bass Tunnel*, *Mirror Spectrum Wave*, *Stereo Oscilloscope*, *Spectrogram Waterfall*, *Stereo Vectorscope*, *Radial Cyber Halo*).
  - Expanded to **6 frequency ranges** (`0–20 kHz Default`, `0–20 kHz Linear`, `5–24 kHz Studio`, `5–48 kHz Hi-Res`, `0–48 kHz Linear`, `20–20 kHz EQ`) and **8 built-in palettes + `Custom Colors...`**, sideways **`Color Flow`**, and **`⛶ Focus`** cinema mode.
  - Added **Dynamic Encoder Rolloff Recovery** (`11.0–22.5 kHz` brickwall detection + level-anchored raised-cosine harmonic continuity) so MP3/AAC tracks display cleanly across all ranges with zero dead gap or step wall.
  - Added `Show Startup Hints & Idle Notices` toggle in Settings (`⚙`) and pixel anti-collision guard on frequency axis labels.
- Expanded automated hardware & DSP verification suite (`SpectraRealtekStudio.Cli.exe test`) to **11/11 checks**.

### Fixed
- Fixed WAV chunk parser loop guard when loading certain multi-chunk WAV files (`"fmt "` chunk boundary).
- Fixed Windows scheduler timing (`timeBeginPeriod(1)` + 50 ms pre-filled WASAPI buffer) to eliminate low-level render underruns at high sample rates (`192 kHz`).
- Fixed WPF initialization event guard during startup before bottom status bar instantiation.

---

## [1.0.1] — 2026-11-21

### Added
- **Tab 7 Live 60 FPS Audio Visualizer** (`5 Hz – 48 kHz`, 8192-pt Radix-2 FFT, up to 256 bands, 0% CPU idle).
- **Header Master Pre-Mix Gain Knob (`-24..+12 dB`) & Auto-Gain Anti-Clip Limiter** (`70%–95%` ceiling guard).
- **Webpage-Style Neon Equalizer UI** with Catmull-Rom curve and one-click preset pill chips.
- **Live 850 ms Kernel Jack-Sense & Instant Non-Admin Jack Retasking** via `RtCOM64.dll` (`IRtkPropSet20146`).
- **Settings (`⚙`) Menu** with tooltips toggle and manual GitHub Releases update checker.

---

## [1.0.0] — 2026-11-20

### Added
- Initial public MIT open-source release of **Spectra Realtek Studio** (6 core Realtek HDA & `RltkAPO64.dll` control modules + CLI + Inno Setup 6 Installer & Portable ZIP).
