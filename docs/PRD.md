# OCCA for Windows: Product Requirements Document (PRD)

| | |
|---|---|
| Product | OCCA for Windows 1.0 |
| Status | Draft for review |
| Date | 22 November 2026 |
| Related | [BRD](BRD.md); OCCA Mac/iPad 1.1.0 (`cakra-pencil` repo: README "Status" table, CHANGELOG); HRIS `docs/license-manager-api.md` |

## 1. Summary
OCCA for Windows is a native Windows desktop app with the same purpose, file formats, IOF symbols, print output and licensing as OCCA for Mac/iPad: drawing orienteering maps and planning courses. The UI is rebuilt for Windows, with mouse, keyboard, touch and pen. The core behaviour is ported from the Mac/iPad core (`CakraCore`) and checked against the same test files.

## 2. Users
| Persona | Needs |
|---|---|
| **Mapper** (club or freelance) | Draw on satellite imagery or GeoTIFF; IOF symbols; OCAD/OOM import and export; print-ready PDF. |
| **Course setter** | Courses on an existing map, control descriptions (IOF 2024), IOF XML for timing software, race PDFs. |
| **Mixed-device team** | Open a colleague's `.occa` from Mac/iPad; process an iPad survey day (`.occafield`) on a PC. |
| **Back office admin** | Issue Windows keys, publish Windows installers, see activations. |

## 3. Platforms and devices
- **Minimum: Windows 11, version 24H2 (build 26100)**, x64 and ARM64.
  - 24H2, 25H2 and 26H2 share one platform; 25H2 and 26H2 are enablement packages on 24H2, so the same APIs are available.
  - 24H2 Home/Pro updates ended on 13 October 2026, but Enterprise/Education 24H2 is supported until 12 October 2027, which covers clubs and schools on managed PCs.
- **Target and test: Windows 11, version 26H2 (build 26300)**, the current release (general availability 29 September 2026), plus 25H2.
- **Not supported:** Windows 10 (support ended October 2025) and Windows 11 23H2 or older.
- **Recommended hardware:**
  - CPU: x64 or ARM64 meeting the Windows 11 requirements.
  - Memory: 8 GB RAM (4 GB minimum).
  - GPU: DirectX 12, which every Windows 11 PC has, for Win2D/Direct2D drawing.
  - Display: 1920×1080 or higher; the main window needs at least 1100×760.
  - Storage: 2 GB free, plus projects and imagery.
- **Input:**
  - mouse and keyboard, as primary;
  - precision touchpad with two-finger pan and pinch;
  - touch screen;
  - pen (Windows Ink: Surface Pen, Wacom), with pressure where useful and the eraser button.
- High-DPI and multiple monitors; light, dark and system appearance.
- Works offline after activation (licensing §7). Basemap download and contours need the internet once per project.

## 4. Feature scope: parity with OCCA Mac 1.1
Legend: **M** = in Windows 1.0; **L** = later; **—** = not planned for Windows.

