# Testing a CI build on a Windows 11 PC

Every push to `main` runs `.github/workflows/build.yml` (also on demand: *Actions › Build › Run workflow*). When it
finishes, the run page lists four artifacts: `OCCA-MSIX-x64`, `OCCA-MSIX-ARM64`, `OCCA-MSI-x64`, `OCCA-MSI-ARM64`.
Pick x64 for an Intel/AMD PC, ARM64 for a Snapdragon PC. Windows 11 24H2 or newer is required.

## A. MSIX (the Store channel, sideloaded)

1. Download `OCCA-MSIX-x64.zip`, unzip. It contains `OCCA_<version>_x64.msix` and `OCCA-Dev.cer`.
2. Trust the test certificate **once per PC** (the CI build signs with a self-signed certificate; the Store build will
   be signed by Microsoft instead). In an elevated PowerShell:
   ```powershell
   Import-Certificate -FilePath .\OCCA-Dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```
   A new certificate is created on every CI run, so repeat this for each build you install.
3. Double-click the `.msix` and choose *Install* (or `Add-AppxPackage .\OCCA_<version>_x64.msix`).
4. Start **OCCA** from the Start menu.

Uninstall: *Settings › Apps › OCCA › Uninstall*.

## B. MSI (the HRIS channel for managed PCs)

Full guide, including silent install for IT admins: [install-msi.md](install-msi.md).

1. Download `OCCA-MSI-x64.zip`, unzip `OCCA-<version>-x64.msi`.
2. Double-click. SmartScreen shows "Windows protected your PC" because the MSI is unsigned in beta: choose
   *More info › Run anyway*.
3. Follow the installer (default folder `C:\Program Files\OCCA`). A Start menu shortcut is created and `.occa`,
   `.occafield`, `.occasym` are associated with OCCA.

Uninstall: *Settings › Apps › OCCA › Uninstall*. The MSI and the MSIX can be installed side by side; they are separate
installations.

## C. What to check in this milestone (M0)

1. The main window opens with the title bar, the *Projects* heading and the empty state
   "No projects yet", showing the projects folder `%LOCALAPPDATA%\OCCA\Projects`.
2. **Open .occa…** → pick a file saved on Mac/iPad (or `tests/Occa.Core.Tests/Fixtures/sample-park-sprint.occa`).
   A dialog lists the name, scale, CRS, number of objects (features + sketch strokes), controls and courses, and the
   files in the archive. `sample-park-sprint.occa` should show 8 objects, 5 controls, 2 courses.
3. **Test basemap tile** → with an Esri API key configured, a 256×256 satellite tile of Monas, Jakarta, with the
   attribution line. Without a key the yellow bar says where to put it:
   - for a CI build, add the repository secret `OCCA_WINDOWS` (the Esri API key) (Settings › Secrets and variables › Actions), or
   - on the PC, create `appsettings.local.json` next to `OCCA.exe` (see `appsettings.local.json.example`), or set the
     user environment variable `OCCA_ESRI_API_KEY` and restart OCCA. For the MSIX install the executable folder is
     read-only, so use the environment variable.
4. Light/dark follows the system setting; the window can be resized and moved between monitors at different DPI.

## C2. What to check in milestone M1 (open and display Mac projects)

1. **Open file** → a `.occa` from Mac/iPad. It is imported into `%LOCALAPPDATA%\OCCA\Projects` and opened on the map.
   `tests/Occa.Core.Tests/Fixtures/symbol-display.occa` shows every kind of ISOM symbol: solid and pattern areas,
   an area with a hole, contour (curved), dashed footpath, double-line road, cliff tags, wall dots, fence tags,
   power-line bars, a boulder, a bush (green dot with white centre) and a rotated, enlarged slope line.
2. The satellite basemap (Esri World Imagery) appears under the objects inside the A4 frame; the opacity slider
   fades it to paper; the toggle hides it. Tiles are cached in `%LOCALAPPDATA%\OCCA\TileCache`.
3. Mouse: drag pans, wheel pans, Ctrl+wheel zooms at the pointer; touch: two fingers pan, pinch zooms; Ctrl+0 or the
   fit button zooms to the map area.
4. Back to **Projects**: the card shows a preview, scale chip, name, technical summary and dates.
5. Compare with the Mac app: open the same `.occa` on both; the objects, colours, dashes and patterns must match.

## D. Logs and failures

- The CI run's *Core tests* job uploads `test-results/core.trx` (open in Visual Studio or any TRX viewer).
- If the app closes immediately on the PC: *Event Viewer › Windows Logs › Application* shows the .NET exception, or
  run `OCCA.exe` from a terminal in the MSI install folder to see the output.
