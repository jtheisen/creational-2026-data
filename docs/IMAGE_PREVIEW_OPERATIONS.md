# Image preview operations

The `/images` page accepts an OTT ID or an English Wikipedia article URL. OTT
selection includes the ID and all descendants in the full OTT 3.3 hierarchy,
then uses the investigative exact-name correlation to find Wikipedia pages.
The gallery shows distinct resolvable image files from taxobox fields, file
links, and galleries. It counts unresolved markup separately. It fetches
Wikimedia `imageinfo` thumbnail and license metadata only for the visible 24
files. Once `resolved-image-metadata` completes, it also shows the selected
branch's known original-byte total and counts of resolved, missing, and
non-image media, and ambiguous filenames. No image bytes are copied.

On first use after an App Service process restart, the control plane downloads
the compact OTT hierarchy, exact matches, and the 19 parsed article/image
reference pairs into temporary local storage. It verifies each Blob's length
and SHA-256 against its completion manifest. The first Azure-backed preview
of OTT 114168 took 14.3 seconds in the development environment; a second run
with a warm local cache took 1.5 seconds. Those are single observations, not
App Service latency guarantees. A changed contributing target fingerprint
causes a new local catalog version to be downloaded. A published edge marker
whose source SHA-256 differs from the current `ott-source` manifest is refused.

## OTT hierarchy artifact

`taxonomy_names.parquet` omits noncanonical intermediary nodes, so it cannot
serve as a complete subtree index. The pinned OTT 3.3 `taxonomy.tsv` was
converted once into a 12,559,798-byte `edges.parquet` containing only
`ott_id` and `parent_ott_id`. The source SHA-256 is
`0c798437d90dedd1511e0ba2470323b6ffad54f2919634fe1a132bcc71c7239f`;
the edge file SHA-256 is
`eb245cc7acfb5b4f0e13a8cdc18c93c8aa07c9af67c11ddb4f1c7ddd507dcc74`.
It was published under
`wikimedia-extracts/enwiki/2026-10-01/image-preview-catalog-v1/ott3.3/`:

- `edges.parquet` is immutable for this source version.
- `_COMPLETE.json` stores `SourceSha256`, the absolute Blob `Path`, `Length`,
  and `Sha256`; it was uploaded only after the edge Blob's length and hash
  metadata were checked.

To regenerate for a changed OTT source, download its `taxonomy.tsv`, run
`dotnet run --project apps/Creational.Dataflow -- --build-ott-edges <source.tsv> <edges.parquet>`,
verify source and output SHA-256 hashes, publish the new edge Blob, then
publish a matching completion marker last. The long-term collection graph
should make this a generated target when another OTT version is imported.

The Wikimedia metadata client uses a descriptive User-Agent with the project
repository URL as contact unless `Copies:UserAgent` is configured. It queries
English Wikipedia first so a local file wins a same-name collision, then asks
Commons for files not found there. Missing rights
fields are flagged for review; badges do not certify import eligibility.
