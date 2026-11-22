# Esri World Imagery: terms for storing imagery in a project (M0 spike)

Date: 22 November 2026. Covers BRD §8 ("To check in phase 0: Esri's terms for storing the image offline in a project")
and PRD §8.4. Sources are quoted; anything not verified is marked as such.

## 1. What OCCA does on Mac today

The Mac/iPad app shows live Apple Maps tiles while the user chooses the area, then downloads the locked area once as an
HD georeferenced image (`ImagerySnapshot`, folder `imagery/`) that is stored in the project package and used offline. The
`imagery/` folder is excluded from `.occa` files: it is a per-device cache, never shared.

## 2. What the Esri terms say

Source: *ArcGIS Location Platform Agreement*, revised 21 November 2025
(https://www.esri.com/content/dam/esrisites/en-us/media/legal/platform/platform-legal.pdf), article 3.1(d)
"Resultant Output" (Resultant Output = what the location services return, including basemap tiles):

> 3. Customer and Application Users may only use Resultant Output for visualization purposes and only within
> Customer Applications.
>
> 4. Customer must affix an attribution statement to Resultant Output from the Basemap location service,
> acknowledging Esri and, or its applicable licensor(s) as the source of the Resultant Output.
>
> 6. Neither Customer nor Application Users may scrape, download, or extract Resultant Output, nor cache or store
> Resultant Output except as outlined herein:
> A. Customer may allow pre-caching of Resultant Output as permitted by the caching headers (HTTP/1.1 standard
> or future standard) returned by Location Services to the extent necessary for enabling or optimizing the use of
> the Customer Application;
> B. Customers and Application Users may use Resultant Output in a static format (i.e. PDF, GIF, JPEG, HTML), in an
> unalterable final form;

Article 7.3 adds that on termination the customer must "clear any client-side data cache derived from the terminated
or expired ArcGIS Location Platform subscription".

Article 3.2 points to supplemental licensor terms at https://www.esri.com/legal/third-party-data and 3.3 to the
scope-of-use document at https://www.esri.com/legal/scope-of-use (not read for this spike).

Pricing (https://location.arcgis.com/pricing/, read 22 November 2026): basemap tiles "2M free then $0.15 per 1,000
tiles"; basemap sessions "1K free then $4 per 1,000 sessions"; tile hosts billed: `basemaps-api.arcgis.com`,
`ibasemaps-api.arcgis.com`, `static-map-tiles-api.arcgis.com`.

## 3. Conclusion for OCCA

| Mac behaviour | Allowed with Esri World Imagery? | How on Windows |
|---|---|---|
| Live tiles while choosing the area | Yes (normal use, billed per tile) | Tile cache in memory/disk, honouring the HTTP caching headers (clause 6.A). |
| HD image of the locked area stored in the project for unlimited offline use | **Not covered** by clause 6: a stitched image kept in the project is "storing" tiles beyond the cache headers. | Store the stitched area as a **cache** with the expiry from the tiles' `Cache-Control`/`Expires` headers, re-fetch when expired or when online, and clear it when the license ends (7.3). The cache stays per device (never in `.occa`), as on Mac. |
| Imagery in PDF/PNG exports | Yes, "static format … in an unalterable final form" (6.B), with attribution (clause 4). | Attribution line on every export that includes the basemap. |
| Attribution on screen | Required (clause 4). | "Esri, Maxar, Earthstar Geographics, and the GIS User Community" (`EsriWorldImagery.Attribution`) on the map. |
| GeoTIFF basemap | Not Esri data. | Unchanged. |

So the PRD's fallback applies: **temporary cache plus the GeoTIFF basemap**, not permanent offline storage. In practice
Esri's imagery tiles carry long cache lifetimes, so a locked area usually stays usable offline for days to weeks; the
app must respect the header value rather than a fixed period.

## 4. Open points to confirm with Esri (M1)

- The exact cache lifetime the World Imagery tiles return (`Cache-Control: max-age`); the spike test
  (`BasemapTests.FetchesALiveTileWhenAKeyIsConfigured`) prints it once an API key is available.
- Whether an **"export tiles"-enabled** service ("World Imagery (for Export)") is licensed to ArcGIS Location Platform
  developer accounts for offline packages; if so, the permanent HD image could be kept as on Mac. Not verified in this
  spike (the developer documentation pages on offline use returned 404 when fetched).
- The supplemental licensor terms at https://www.esri.com/legal/third-party-data for Maxar imagery.

## 5. Endpoint used by the spike

Raster tiles with API key authentication:

```
https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}?token=<API key>
```

Configured in `src/Occa.App/appsettings.json` (`Esri:TileTemplate`); the key in `appsettings.local.json`
(git-ignored) or the `OCCA_ESRI_API_KEY` environment variable, and in CI from the `ESRI_API_KEY` secret. The key needs
the *Basemaps* privilege (`premium:user:basemaps`) in ArcGIS Location Platform and should be restricted to that
privilege only.
