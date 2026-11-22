# OCCA for Windows: Business Requirements Document (BRD)

| | |
|---|---|
| Product | OCCA (Orienteering Cakra CAD Application) for Windows |
| Owner | PT. Jendela Cakra Digital |
| Status | Draft for review |
| Date | 22 November 2026 |
| Related | [PRD](PRD.md); the OCCA Mac/iPad app (repo `cakra-pencil`, version 1.1.0); the HRIS License Manager |

## 1. Background
OCCA is an orienteering map drawing and course planning app for iPad and Mac. It follows the IOF map standards: ISOM 2017-2, ISSprOM 2019-2 and ISInOM 6.7.3.
- It is sold through license keys issued by the Jendela Cakra Digital back office (HRIS License Manager): Starter (free, Mac only), Trial, Professional, Lifetime and Organization.
- Mac builds are distributed as a `.dmg` downloaded from HRIS.
- iPad builds are installed today through Xcode, with Ad Hoc links through HRIS planned.

Most orienteering mappers and clubs, in Indonesia and abroad, work on **Windows PCs and laptops**. The tools they use today, OCAD (paid) and OpenOrienteering Mapper (free), both run on Windows.
- **Without a Windows version, OCCA can't reach most of its market.** It also can't be the single tool for a club whose members use mixed devices.
- **Mac/iPad users can't hand work to colleagues who use Windows.** A Windows version that opens the same `.occa` project files fixes that.

## 2. Problem statement
- Prospective customers on Windows can't buy or use OCCA.
- Clubs and federations want one tool across their members' devices. Today they must mix OCCA with OCAD/OOM, which costs format conversions and lost features (courses, field data).
- Sales and support material can't promise "works on your PC".

## 3. Business objectives
| # | Objective | Measure |
|---|---|---|
| BO-1 | Reach Windows users with the same OCCA product and licensing | Windows licenses sold within 6 months of release |
| BO-2 | Make OCCA a cross-device tool | A project made on Mac/iPad opens on Windows and back with no loss (file compatibility suite passes) |
| BO-3 | Keep one back office | Windows keys, versions and installers are handled in the existing HRIS License Manager, with no separate system |
| BO-4 | Keep the brand promise | IOF-correct symbols and print colours (CMYK), the same as on Mac |
| BO-5 | Control cost | No per-user running cost except the basemap imagery provider (see §8) |

## 4. Scope
### In scope (release 1.0 for Windows)
- A native Windows desktop app for **Windows 11, version 24H2 (build 26100) or newer** (x64 and ARM64), tested on the current Windows 11 26H2 and on 25H2. Windows 10 is not supported (its support ended in October 2025). It works with mouse and keyboard, touch, and pen (Surface Pen / Windows Ink).
- Feature parity with OCCA 1.1 for desk work:
  - projects and map area;
  - basemaps;
  - IOF symbol sets;
  - drawing and editing tools;
  - course planner;
  - contours;
  - import and export;
  - field data processing (GPX and `.occafield` import, notes, tracks).
- Full compatibility with `.occa`, `.occafield` and `.occasym` files from Mac/iPad.
- Licensing through the HRIS License Manager with **Windows license keys**, including activation, refresh, release, restore after reinstall, and expiry.
- **Distribution (decided):**
  - **Microsoft Store** (MSIX) for individual users. Microsoft signs the package for free, there is no SmartScreen warning, and updates come through the Store.
  - An **MSI downloaded from HRIS** (License Manager › Versions) for managed PCs at clubs, schools and offices, plus the in-app update check.
  - Licensing stays with HRIS keys in both channels; the Store allows desktop apps to use their own licensing.

### Out of scope (release 1.0)
- Live GPS track recording on Windows. Most Windows PCs have no GPS; field capture stays on iPad and the data moves through `.occafield`.
- Linux.
- Cloud sync between devices. That isn't on Mac/iPad either.
- RTK, which isn't started on Mac/iPad either.

