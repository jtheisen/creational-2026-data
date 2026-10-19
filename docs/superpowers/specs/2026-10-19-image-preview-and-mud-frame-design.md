# Image preview and MudBlazor frame

## Outcome

The operator can enter an OTT ID (including its descendant branch) or an English Wikipedia article URL and inspect the candidate image files before deciding whether originals are worth importing. This release does not copy originals. Existing collection controls remain functional, but the app frame, actions, alerts, and summaries use MudBlazor rather than custom HTML styling. Clicking any noninteractive area of a target row toggles its checkbox.

## Catalog and selection

Use the last completed Azure Table targets as the catalog version. A one-time build converts the full OTT source's ID/parent columns into a compact Parquet edge table in Blob Storage, with a completion marker that records the source and artifact SHA-256 hashes; the retained-name table drops intermediate nodes and would truncate some branches. The dashboard rejects an edge artifact whose source hash differs from the current OTT source target. Lazily download the compact edge table, exact OTT/Wikipedia matches, and each parse shard's articles and image references into a local cache on App Service. Query the local Parquet files through DuckDB.NET. Rebuild the cache when any contributing target fingerprint changes. A Wikipedia URL selects one article title. An OTT ID selects the node and descendants from the full OTT hierarchy, then all pages with an exact-name correlation to those OTT IDs. The correlation is investigative, so the preview labels it as candidate coverage rather than confirmed identity.

Count all distinct resolvable file titles for the selection and page the candidates in a stable order. Report unresolved raw references separately; they cannot be passed to imageinfo. Include taxobox fields, inline file links, and gallery entries with their source role. Do not fetch image metadata for candidates outside the visible page.

## Wikimedia metadata

Resolve each visible file title through the MediaWiki imageinfo API, using Commons and English Wikipedia so local files remain visible. Request a browser thumbnail, description page, original URL/size, and extended license metadata. Cache resolved metadata by canonical file title for a short period. Show a thumbnail, filename, source article, source role, license badge, attribution text, and source-description link. Missing, ambiguous, or unparseable rights data is marked for review; no rights inference is made. API errors should leave the catalog preview usable with a visible metadata error. Use an identifying configured User-Agent and small sequential API batches.

## UI and behavior

Use MudLayout, MudAppBar, MudMainContent, and MudContainer as the shared frame, plus MudBlazor cards, buttons, fields, alerts, and tables on the two existing pages. Keep only small layout rules for wide tables, activity indicators, and the thumbnail grid. The preview is a separate page linked from the collection. It has an input for OTT ID or Wikipedia URL, a search button, total candidate and unresolved counts, and server-side paging. The target table retains ID-based cross-group selection; row clicks toggle selection except on buttons, links, and checkboxes. Collapsed groups and checked IDs survive polling refreshes.

## Limits

Only the pinned October 2026 collection is supported. The exact-name correlation omits noncanonical names and can contain homonyms. The one-time edge artifact needs regeneration when the OTT source changes. A thumbnail is a browser link to Wikimedia, not a stored asset. Original import, persistent image inventory, stale-image markers, and job gallery are a later release.

## Verification

Test URL/OTT parsing and selection rules, catalog query behavior against a small local Parquet fixture, and imageinfo response handling. Run the .NET tests and Release publish, then inspect rendered routes and live metadata when network access permits. Do not start collection imports or copy originals during verification.
