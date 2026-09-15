# IMP Weld Photos

Copies endoscope weld photos from an SD card into `U:\100_Identi\140_Zvari`, sorted
by project, unit and isometrija, and stamps each photo with its own file name.
Details in [SPEC.md](SPEC.md); the look in [design/DESIGN.md](design/DESIGN.md).

## Download

**[IMPWeldPhotos.exe (latest)](https://github.com/petarcrncecIMP/IMPWeldPhotos/releases/latest/download/IMPWeldPhotos.exe)**

One file, nothing to install: .NET is built in. Put it anywhere and run it.

`IMPWeldPhotos.config.json` is optional. Without it the app uses the default `U:` paths
and skips the CommonData API fallback. To change either, download
[the config](https://github.com/petarcrncecIMP/IMPWeldPhotos/releases/latest/download/IMPWeldPhotos.config.json)
and put it beside the exe.

## Release a new version

```
git tag v1.0.1
git push origin v1.0.1
```

GitHub Actions builds the exe and publishes the release; the download link above then
points to it.

## Build locally

```
dotnet publish -c Release
```

Output: `bin\Release\net8.0-windows\win-x64\publish\IMPWeldPhotos.exe`.
