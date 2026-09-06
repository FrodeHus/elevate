# Elevate for Windows

Planned Windows 11 counterpart of the macOS app: a system-tray flyout built with WinUI 3 on .NET 10, installed from an MSI and published to winget as `Reothor.Elevate`.

Status: `Elevate.Core` (models, providers, persistence, discovery, coordinator) is ported and tested; the WinUI app, installer and CI are next and need a Windows machine — start from [CONTINUING.md](CONTINUING.md). See the [design spec](../docs/superpowers/specs/2026-09-05-elevate-windows-design.md) and the implementation plan in `docs/superpowers/plans/`.

UI design: [docs/design/elevate-windows.html](../docs/design/elevate-windows.html), index in [docs/design/README.md](../docs/design/README.md) (Fluent mockups of the flyout, windows, tray states and tokens; open the file in a browser).

## Build and test the Core library

```bash
cd windows
dotnet test Elevate.sln
```

Works on macOS, Linux and Windows with the .NET 10 SDK.
