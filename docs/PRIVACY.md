# Privacy Policy — Spectra Realtek Studio

**Spectra Realtek Studio** is built by **Biohazard Corp / Creator-NL** with a strict **Zero Telemetry** guarantee.

## What Data Spectra Realtek Studio Collects
**None.** Spectra Realtek Studio does not collect, transmit, store remotely, or share any personal data, audio streams, hardware identifiers, or usage analytics.

## Local-Only Operation
- **Zero Network Calls:** The application and CLI contain no HTTP clients, analytics SDKs, crash telemetry reporters, or background update checks.
- **No Audio Recording or Capture:** Peak meters (`IAudioMeterInformation`) read only instantaneous scalar peak levels (`0.0..1.0`) from Windows CoreAudio to animate the UI L/R bars; no audio samples are ever recorded, buffered, or stored.
- **Local Settings Only:**
  - Custom EQ presets are saved locally in `%AppData%\SpectraRealtekStudio\custom_eq_presets.json`.
  - Exported studio profiles and community diagnostic JSON reports are written only to the exact file path you explicitly choose in the Save dialog or CLI argument.
  - Audio DSP settings are written directly to Windows' local audio endpoint store via `RtkCfg64.dll` / `IPolicyConfig` RPC.

## Contact
If you have any privacy questions, please open an issue on GitHub or email **contact@biohazard-corp.nl**.
