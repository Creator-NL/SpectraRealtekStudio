# Privacy Policy — Spectra Realtek Studio

**Spectra Realtek Studio** is built by **Biohazard Corp / Creator-NL** with a strict **Zero Telemetry** guarantee.

## What Data Spectra Realtek Studio Collects
**None.** Spectra Realtek Studio does not collect, transmit, store remotely, or share any personal data, audio streams, recordings, music library metadata, hardware identifiers, or usage analytics.

## Local-Only Operation
- **Zero Background Network Calls:** The application and CLI contain no analytics SDKs, crash telemetry reporters, or background update polls. The only optional network request is a single read-only call to GitHub Releases (`api.github.com/repos/Creator-NL/SpectraRealtekStudio/releases/latest`) *only* when you manually click **Settings (`⚙`) → Check for Updates...**.
- **Strict Local-Only Audio Processing:**
  - Header peak meters (`IAudioMeterInformation`) read only instantaneous scalar peak levels (`0.0..1.0`).
  - Tab 7 (**Visualizer**) reads real-time playback audio via Windows WASAPI Loopback strictly in RAM to render live 60 FPS FFT visuals while Tab 7 is visible, and immediately shuts down capture (`0%` CPU) when switching tabs, minimizing, pausing, or sending to tray.
  - Tab 8 (**Record** & **Sample & Loop Studio Editor**) only records or saves `.wav` audio files when you explicitly click **Record** or **Save**, writing solely to the local folder you choose on your PC (default: `%UserProfile%\Music\SpectraRecordings`). No audio is ever transmitted or uploaded anywhere.
  - Tab 9 (**DJ**) reads audio files strictly from folders you browse on your local PC and caches analyzed BPM/Key metadata locally in `%LocalAppData%\SpectraRealtekStudio\dj_track_cache_v2.json`.
- **Local Settings Only:**
  - App preferences (gain knob, auto-gain limiter ceiling/mode, visualizer palette/custom colors, tooltips & idle hints toggle, recording folder) are saved locally in `%AppData%\SpectraRealtekStudio\settings.json`.
  - Custom EQ presets are saved locally in `%AppData%\SpectraRealtekStudio\custom_eq_presets.json`.
  - Exported studio profiles and community diagnostic JSON reports are written only to the exact file path you explicitly choose in the Save dialog or CLI argument.
  - Audio DSP settings are written directly to Windows' local audio endpoint store via `RtkCfg64.dll` / `IPolicyConfig` RPC.

## Contact
If you have any privacy questions, please open an issue on GitHub or email **contact@biohazard-corp.nl**.
