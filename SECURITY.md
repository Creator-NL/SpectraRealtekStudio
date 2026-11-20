# Spectra Realtek Studio Security Architecture & Policy

**Project:** Spectra Realtek Studio  
**Maintainer:** Biohazard Corp / Creator-NL  
**Repository:** https://github.com/Creator-NL/SpectraRealtekStudio  

---

## 1. Security Philosophy

Because Spectra Realtek Studio interacts directly with Windows CoreAudio, Realtek Audio Processing Objects (`RltkAPO64.dll`), and optional kernel HDA registry keys, it is engineered with transparency, safety, and least privilege at its foundation:

1. **Zero Telemetry / 100% Offline:** Spectra Realtek Studio makes zero network connections. There are no background pings, update checks, telemetry beacons, or remote endpoints.
2. **Non-Admin Day-to-Day Operation:** All Equalizer, EAX Reverb, Karaoke, Spatial Audio, Speaker Config, Analog Mixer, and Microphone DSP features operate as a standard unprivileged user via Windows Audio RPC (`RtkCfg64.dll` / `IPolicyConfig`) and Windows CoreAudio COM APIs.
3. **Explicit UAC Elevation Only for Kernel Driver Tweaks:** Administrator elevation (`runas`) is ONLY requested when the user explicitly clicks **Apply Driver Tweaks** or **Retask Jack** in Tab 6, which modifies `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}\<index>` (`GlobalSettings`, `PowerSettings`, `PinConfigOverrideVerbs`).
4. **No DLL Injection or Kernel Driver Modification:** Spectra Realtek Studio never modifies `RTKVHD64.sys`, never injects DLLs into `audiodg.exe` or games, and never bypasses Windows Driver Signature Enforcement. It communicates exclusively through documented and driver-registered COM/RPC interfaces.
5. **Reproducible SHA-256 Release Artifacts:** Official GitHub Releases include `SHA256SUMS.txt` generated directly by `build-package.ps1` so users can verify both the Installer (`Setup.exe`) and Portable (`Portable.zip`) downloads.

---

## 2. Reporting a Security Vulnerability

We welcome responsible security disclosures from researchers and community members.

### Responsible Disclosure Guidelines
* **Do NOT report security vulnerabilities via public GitHub issues.**
* Please submit security advisories privately via **GitHub Security Advisories** on the repository:  
  `https://github.com/Creator-NL/SpectraRealtekStudio/security/advisories`
* Alternatively, contact the maintainer directly at **contact@biohazard-corp.nl** or via GitHub ([@Creator-NL](https://github.com/Creator-NL)).

### Our Commitment
* We will acknowledge receipt of your vulnerability report within **48 hours**.
* We will provide an initial assessment and remediation timeline within **5 business days**.
* Once remediated, a security release will be published with credit given to the reporter (if desired).
