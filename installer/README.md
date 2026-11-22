# Installers

Two channels, both decided in the BRD (§4, §8):

| Channel | Package | Signing | Built by |
|---|---|---|---|
| Microsoft Store (individual users) | MSIX, self-contained | Microsoft signs on submission | `src/Occa.App` with `WindowsPackageType=MSIX` |
| HRIS › License Manager › Versions (managed PCs) | MSI (WiX 5) | Unsigned in beta; OV certificate later if needed | `installer/wix` from a `WindowsPackageType=None` publish |

## CI artifacts

`.github/workflows/build.yml` produces, for x64 and ARM64:

- `OCCA-MSIX-<platform>`: the `.msix` plus `OCCA-Dev.cer`, the public part of the **self-signed test certificate** the
  workflow creates for that run. Sideloading on a test PC needs that certificate trusted once (see
  `docs/testing-on-windows.md`). The Store build does not use it: Partner Center re-signs the package.
- `OCCA-MSI-<platform>`: `OCCA-<version>-<platform>.msi`. SmartScreen shows "Windows protected your PC" until the MSI is
  signed; choose *More info › Run anyway* (BR-8).

## Store identity

`Package.appxmanifest` carries a placeholder identity (`JendelaCakraDigital.OCCA`, `CN=PT. Jendela Cakra Digital`).
Before the first Store submission (M5), replace `Identity Name`, `Publisher` and `PublisherDisplayName` with the values
Partner Center shows under *Product identity*, and keep the CI test certificate's subject equal to the manifest
`Publisher` (the workflow reads it from the manifest).

## Local build on a Windows PC

```powershell
# MSIX (unsigned; sign with signtool or let CI do it)
msbuild src/Occa.App/Occa.App.csproj -restore -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=../../artifacts/msix/x64/

# MSI
dotnet publish src/Occa.App -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -o artifacts/publish/x64
dotnet build installer/wix -c Release -p:InstallerPlatform=x64 -p:PublishDir="$PWD/artifacts/publish/x64" -p:ProductVersion=0.1.0
```