| Area | Mac/iPad 1.1 | Windows 1.0 |
|---|---|---|
| **Projects** | New project (name, scale 1:1000–1:15000, indoor 1:250–1:2000, dpi, Indoor map option), auto UTM, A4 portrait/landscape frame, margins, lock area, reselect area, project list with previews, search, sort, rename, duplicate, delete | M |
| **Files** | `.occa` save/open, OCAD/OOM map → new project, `.occasym`, `.occafield` | M (same formats, both directions) |
| **Basemap** | Apple Maps satellite/hybrid/standard; HD image of the locked area; live mode; GeoTIFF basemap; opacity | M. A replacement imagery provider (§8.3) for satellite/standard; HD image and GeoTIFF the same |
| **North grid** | Grid/magnetic north lines, spacing, colour, export | M |
| **Symbols** | OCCA ISOM 2017-2, ISSprOM 2019-2, ISInOM 6.7.3 sets; import OCAD/OOM sets; colour hierarchy (IOF Rev 4); symbol guide (omapwiki) | M |
| **Drawing** | Pen (bezier), freehand, rectangle, ellipse, point symbols, snap, follow object, auto-merge, fill area | M |
| **Editing** | Move Tool (rotate handle, Shift precise), Node Tool (sharp/smooth/smart), Corner Tool, scale, parallel, reverse, cut line, split area / holes, join/merge, multi-select, duplicate, change symbol, undo/redo | M |
| **Pencil sketch layer** | Free sketch with constant width, eraser | M (pen and mouse) |
| **Layers** | Objects list, visibility, lock, reorder | M |
| **Measure** | Distance and azimuth | M |
| **Contours** | AWS Terrain Tiles, DEM GeoTIFF/ASCII, GeoJSON lines, interval/index/form lines, convert to objects, SHP/KML/KMZ export | M |
| **Course planner** | Courses, controls, IOF Control Descriptions 2024, objects (text/shapes/images), leg gaps, preview, IOF XML 3.0, OCAD/OOM/`.occa` course import | M |
| **Export** | Georeferenced PDF (CMYK, IOF colour order), PNG/TIFF/JPEG + world file + .prj, OMAP, DXF, IOF XML; Save as dialog with file name and location | M |
| **Field: process** | Tracks/notes list, show/hide per item, sketch notes, make map object, track → line, coverage, field report PDF, GPX import/export, `.occafield` import/export | M |
| **Field: capture** | Live GPS track recording, field mode, averaging | **—** (capture stays on iPad); L only if devices with GPS/GNSS ask for it |
| **Licensing** | Starter/Trial/Professional/Lifetime/Organization; activate, refresh, release, restore, expiry, watermark on Starter/Trial | M |
| **Updates** | Version check against HRIS, critical updates | M |
| **Guide, About, Appearance** | First-run guide, About with contacts, Light → Dark → System switch | M |

## 5. Functional requirements (Windows-specific details)
### 5.1 App shell
- FR-W1: A single main window with a title bar that holds the project name and actions (Course, panels, Export), the same as the Mac top bar.
- FR-W2: A toolbar band at the top (tools, map controls, pens, contextual bar) and a right panel (Symbols, Contours, Map, Layers, Field), matching the Mac/iPad layout.
- FR-W3: Keyboard shortcuts:
  - Mac ⌘ becomes **Ctrl**: Ctrl+S, Ctrl+O, Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z, Ctrl+A, Ctrl+J, Ctrl+0.
  - Tool keys are unchanged: V, A, C, R, H, P, F, M, L, T, K, E, N.
  - Esc cancels.
  - Tooltips show the shortcut.
- FR-W4: Window size and position are remembered; the minimum window is 1100×760, as on Mac.

### 5.2 Input
- FR-W5: Mouse:
  - left button draws or selects;
  - the middle button or Space+drag pans;
  - the wheel pans, with Ctrl+wheel to zoom;
  - right-click opens a context menu (Copy, Delete, Change symbol…).
- FR-W6: Touchpad and touch: two fingers pan, pinch zooms, two-finger rotate turns the map. The first finger never selects or adds a point when it turns into a two-finger gesture (the same rule as the iPad).
- FR-W7: Pen: the pen draws, touch navigates (an option, as on iPad); the pen eraser button erases sketch strokes; pressure is optional for the sketch layer only, since vector line widths come from symbols.

### 5.3 Files
- FR-W8: Associate `.occa`, `.occafield` and `.occasym` with OCCA (open by double-click; Explorer icons).
- FR-W9: Projects live in `%LOCALAPPDATA%\OCCA\Projects`, in the same package layout as Mac (`project.json`, `features.json`, `courses.json`, `field.json`, `imagery/`, …).
- FR-W10: Exports go by default to `Documents\OCCA\Exports`, with a File name field and a location picker, as on Mac.

