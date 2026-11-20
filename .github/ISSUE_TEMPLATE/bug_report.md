---
name: Bug Report / Codec Compatibility Issue
about: Report a bug, missing DSP control, or Realtek codec compatibility issue
title: "[Bug] "
labels: bug
assignees: ""
---

## Hardware & Driver Environment
- **Windows Version:** (e.g., Windows 10 22H2 / Windows 11 24H2)
- **Motherboard / Laptop Model:** (e.g., MSI B450 / ASUS ROG / Gigabyte)
- **Realtek Codec Chip:** (e.g., ALC892, ALC897, ALC1200, ALC1220, ALC4080)
- **Realtek Driver Version:** (e.g., 6.0.1.8186 / R283 / 6.0.9xxx UAD)
- **Spectra Realtek Studio Version:** (e.g., v1.0.0 Installer / Portable)

## Describe the Bug
A clear and concise description of what happened and what you expected to happen.

## Steps to Reproduce
1. Open Spectra Realtek Studio
2. Navigate to Tab '...'
3. Click / adjust '...'
4. Observe '...'

## Community Diagnostic Report (Highly Recommended)
Please attach the JSON report generated from **Tab 6 → Export Community Diagnostic JSON** or by running:
```powershell
SpectraRealtekStudio.Cli.exe diag codec_report.json
```
*(Diagnostic reports contain only audio endpoint metadata, driver registry flags, and FxProperties — zero personal files or credentials.)*
