# Privacy Policy — Spectra Realtek Studio

**Spectra Realtek Studio** is built by **Biohazard Corp / Creator-NL** with a strict **Zero Telemetry** guarantee.

## What Data Spectra Realtek Studio Collects
**None.** Spectra Realtek Studio does not collect, transmit, store remotely, or share any personal data, audio streams, hardware identifiers, or usage analytics.

## Local-Only Operation
- **Zero Background Network Calls:** The application and CLI contain no analytics SDKs, crash telemetry reporters, or background update polls. The only optional network request is a single read-only call to GitHub Releases (`api.github.com/repos/Creator-NL/SpectraRealtekStudio/releases/latest`) *only* when you manually click **Settings (`⚙`) → Check for Updates...**.
- **No Audio Recording or Storage:** Header peak meters (`IAudioMeterInformation`) read only instantaneous scalar peak levels (`0.0..1.0`). Tab 7 (**Visualizer**) reads real-time playback audio via Windows WASAPI Loopback strictly in RAM to render live 60 FPS FFT visuals while Tab 7 is visible, and immediately shuts down capture (`0%` CPU) when switching tabs, minimizing, pausing, or sending to tray. No audio is ever recorded, saved to disk, or transmitted.
- **Local Settings Only:**
  - App preferences (gain knob, auto-gain limiter ceiling/mode, tooltips toggle) are saved locally in `%AppData%\SpectraRealtekStudio\settings.json`.
  - Custom EQ presets are saved locally in `%AppData%\SpectraRealtekStudio\custom_eq_presets.json`.
  - Exported studio profiles and community diagnostic JSON reports are written only to the exact file path you explicitly choose in the Save dialog or CLI argument.
  - Audio DSP settings are written directly to Windows' local audio endpoint store via `RtkCfg64.dll` / `IPolicyConfig` RPC.

## Contact
If you have any privacy questions, please open an issue on GitHub or email **contact@biohazard-corp.nl**.