### 5.4 Licensing (HRIS)
- FR-W11: The key screen accepts Windows keys `OC7…`. Mac/iPad keys get the "wrong platform" message.
- FR-W12: The token is verified offline with the product's Ed25519 public key. It is bound to the device ID, and the plan applies only while the end date, offline limit, clock check and Lifetime major version pass (the same rules as `LicenseValidity` on Mac).
- FR-W13: Device ID: a SHA-256 hash of the machine GUID (`HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`) with an app-specific salt, so it stays the same after reinstall. The raw GUID never leaves the PC.
- FR-W14: At launch without a valid token, the app calls `restore`. Release this device, the refresh schedule and the expiry re-check while running all work as on Mac.
- FR-W15: The token and state are stored with Windows DPAPI (per user) in `%LOCALAPPDATA%\OCCA`.

### 5.5 Field data with iPad
Windows works like the Mac. Field capture (GPS tracks, field mode, averaging) runs on iPad; Windows processes the data at the desk.
- FR-W16: Import and export `.occafield` (Field › Data) and GPX.
  - New tracks and notes are added and nothing is duplicated.
  - Desk work is kept: renamed tracks, done notes, map object links and per-item visibility.
- FR-W17: The full processing set:
  - track/note list with per-item show/hide;
  - note details with photos and photo markup;
  - sketch notes (draw on the map with a note);
  - make map object; track → line; survey coverage; field report PDF.
- FR-W18: Moving files between iPad and Windows (no AirDrop on Windows):
  - USB through File Explorer / the Apple Devices app;
  - OneDrive, Google Drive or iCloud for Windows;
  - email or WhatsApp (`.occafield` files are small);
  - a USB drive through the iPad Files app.

  The in-app Guide explains these steps on Windows.
- Later (not in 1.0):
  - **Automatic field sync through HRIS:** iPad uploads a survey day; Mac/Windows downloads it with the same license, with no manual file transfer.
  - **GPS recording on Windows tablets that have GNSS** (e.g. Surface Pro LTE, rugged tablets), through the Windows Location API, if customers ask.

## 6. Non-functional requirements
| ID | Requirement |
|---|---|
| NFR-1 | **Performance:** 60 fps pan/zoom on a map with 5,000 objects on a mid-range laptop (integrated GPU); live drag of one object at 60 fps. |
| NFR-2 | **Startup:** the project list in under 2 s on SSD; open a typical project in under 1 s. |
| NFR-3 | **File compatibility:** every file written by Mac/iPad 1.1 opens; files written by Windows open on Mac/iPad 1.1 or newer. Unknown JSON fields are kept, so they are not lost on a round trip. |
| NFR-4 | **Print fidelity:** the PDF uses DeviceCMYK with the IOF Rev 4 values and the same colour order as Mac. Course overprint uses lower purple in the colour order, with no multiply. |
| NFR-5 | **Reliability:** atomic saves (write a temp file, then move it) and autosave; no data loss when the app is killed. |
| NFR-6 | **Security:** the license secret never ships; tokens are signed; Release builds have no debug overrides; the installer and binaries are code-signed. |
| NFR-7 | **Accessibility:** keyboard navigation of panels; Narrator labels on all icon buttons; respects system text scaling. |
| NFR-8 | **Size:** installer under 150 MB. |

## 7. Licensing and update flows
Same API as Mac/iPad: `POST /api/licenses/occa/{activate|refresh|deactivate|restore|rebind}`.
- Platform value `windows`; key platform digit `7` (decided).
- **Updates:**
  - **Microsoft Store installs** update through the Store. The app shows "What's new" from HRIS release notes and points critical updates to the Store page.
  - **MSI installs** read `/api/licenses/occa/releases/latest?platform=windows`, show the release notes, offer the MSI from HRIS and mark critical updates.

