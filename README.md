# OCCA for Windows

Windows edition of OCCA (Orienteering Cakra CAD Application) by PT. Jendela Cakra Digital: orienteering map drawing and course planning with the IOF standards (ISOM 2017-2, ISSprOM 2019-2, ISInOM 6.7.3), compatible with the Mac/iPad app's `.occa` files and licensed through the HRIS License Manager.

Status: **M0 Setup** (PRD §11). The solution skeleton builds an empty WinUI 3 app with a project list, reads Mac/iPad `.occa` files (LZFSE) and fetches Esri World Imagery tiles.

- [Business Requirements (BRD)](docs/BRD.md)
- [Product Requirements (PRD)](docs/PRD.md)
- [Esri offline imagery terms (spike)](docs/esri-offline-imagery.md)
- [Testing a build on Windows](docs/testing-on-windows.md)
- [Installing from the MSI](docs/install-msi.md)
- [Installers](installer/README.md)

## Stack

.NET 8 (C#) · WinUI 3 (Windows App SDK 1.8) · Win2D · WiX 5 · Windows 11 24H2+ (x64, ARM64)

## Layout

| Path | Contents |
|---|---|
| `src/Occa.Core` | Core library (`net8.0`): models, file formats, geodesy, licensing rules |
| `src/Occa.Render` | Win2D renderer, PDF and raster export |
| `src/Occa.App` | WinUI 3 application (single-project MSIX) |
| `tests/Occa.Core.Tests` | xUnit tests and shared fixtures |
| `installer/wix` | MSI for HRIS distribution |
| `.github/workflows/build.yml` | CI: tests, MSIX and MSI for x64 and ARM64 |

## Build

The app builds on Windows only. CI builds on every push; artifacts are on the workflow run page. The core library and its
tests run anywhere:

```sh
dotnet test tests/Occa.Core.Tests
```

The Mac/iPad app lives in the separate `cakra-pencil` repository.