## 5. Stakeholders
| Role | Interest |
|---|---|
| PT. Jendela Cakra Digital (owner) | Revenue, brand, one product line |
| Product owner / developer (Rizky Ajie Kurniawan) | Scope, schedule, code quality |
| Customers: individual mappers, course setters | Draw maps on their PC, open files from colleagues |
| Customers: clubs, federations, schools (Organization plan) | Several seats, mixed devices |
| Back office admins (HRIS) | Issue Windows keys, publish versions, support |

## 6. Business requirements
| ID | Requirement | Priority |
|---|---|---|
| BR-1 | Windows users can buy, activate and use OCCA with the same plans as Mac: Starter, Trial, Professional, Lifetime, Organization. | Must |
| BR-2 | Licenses are per platform. A Windows key works only on Windows, like the separate Mac and iPad keys today. Plans, durations, devices per key and Lifetime major-version coverage behave the same. | Must |
| BR-3 | The Starter plan on Windows has the same limits as on Mac: 3 projects, scales 1:4000 and 1:10000, no fieldwork, contours or course images, and a "Made with OCCA" credit on PDFs. | Must |
| BR-4 | A project saved on any platform opens on any other with no loss of objects, symbols, courses, field data or settings. | Must |
| BR-5 | Printed maps (PDF) use the IOF CMYK colours and colour order, the same as the Mac version. | Must |
| BR-6 | The back office issues Windows keys, stores Windows installers per version and shows Windows activations and events, all inside the existing License Manager. | Must |
| BR-7 | Reinstalling OCCA on the same PC restores the license without a new key or a new seat, as on Mac/iPad. | Must |
| BR-8 | Users install without a blocking warning. The Microsoft Store package is signed by Microsoft (free). The MSI from HRIS is unsigned during beta, with "More info › Run anyway" instructions, and gets an OV certificate if many customers use it. | Must (before selling) |
| BR-9 | The app works offline after activation, for up to 30 days between online checks, the same as Mac. | Must |
| BR-10 | UI language is English at launch, as on Mac/iPad. | Must |
| BR-11 | Satellite/aerial basemap comparable to the Apple Maps imagery used on Mac: **Esri World Imagery (ArcGIS Location Platform)** (decided), with Esri attribution and a GeoTIFF fallback. | Must |
| BR-12 | OCCA is listed in the Microsoft Store (free individual developer account; Microsoft signs the package). | Must |

## 7. Pricing and licensing
- **Key format (decided):** keep `OC{platform}{plan}-XXXX-XXXX-XXXX-NNNN`, with Windows platform digit `7` (Mac = 3, iPad = 5). Example: `OC72-…` for Windows Professional.
- **Plan prices (decided):** separate keys per platform at the same prices as Mac, plus an optional **Mac + Windows bundle**.
- **Lifetime** covers one major version, the same as Mac/iPad.

## 8. Costs and dependencies
| Item | Notes | Estimate |
|---|---|---|
| Development | A new Windows app: UI rewritten, core logic ported (see PRD §9). | Largest cost, see the timeline |
| Code signing | **Microsoft Store** signs the MSIX for free (decided). The MSI from HRIS is unsigned in beta; an OV certificate (about USD 150–300 per year) later, only if needed. Azure Artifact Signing is currently limited to organizations in the US, Canada, EU and UK. | USD 0 at launch |
| Basemap imagery | **Esri World Imagery through ArcGIS Location Platform** (decided). 2 million free basemap tiles per month, then about USD 0.15 per 1,000 tiles. OCCA downloads each project area once as an HD image, so launch usage fits the free tier. To check in phase 0: Esri's terms for storing the image offline in a project. | USD 0 at launch volume |
| Elevation data | AWS Terrain Tiles, as on Mac (free). | 0 |
| HRIS changes | Windows platform, Windows installers, events (see PRD §8). | Small |
| Test hardware | A Windows PC/laptop and, ideally, a pen tablet or Surface for pen testing. | One-off |