## 8. Technical approach
### 8.1 Decision
**Decided (22 November 2026): Windows only, built with .NET 8 (C#) + WinUI 3 (Windows App SDK) + Win2D/Direct2D.**
- OCCA is a pen-and-precision drawing app with exact print output, and Linux, web and Android are not targets.
- So the most native Windows input matters most: Windows Ink pen, precision touchpad, touch.
- So do GPU drawing (Direct2D), mature PDF/TIFF libraries and compiled code (stronger license protection). Sharing code across platforms matters less.
- Core logic is ported from `CakraCore` to C# and checked with shared golden files (§10).

### 8.2 Options considered
| Option | Platforms | Pen & touch | Map rendering | License protection | Why not chosen |
|---|---|---|---|---|---|
| **.NET 8 + WinUI 3 + Win2D (chosen)** | Windows | Best (Windows Ink, precision touchpad) | Direct2D on the GPU | Good (compiled .NET; NativeAOT where possible) | — |
| Avalonia UI + SkiaSharp (C#) | Windows, Linux, macOS | Good | Skia on the GPU | Good | Cross-platform isn't needed; WinUI is more native on Windows. |
| Qt 6 (C++/QML) | Windows, Linux, macOS, Android | Very good (QTabletEvent) | QPainter | Very good | C++ is slower to build with; cross-platform isn't needed; Qt licensing (LGPL/commercial). |
| Electron (TypeScript) | Windows, Linux, macOS | Good (Pointer Events) | Canvas/WebGL; needs extra work for large maps | Weak: JavaScript in `app.asar` is easy to change; needs native modules, ASAR integrity and fuses | Weakest license protection, largest installer (90–150 MB), higher memory use. |
| Tauri (TypeScript + Rust) | Windows, Linux, macOS | Good | Canvas/WebGL | Good (Rust) | A web UI plus Rust for a Windows-only app; younger ecosystem. |
| Compose Multiplatform (Kotlin) | Windows, Linux, macOS, Android | Weak: no Windows Ink pen pressure | Skia | Medium | Missing pen input for a drawing app; bundles a JVM. |
| Swift on Windows + WinUI (swift-winrt) | Windows | Good | Direct2D | Good | Most reuse of `CakraCore`, but a young toolchain on Windows: high risk for a small team. |
| Flutter desktop | Windows, Linux, macOS | Medium | Skia/Impeller | Medium | Less complete desktop pen and touchpad support; a new language. |

### 8.3 Architecture
```
OCCA.Windows.sln
├─ Occa.Core           C# class library: models, geometry (bezier, holes, cut/merge, corners), symbols and colour
│                      tables, UTM/geodesy, contours, course logic, IOF descriptions, readers/writers
│                      (.occa, OCAD, OMAP, GeoTIFF, GPX, IOF XML), license token and validity
├─ Occa.Render         Win2D/Direct2D map renderer (patterns, line decorations, colour order), PDF writer (CMYK),
│                      raster exporter
├─ Occa.App            WinUI 3 app: shell, panels, tools, input handling, dialogs, licensing UI, updater
├─ Occa.Core.Tests     xUnit: ported CakraCore tests plus shared golden files
└─ installer/          MSIX for the Microsoft Store, MSI (WiX) for HRIS
```

### 8.4 Key components
- **`.occa` packages:** the Mac app compresses with Apple LZFSE. Windows uses the open-source LZFSE reference library (BSD) through P/Invoke, or a C# port, to read and write the same files. A spike task confirms this first.
- **Basemap imagery (decided): Esri World Imagery** through ArcGIS Location Platform (API key; 2 million free basemap tiles per month).
  - Live tiles (Web Mercator) are used while choosing the area.
  - As on Mac, the locked area is then downloaded once as an HD georeferenced image and stored in the project, if Esri's terms allow offline storage; this is checked in M0. Otherwise a temporary cache is used.
  - Attribution ("Esri, Maxar, Earthstar Geographics, and the GIS User Community", or the current credit text) shows on the map and in exports that include the basemap.
  - The GeoTIFF basemap is unchanged.
  - The API key is restricted to the app's referrer/usage and is never shown in the UI.
- **Map view:** a Win2D canvas with tiled rendering and a cache per zoom level. Objects are drawn in the symbol set's colour order, the same as Mac.
- **PDF:** a custom PDF writer or PdfSharp with DeviceCMYK paths, patterns and Geospatial PDF georeferencing (the same structure as the Mac `GeoPDFExporter`).
- **Licensing:** the same token format; Ed25519 verify; MachineGuid-derived device ID; DPAPI storage.
- **Distribution (decided):**
  - **Microsoft Store** MSIX (self-contained runtime), which Microsoft signs.
  - An **MSI** (WiX) for managed PCs, uploaded to HRIS Versions. Unsigned in beta; an OV certificate later if needed.

## 9. Back office (HRIS) changes
- Platform `windows` on licenses and activations, plus the key digit `7`.
- Versions: a third platform value, so `platforms` can be `mac`, `ipad`, `windows` or combinations. Add Windows installer upload (`.msix`/`.msi`/`.exe`) with size and SHA-256, next to `.dmg` and `.ipa`.
- Public download for Windows keys; `releases/latest?platform=windows`.
- FE: a Windows option in the license form, the Versions page and event labels.

## 10. Quality and testing
- **Golden files:** a shared folder of reference projects (`.occa`) with expected outputs (object counts, geometry hashes, PDF colour operators, IOF XML). Both the Mac app (`swift test`) and Windows (`dotnet test`) must pass them.
- **Ported unit tests:** the CakraCore tests (geometry, symbols, cut/holes/corners, descriptions, contours, license validity) are ported to xUnit.
- **UI tests:** WinAppDriver or Appium for the main flows: new project, draw, export, license.
- **Manual pen tests:** on a Surface and a Wacom tablet.
- **Round-trip check:** a Mac project opened and saved on Windows, then reopened on Mac, is identical (JSON comparison).

## 11. Milestones and deliverables
| Milestone | Deliverable | Exit criteria |
|---|---|---|
| M0 Setup | Decisions made. Accounts: Esri ArcGIS Location Platform, Microsoft Partner Center (Store). Spikes: read Mac `.occa` (LZFSE), Esri tiles and HD image, Esri offline terms | Spikes pass |
| M1 Foundation | Open and display Mac `.occa` projects (read-only), basemap, symbols | Golden display tests pass |
| M2 Drawing | Draw and edit tools, save `.occa` | Round trip with Mac passes |
| M3 Output | PDF CMYK, raster, OMAP/DXF, OCAD/OOM import | PDF matches the Mac reference |
| M4 Courses and contours | Course planner, IOF XML, contours | Course golden tests pass |
| M5 Licensing and distribution | HRIS Windows keys, Microsoft Store submission (private audience first), MSI for HRIS, updater | Production activation passes; Store certification passes |
| M6 Beta → 1.0 | Fixes from testers | Success criteria in BRD §10 |

## 12. Decisions and open questions
1. ~~Tech stack~~: decided, .NET 8 + WinUI 3 + Win2D, Windows only (§8.1).
2. ~~Basemap provider~~: decided, Esri World Imagery through ArcGIS Location Platform (§8.4).
3. ~~Windows key digit~~: decided, `7`.
4. ~~Installer type~~: decided, MSIX for individual users plus MSI for managed PCs at clubs and schools.
5. ~~Minimum Windows version~~: decided, Windows 11 24H2 (build 26100) minimum, tested on 26H2 and 25H2 (§3).
6. ~~Signing and distribution~~: decided, Microsoft Store (free signing) plus an MSI from HRIS (§8.4).
7. **Open (recommended: yes):** a Starter (free) plan on Windows with the same limits as on Mac (BRD §13).
8. ~~Test machine~~: decided, every build is tested by the owner on a Windows 11 PC/laptop before release.
   - **Open:** how builds reach that PC. Options: (A) GitHub Actions builds the MSIX/MSI on a Windows runner and the owner downloads it (recommended), and/or (B) build and install directly on the PC over SSH.
