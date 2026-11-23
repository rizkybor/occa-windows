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

## C3. What to check in milestone M3 (output and import)

1. Open a project → **Export** (Ctrl+E). PDF: open in Acrobat/Edge; Acrobat's *Geospatial location* tool shows
   lat/lon; colours are CMYK (check with Acrobat *Output preview*). Compare with the Mac PDF of the same `.occa`.
2. PNG/TIFF/JPEG at 300 dpi: an A4 export is 2480×3508 px (portrait) and comes with a world file (.pgw/.tfw/.jgw)
   and .prj; open in QGIS to confirm the position.
3. OMAP: open in OpenOrienteering Mapper; DXF: open in QGIS or OCAD; objects keep their symbol codes.
4. **Open file** with an `.ocd` (OCAD 10–2018) or `.omap`/`.xmap`: a new project appears, centred on the content.
   OCAD files ask for the UTM zone first.

## C4. What to check in milestone M4 (course planner and contours)

1. Open a project with a locked map area and click **Courses** (Ctrl+K). The side panel shows the event, courses,
   controls and the IOF 2024 description editor. **+** adds a course; the placement toggles **Start / Control /
   Finish** make the next map click place that control (controls get the next free code, 66/68/… are skipped).
   Dragging a control moves it; clicking an existing control adds it to the selected course. The purple overprint
   (circles, start triangle, legs with the 0.3 mm gap, numbers placed away from the legs) updates immediately and the
   package is saved after about half a second (`courses.json` in the project folder, same JSON as Mac/iPad).
2. Pick symbols in columns C–H: the text line under the editor reads like "Northern boulder, 1.5 m, east side".
   **Check** lists the same warnings as the Mac (codes used twice, controls closer than 30 m, missing finish…).
3. **Export** → PDF with "Include the selected course": the map page carries the control descriptions in the chosen
   corner (symbols or text, 5–7 mm cells) and, when "separate description page" is on, a second A4 page. Open the PDF
   in Acrobat: the purple is DeviceCMYK 35/85/0/0 and the course sits in the colour order (black lines over the lower
   purple). PNG/TIFF/JPEG exports draw the same overprint. **IOF XML…** writes a `CourseData` file MeOS/OE can read.
4. **Import course…** accepts a Mac `.occa` (controls with descriptions, positions converted between UTM zones), an
   OCAD course-setting `.ocd` and an OOM `.omap/.xmap` (701/702/706 symbols become controls).
5. Click **Contours** (Ctrl+H). **Download terrain** fetches AWS Terrain Tiles for the map area (needs internet) and
   stores `dem.json` + `dem.bin` in the package; **Open DEM…** reads a GeoTIFF/ASCII grid, or a GeoJSON with contour
   lines. Brown contours (ISOM 101/102/103 widths, index every 5th) appear under the objects; interval, base
   elevation, smoothing and form lines regenerate them; the interval warning shows when it is too fine for the DEM.
6. **Export contours…** writes Shapefile (UTM) / KML / KMZ (WGS84; open in Google Earth). **To vector objects** turns
   the lines into editable ISOM line objects and hides the live contours. PDF and raster exports include the contours
   when "Include in PDF and raster exports" is on.
7. Open the same project on the Mac afterwards: courses, descriptions, print layout and the DEM must read identically.

## C5. What to check in milestone M5 (licensing and updates)

Needs the HRIS back office with the Windows platform (see `licensing-windows.md`); until then the server answers
`wrong_platform`/`invalid_request` and only the offline parts can be checked.

1. The header chip reads **Starter**; the bottom-left corner shows "OCCA Starter · Activate a license key…". Clicking the
   chip opens the License screen: plan card, key field, plans, features, device id (a UUID derived from the machine GUID;
   the same after reinstalling).