## 9. Timeline (proposal)
| Phase | Content | Duration (indicative) |
|---|---|---|
| 0. Decide | Approve BRD/PRD (all key decisions made: .NET 8 + WinUI 3, Esri World Imagery, Microsoft Store + MSI); set up the Esri and Microsoft Partner Center accounts. | 1 week |
| 1. Foundation | App shell, project list, `.occa` read/write, map view with basemap, symbol rendering. | 4–6 weeks |
| 2. Drawing | Drawing and editing tools, symbol panel, layers, undo. | 6–8 weeks |
| 3. Output | PDF (CMYK), raster, OMAP/DXF export; OCAD/OOM import. | 3–4 weeks |
| 4. Courses and contours | Course planner, descriptions, IOF XML; contours. | 4–6 weeks |
| 5. Licensing and distribution | HRIS keys, Microsoft Store submission, MSI, update check. | 2–3 weeks |
| 6. Beta | Testers, file compatibility checks with Mac/iPad, fixes. | 3–4 weeks |

Total: roughly 5–7 months for one developer working with AI assistance. A first beta with drawing, PDF and licensing is possible after phases 1, 2, 3 and 5, in about 4 months.

## 10. Success criteria
- The file compatibility suite passes: projects from Mac/iPad open on Windows and round-trip unchanged.
- The PDF of a reference map matches the Mac PDF: same colours (CMYK values) and colour order, with visual difference within tolerance.
- Activation, refresh, release, restore and expiry work against the production HRIS.
- No blocking warning on install from the Microsoft Store.
- At least 5 beta users draw a complete map on Windows.

## 11. Risks
| Risk | Impact | Mitigation |
|---|---|---|
| A rewrite drifts from the Mac behaviour | Files or prints differ | Shared test files: the same projects and expected outputs run against both apps (PRD §10). |
| Basemap provider terms or costs | Legal or financial | Pick a provider whose terms allow imagery as a drawing reference; keep the GeoTIFF basemap; cache the HD image per project. |
| SmartScreen warns on the unsigned MSI | Friction for managed PCs | Most users install from the Store; the MSI gets an OV certificate if demand grows. |
| Microsoft Store certification rejects or delays a release | Late release | Follow the Store policies (own licensing is allowed for desktop apps); submit early betas to the Store's private audience. |
| Esri terms limit storing imagery offline | Basemap behaviour differs from Mac | Check in phase 0; fall back to a temporary cache plus the GeoTIFF basemap. |
| Pen input differs per device | Poor drawing feel | Windows Ink APIs; test on Surface and Wacom. |
| `.occa` uses Apple's LZFSE compression | Windows can't open Mac files | Use the open-source LZFSE library (BSD) on Windows (PRD §8). |
| One developer for two codebases | Slower releases | Keep shared formats and specs in one place; port the Mac core tests. |

## 12. Assumptions
- The HRIS License Manager stays the single licensing back office.
- Windows users accept English UI at launch.
- Field capture stays on iPad; Windows processes field data.

## 13. Open questions for the owner
1. ~~Platform digit~~: decided, `7`.
2. ~~Bundles~~: decided, separate keys plus a Mac + Windows bundle.
3. ~~Basemap~~: decided, Esri World Imagery (ArcGIS Location Platform).
4. ~~Signing~~: decided, Microsoft Store (free signing); MSI from HRIS unsigned in beta.
5. **Starter plan:** offer it on Windows, as on Mac? Recommended: **yes**, with the same limits:
   - OOM is free on Windows, so OCCA needs a free entry point;
   - Store users expect to download first and unlock with a key;
   - Trial stays the full-feature, time-limited option for serious buyers;
   - the same rules on every platform are simpler to explain and support;
   - Esri usage per Starter user is small (at most 3 projects).
6. ~~Store~~: decided, Microsoft Store for release 1.0.
