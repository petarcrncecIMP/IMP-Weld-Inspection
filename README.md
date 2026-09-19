# IMP Weld Inspection

Copies endoscope weld photos from an SD card into `U:\100_Identi\140_Zvari`, sorted
by project, unit and isometrija, and names each photo after its weld. Photos are copied
unstamped, as the endoscope took them. Project, unit and isometrija come from the
CommonData database (sign-in shared with the AutoCAD tools), or from `titles.json` when
offline. The Pregled page browses the photos already on the share and makes a Word report for a skid: stamped photos in the annex, packed in `{project}\Poročila\{number} - {skid}\`. The report template is an ordinary Word file on the share — see SPEC.md §6.
Details in [SPEC.md](SPEC.md); the look in [design/DESIGN.md](design/DESIGN.md).

## Download

**[IMP-Weld-Inspection.exe (latest)](https://github.com/petarcrncecIMP/IMP-Weld-Inspection/releases/latest/download/IMP-Weld-Inspection.exe)**

One file, nothing to install: .NET is built in. Put it anywhere and run it. The Kosovnice
apps menu links to the same file.

When a newer version is released, the app shows **Posodobi** in its header; one click
downloads it, swaps the exe and restarts.

`IMP-Weld-Inspection.config.json` is optional. Without it the app uses the default `U:`
paths and production CommonData. To change either, download
[the config](https://github.com/petarcrncecIMP/IMP-Weld-Inspection/releases/latest/download/IMP-Weld-Inspection.config.json)
and put it beside the exe.

## Release a new version

```
git tag v1.0.3
git push origin v1.0.3
```

GitHub Actions builds the exe (with the tag as its version) and publishes the release; the
download link above and the in-app updater then pick it up. Keep the asset name
`IMP-Weld-Inspection.exe`: the Kosovnice apps menu links to it.

## Build locally

```
dotnet publish -c Release
```

Output: `bin\Release\net8.0-windows\win-x64\publish\IMP-Weld-Inspection.exe`. Local builds
are version 0.0.0 and never offer updates.
