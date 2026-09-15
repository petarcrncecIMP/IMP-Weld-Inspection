# IMP Weld Inspection

Copies endoscope weld photos from an SD card into `U:\100_Identi\140_Zvari`, sorted
by project, unit and isometrija, and stamps each photo with its own file name. The
Pregled page browses the photos already on the share.
Details in [SPEC.md](SPEC.md); the look in [design/DESIGN.md](design/DESIGN.md).

## Download

**[IMP Weld Inspection (latest)](https://github.com/petarcrncecIMP/IMP-Weld-Inspection/releases/latest/download/IMP.Weld.Inspection.exe)**

One file, nothing to install: .NET is built in. Put it anywhere and run it. GitHub names
the download `IMP.Weld.Inspection.exe`; rename it to `IMP Weld Inspection.exe` if you like.

When a newer version is released, the app shows **Posodobi** in its header; one click
downloads it, swaps the exe and restarts.

`IMP Weld Inspection.config.json` is optional. Without it the app uses the default `U:`
paths and skips the CommonData API fallback. To change either, download
[the config](https://github.com/petarcrncecIMP/IMP-Weld-Inspection/releases/latest/download/IMP.Weld.Inspection.config.json)
and put it beside the exe with that name.

## Release a new version

```
git tag v1.0.3
git push origin v1.0.3
```

GitHub Actions builds the exe (with the tag as its version) and publishes the release; the
download link above and the in-app updater then pick it up.

## Build locally

```
dotnet publish -c Release
```

Output: `bin\Release\net8.0-windows\win-x64\publish\IMP Weld Inspection.exe`. Local builds
are version 0.0.0 and never offer updates.