2. Paste a Mac key (`OC32-…`): the hint and the message say it is a Mac key. Paste a Windows key (`OC72-…`): **Activate**
   → the chip turns Professional (or the key's plan), the watermark disappears, "n of m devices in use" shows. The token
   is stored encrypted in `%LOCALAPPDATA%\OCCA\license\*.bin` (DPAPI, current user).
3. Restart OCCA offline: the plan stays (offline verification). **Check now** online refreshes; switching the key off in
   HRIS and pressing Check now falls back to Starter with the server's message.
4. **Release this PC** (confirm) → Starter; the seat is free in HRIS. Reinstall or delete the license folder, start OCCA
   online: `restore` brings the license back without a key (only while the device is still activated).
5. Starter limits: a 4th project import, a 5th course, Contours, Import course, IOF XML, PNG/TIFF/JPEG and OMAP/DXF
   exports open the License screen; PDF export still works and carries the "Made with OCCA" credit.
6. Set the PC clock back a day: the plan drops to Starter with the clock message until the time is right and the app has
   been online. A Trial key past its end shows the "trial ended" message.
7. Updates: with a newer published Windows release in HRIS Versions, the project list shows "Update available" with the
   release notes; **Download** opens the HRIS installer link (MSI install) or the Store page (MSIX install).

## C6. What to check for the beta (M6: drawing, new project, file association)

1. **New project** (Ctrl+N): name, scale (Starter: 1:4000 / 1:10000), orientation, start location. The map opens in
   Select Area mode: a dimmed outside and the A4 frame at the centre; dragging the imagery moves the frame, "lat, lon · Go"
   jumps, the UTM zone in the status line follows the location. **Lock area** fixes it, opens the Symbols panel and
   selects the Pen tool.
2. **Symbols** (Ctrl+J): search "506" or "path", pick a line symbol → Pen tool; click points on the map, double-click or
   Enter finishes; Esc cancels. Pick an area symbol (e.g. 406 Vegetation) → Pen draws a closed area; Rectangle (M) and
   Ellipse (L) drag areas; Freehand (F) drags a line (or area when it closes); pick a point symbol (204 Boulder) → Point
   (T) places it. Sketch pencil (N) draws black strokes; Eraser (E) removes a stroke.
3. **Move Tool** (V): click selects (Shift adds), drag moves, drag on empty space is a selection box; Delete removes;
   Ctrl+D duplicates 2 mm offset; right-click: Duplicate, Change symbol to the active one, Reverse, Close line, Delete.
   **Node Tool** (A): drag points; drag bezier handles of the active point; click a segment to insert a point; Delete
   removes the active point.
4. Ctrl+Z / Ctrl+Y undo and redo every step; Ctrl+S saves now (the package saves itself 0.6 s after each edit anyway). Back to
   Projects: the card preview shows the drawing.
5. Open the same project on the Mac (`%LOCALAPPDATA%\OCCA\Projects\<id>.cakra` zipped as `.occa`, or Export → nothing
   needed: copy the folder) — the objects, symbols and widths must match. Round trip: a Mac project edited on Windows and
   reopened on the Mac keeps every field.
6. Double-click a `.occa` in Explorer (MSIX and MSI installs): OCCA starts and imports/opens it. The window reopens at its
   last position and size.

## C7. Styling and layout checks (beta audit, 24 Nov 2026)

Check each screen in Light, Dark and System appearance (the sun / moon button bottom-right) and at two window
widths: maximised and the narrowest the window allows (about 1000 px).

1. **Front page**: caption buttons (minimise / maximise / close) have no light box in Dark mode; the plan chip next to
   the name changes colour with the theme (sand Starter, butter Trial, sage Professional, rose Lifetime, sky
   Organization) and follows an Appearance change at once. Project cards: the preview is clipped to the rounded
   corners, the preview updates after editing (no stale thumbnail), long names and summaries end with "…". The
   M0 "test tile" globe button is gone.
2. **Window**: on a 1366×768 laptop the window opens inside the work area (not under the taskbar) and can be made small
   enough to fit; the saved position is ignored when that monitor is gone. Closing the window while editing keeps the
   last edit (reopen: the object is there).
3. **License dialog**: fits a 768 px tall screen (the content scrolls, the Close button stays visible); the plan tiles
   and badge match the dialog theme when the app is Dark on a Light system (and the reverse); the current plan tile has a
   sage outline; Starter texts mention contours, map import and raster/vector export (what Windows actually gates).
4. **Select Area**: the top bar at the narrowest width shows icon-only buttons with tooltips and the project name is
   still readable; the "Select the map area" bar does not overlap the info bar (messages appear below it); the status
   line and the Esri attribution never overlap (the status text trims first).
5. **Drawing**: after clicking a tool button, the tool letters (H V A P F M L T N E), Esc, Enter and Delete work at once
   without clicking the map first. While typing in a panel field (course name, lat/lon), Ctrl+Z / Ctrl+A / Ctrl+D edit
   the text, not the map, and letters do not switch tools.
6. **Panels**: Symbols, Courses and Contours open one at a time at 360 px; with the Courses panel open the tool rail
   dims and the status line says "course planning"; closing it re-enables the rail. Contours: "Download terrain" stays
   disabled for a project without a map area, also after a DEM load finishes.
7. **Course planner**: placing Start / Control / Finish, dragging a control, the blue selection ring, descriptions and
   print layout fields; the overprint is purple at 90 % on screen and solid in the PDF.
8. **Imagery** (fixes for the 23 Nov screenshots): a new project shows satellite imagery under the A4 frame within a few
   seconds; zooming out or in never leaves blank paper (the previous level's tiles are scaled until the new ones arrive;
   beyond level 19 the level-19 tiles are enlarged); a tile that failed (offline moment) appears once the network is back.
   No "Open file / New project / Search projects" ghosts show through the side panels. Right-click in Select Area mode
   shows no menu; the side panels close when a new project opens in Select Area mode.
9. **Symbols panel**: every row shows the symbol as it prints on light paper (line with its dashes / tags, area with
   its pattern, point symbol shape), also in Dark mode; ISOM (1:10000) and ISSprOM (1:4000) sets differ.

## D. Logs and failures

- The CI run's *Core tests* job uploads `test-results/core.trx` (open in Visual Studio or any TRX viewer).
- If the app closes immediately on the PC: *Event Viewer › Windows Logs › Application* shows the .NET exception, or
  run `OCCA.exe` from a terminal in the MSI install folder to see the output.
