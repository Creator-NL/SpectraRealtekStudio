# Contributing to Spectra Realtek Studio

Thank you for your interest in contributing to **Spectra Realtek Studio**! Because Realtek High Definition Audio codecs (`ALC887`, `ALC888`, `ALC892`, `ALC897`, `ALC898`, `ALC1150`, `ALC1200`, `ALC1220`, `ALC4080`, etc.) vary across motherboard vendors and driver branches, community diagnostic reports and pull requests make this tool better for everyone.

---

## Ways to Contribute

1. **Share Codec & Motherboard Diagnostic Reports**
   - Use Tab 6 (**Export Community Diagnostic JSON**) or run:
     ```powershell
     SpectraRealtekStudio.Cli.exe diag my_codec_report.json
     ```
   - Open an issue attaching your sanitized JSON report so we can map additional OEM `PROPERTYKEY`s, `PinConfigOverrideVerbs`, and SounzReal / Nahimic / DTS APO extensions.
2. **Report Bugs or Missing Controls**
   - Use the [Bug Report](.github/ISSUE_TEMPLATE/bug_report.md) template and include your Codec ID (`DEV_XXXX`), Motherboard Subsystem ID (`SUBSYS_XXXXXXXX`), and Realtek Driver Version.
3. **Submit Pull Requests**
   - Fork the repository, create a feature branch, and ensure `dotnet build SpectraRealtekStudio.sln -c Release` and `dotnet test SpectraRealtekStudio.sln -c Release` succeed cleanly with zero warnings or errors.

---

## Core Engineering Principles

All contributions MUST adhere to the following rules:

- **Zero Telemetry & Strictly Local:** Never add analytics, crash uploaders, remote update checkers, or network requests.
- **Least Privilege by Default:** Daily audio DSP, Equalizer, EAX Reverb, Microphone DSP, and Mixer operations MUST continue working without requiring Administrator elevation (via `RtkCfg64.dll` / `IPolicyConfig` RPC and CoreAudio). Only explicit kernel driver registry changes (`HKLM\SYSTEM\CurrentControlSet\Control\Class\...`) may request UAC elevation.
- **Zero External Bloated Dependencies:** Keep `SpectraRealtekStudio.Core` and `SpectraRealtekStudio.App` lightweight native .NET 8 / WPF / Win32 COM interop without bundling third-party runtime drivers.
- **Safe Non-Destructive Tests:** Any integration test that modifies an active endpoint property MUST capture the original state and restore it in a `finally` block, and gracefully skip hardware assertions on headless CI runners without a physical Realtek codec.

---

## Building from Source

Prerequisites:
- Windows 10 or Windows 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer)
- Optional: [Inno Setup 6](https://jrsoftware.org/isinfo.php) to compile the Windows installer via `.\build-package.ps1`

```powershell
# Build entire solution
dotnet build SpectraRealtekStudio.sln -c Release

# Run automated unit tests
dotnet test SpectraRealtekStudio.sln -c Release

# Run live hardware self-verification CLI
dotnet run --project src/SpectraRealtekStudio.Cli/SpectraRealtekStudio.Cli.csproj -- test

# Build Installer + Portable ZIP + SHA-256 Checksums
.\build-package.ps1
```

---

## Code of Conduct

All contributors are expected to follow our [Code of Conduct](CODE_OF_CONDUCT.md). For questions, reach out via GitHub Issues or **contact@biohazard-corp.nl**.
