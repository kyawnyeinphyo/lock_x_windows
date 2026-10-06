# Lock X for Windows

Encrypted vault for photos, videos, files and notes. WPF / .NET 10, ported from the macOS SwiftUI app.

## Build the installer

```powershell
.\build.ps1          # tests -> self-contained publish -> dist\LockX-Setup-1.0.0.exe
```

Needs the .NET 10 SDK and Inno Setup 6 (`winget install Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup`).

## Layout

- `src/LockX.Core`: crypto + vault logic (no UI). AES-256-GCM chunked blobs, PBKDF2-SHA256 (310k), HKDF.
- `src/LockX`: WPF app. Windows Hello unlock (TPM-backed key), shell thumbnails, streamed video playback.
- `tests/LockX.Core.Tests`: xUnit tests for the crypto and vault.
- `installer/LockX.iss`: Inno Setup script.

## Notes

- Vault data lives in `%LOCALAPPDATA%\LockX`. Uninstalling does not delete it.
- Set `LOCKX_ROOT` to use a different vault folder (handy for testing).
- Encrypted blob format matches the macOS app; `vault.json` and the index are not interchangeable, so a Mac vault cannot be opened directly.
