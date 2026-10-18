# Wikipedia lifeform extraction: intended architecture

Recorded: 2026-10-15; updated 2026-10-17. Status: the import and one-pass
candidate pool have been proven on a VM, and local taxobox parsing has been
completed. The collection control-plane and worker code now exists under
`apps/` and `workers/`; deployment and live orchestration validation remain.

The collection target graph, adoption workflow, App Service copier, queue
dispatcher, Container Apps worker contract, and dashboard are described in
[the collection operations guide](docs/COLLECTION_IMPORT_OPERATIONS.md) and
[the worker deployment guide](infra/collection-worker.md). The 22 local output
groups have been published to Blob Storage. Verify-only adoption passed for
all 83 targets; applying the Azure Table rows currently awaits Table Data
Contributor permission for the VM managed identity.

## Goal and immediate priority

Extract article text, images, and taxobox information from Wikipedia pages about
lifeforms, using official or well-maintained sources. Start with English
Wikipedia, including extinct organisms and higher taxonomic groups. Coverage of
breeds, cultivars, common-name groups, and lists needs further investigation.

The October 2026 dump is already mirrored to Blob Storage. A one-pass
candidate pool and local raw-taxobox/image-reference tables have been produced.
Rendered text, taxonomy resolution, and image-file resolution still need
investigation before the full pipeline is automated.

## Agreed Azure architecture

| Component | Responsibility |
| --- | --- |
| New .NET Blazor Server app on the existing Linux App Service plan | Control plane and pipeline dashboard: accept an OTT ID or Wikipedia page URL, preview image-copy scope, submit and monitor work, and run bounded async streaming copies of Wikimedia dumps and originals. Compute-heavy parsing runs elsewhere. |
| Azure Container Apps Jobs, Consumption plan | On-demand workers for parsing and transformations that need substantial compute or memory. Start for assigned work, report progress, and exit. Compute is billed while executions run. |
| Azure Tables | Persistent target state, stale revisions, attempts, outcomes, and references to verified output groups. |
| Azure Blob Storage | Dated source dumps, filtered wikitext, extracted article/taxobox data, image manifests, images, and later derived artifacts. |
| Azure Storage Queue | Pending work consumed by workers, with bounded concurrency and retry handling. |

The dashboard and compute workers may share a codebase/container image but have
separate execution roles. Compute-job state must survive worker termination and
dashboard deployment. Copy progress lives in an in-process ledger; copy jobs
need not survive an App Service restart. Use managed identities for Azure
resource access where supported.
Place the new app under an `apps/` subfolder so other Creational apps can live
beside it in this repository. A full taxon browser is not needed for the first
version; the selection preview starts from an entered OTT ID (the entire
descendant subbranch, including the named node) or Wikipedia URL (one page).

Data Factory was considered for its visual pipeline monitoring. The chosen
direction is a custom dashboard and workers because our stages require custom
code and Container Apps orchestration would need integration work in Data
Factory. Azure SQL Serverless was considered, but its approximately one-minute
resume latency was undesirable for dashboard access. Cosmos Serverless was
initially preferred; Azure Tables is now being considered because the existing
storage account may simplify deployment. Containers are a deployment choice,
not a parsing requirement.

## Orchestration and observability decisions

Discussed 2026-10-16 and updated 2026-10-17; the collection-import subset is
implemented in code, while image selection and live deployment remain future work:

- Keep the control plane in a Blazor Server app on the existing App Service
  plan for the initial deployment. It starts runs, advances stages, presents
  progress and failures, and hosts the async streaming copier. CPU-heavy
  Python processing runs in Azure Container Apps Jobs. Keep transfer buffers
  bounded and measure the effect of copies on dashboard responsiveness.
- For interactive image-selection planning, have the .NET control plane copy
  the compact Parquet catalog from Blob Storage and query its local copy with
  DuckDB.NET. It should refresh the copy when a completed catalog version is
  published. The existing VM probe confirmed that DuckDB.NET can query the
  Parquet data, but an all-shard remote column scan took about 30 seconds and
  DuckDB's direct managed-identity mode hit token throttling while opening 19
  Blob files. Keep large extraction and image-processing tasks in workers;
  selection planning can remain in the control plane. See
  [the DuckDB.NET probe](benchmarks/duckdb_net_probe/README.md).
- Use one App Service hosted streaming copier for Wikimedia dump shards and
  selected original images. It fetches sources with a Wikimedia-compliant
  User-Agent and stages bounded blocks in Blob Storage. Keep a singleton
  in-memory ledger with one entry per file for status, bytes, and errors;
  Blazor pages refresh from it at regular intervals. A page refresh creates a
  new UI circuit but does not stop the hosted copier. A process restart loses
  in-flight work and its ledger; automatic recovery is not required. In an
  October 2026 probe, both Azure
  `Copy Blob` and `Put Blob From URL` failed for a public Wikimedia original
  with `CannotVerifyCopySource`/403, while a VM fetch plus Blob upload worked.
  The cause of the server-side rejection was not established. Use a configurable
  total concurrency ceiling of five, with separate source-specific limits;
  start with one image download at a time because a three-at-once probe
  encountered HTTP 429. The initial probe transferred
  140 KB, 4.6 MB, and 9.8 MB sequentially in 1.8 seconds total, which is not
  a sustained throughput estimate. Do not run a transfer inside a web request.
- Before creating an image-copy job, show a paginated preview for the entered
  OTT subbranch or Wikipedia page. Include all candidate files, whether hosted
  on Commons or locally on Wikipedia; the original-import eligibility rule is
  still undecided. Resolve Wikimedia thumbnail URLs and license metadata for
  visible rows in small, serial API batches and cache the results by resolved
  file identity; do not fetch metadata for an entire large branch merely to
  open its first preview page. Display thumbnails directly in the browser. Show a
  concise license badge, attribution, and a link to each source file's
  description page; mark missing or ambiguous rights data for review. Do not
  import thumbnails into Blob Storage for this preview. Show the total candidate
  count; any rights-category counts must be marked partial until all candidate
  metadata has been resolved. The final **Import originals** button submits the full
  selection allowed by the eventual import rule, not only the visible page.
  Freeze that selection in the in-memory job ledger when the button is pressed,
  then start background copying and navigate to its progress page.
- Give each image-copy job a scrollable image-list page. Keep the job's
  selected-image manifest in memory so its membership does not change when the shared
  image inventory changes later. Show thumbnails, source page, filename, copy
  status, size when known, and failure details; include images that were already
  present and therefore skipped by the copy operation. Page and filter the list
  server-side, and load thumbnails only for visible rows. Keep per-job outcomes
  in memory separately from the persistent image inventory; they disappear on
  an App Service restart.
- Track each canonical image once in a persistent image inventory,
  independently of the runs that selected it. A per-image Table entity records
  the last completed copy outcome, expected source bytes when known, actual
  destination bytes when loaded, destination blob name, and last verified blob
  version ID. Live transfer progress belongs to the in-memory ledger. Keep small summary
  entities partitioned by a stable hash
  of the image key. A state transition updates its image entity and summary
  entity atomically in the same Azure Table partition; the dashboard sums only
  the summary entities for global unique-image counts and bytes. Run/batch
  aggregates remain useful for individual run progress, but must not be summed
  for global storage totals because selections and retries overlap. Reconcile
  inventory against Blob properties if a process restart leaves uncertainty.
- Reuse an existing image blob by default when a later selection includes the
  same canonical image. Allow an image to be marked stale explicitly; the
  marker does not itself launch a transfer. The next selection that includes
  that image schedules one refresh, while concurrent selections share the
  same pending copy. Clear the stale marker only after the replacement copy is
  verified and the inventory points to it; leave it set after a failed copy.
  Use a monotonically increasing stale revision so a new stale mark made during
  a refresh is not accidentally cleared by that refresh's completion. Store
  images under a repository-specific prefix and their canonical Wikimedia
  filename, preserving the original filename at the end of the blob path.
  Refresh by staging blocks for the same blob name, with Azure Blob versioning
  enabled; commit the new block list only after the transfer is validated.
  Keep the last verified version ID in the inventory until the new version is
  complete and verified. A failed refresh remains stale and retains the old
  verified version.
  On success, atomically advance the inventory to the new verified version and
  adjust logical current-image byte counters by the difference between old and
  new sizes; then delete the old version by its version ID. A cleanup failure
  leaves the new image usable and records the old version for retry.
  The chosen cleanup policy is to soft-delete the superseded version after a
  verified refresh and let Azure permanently remove it when blob soft-delete
  retention expires. Blob versioning and blob soft delete must be enabled on
  the storage account; the Permanent Delete feature is not needed. The app's
  managed identity needs blob read/write and version-delete data actions
  (Storage Blob Data Owner covers these, though a narrower custom role is
  possible). Record cleanup failures for retry. Soft-deleted versions still
  incur storage charges during retention. Versioning and soft delete apply at
  the storage-account level, including other containers in this account;
  confirm the settings before the first refresh.
- Have the App Service hosted copier stream each dump shard in bounded blocks
  from Wikimedia to Blob Storage and report per-file bytes in its in-memory
  ledger. Do not wait for completion inside a web request. A process restart
  ends the current transfer; an operator may start it again. The largest
  October 2026 shard is 2,807,278,393 bytes. Measure sustained App Service
  throughput and dashboard responsiveness before relying on this for another
  full dump set.
- Check the source manifest and copied blob length at import. Verify the
  manifest's SHA-256 while the Parquet worker streams the copied blob; only
  regard that shard as validated after the checksum passes. The import's
  write transactions are negligible at 19 files, inbound Azure transfer is
  free, and normal Blob capacity charges still apply.
- Use finite job executions for independently retryable units such as a source
  shard and processing stage. The same Python container image can run different
  commands or modes. Workers do the assigned work and report progress/results;
  they do not coordinate the entire pipeline.
- Run one candidate-pool job per source shard, with a configurable concurrency
  limit. Each job reads its assigned compressed XML blob once and writes its
  own Snappy Parquet pool, dependency file, redirect map, report, and completion
  marker. Jobs can run in parallel because the
  [taxobox alias seed](GLOSSARY.md#taxobox-alias-seed) is known
  before selection. After all shards finish, one finalizer verifies outputs,
  resolves the collected redirects, compares them with the seed, and publishes
  the overall completion marker. A changed alias set requires a new seed and
  pool run to avoid silently missed pages.
- Use the successful Standard_F1s VM run as a worker-sizing baseline: one
  vCPU, 2 GiB RAM, 19 shards in 5 hours 30 minutes, with peak worker memory
  around 314 MiB. Start container trials near 1 vCPU / 2 GiB and measure
  shard time, memory, source throughput, and price. F1s is a VM size, not a
  Container Apps SKU; container compute pricing must be measured separately.
  Start with modest parallelism and raise it only if Wikimedia and Blob
  throughput remain healthy.
- Persist run and work-item state in a database that survives App Service or
  worker restarts. Azure Tables is a candidate, with a current preference for
  its simpler deployment in the existing storage account; Cosmos DB Serverless
  remains an option. This choice is not final.
- Use Azure Storage Queue messages to dispatch work. A table update is not the
  work trigger. Keep queue payloads small and pass blob references and IDs.
- Reflect stage dependencies in the control plane: the source manifest and
  taxobox alias seed precede candidate-pool jobs; all validated shard outputs
  precede finalization; later parsing consumes the completed Parquet pool.
- On startup and periodically, a worker records its work-item ID, attempt,
  current status, and progress. On a fatal Python exception, it should write a
  concise error summary to the state record, log the traceback to stderr, and
  exit nonzero (or re-raise). A caught-and-swallowed fatal exception would
  incorrectly mark the Container Apps execution successful.
- Container Apps execution status is the fallback when a worker is killed or
  crashes before updating its state record. The control plane reconciles stale
  work records against execution status and decides whether to retry or fail
  the run. Outputs and work items must tolerate at-least-once retries.
- Send worker stdout/stderr and platform diagnostics to Azure Monitor / Log
  Analytics. The dashboard shows the concise error summary and links to logs
  filtered by run ID, work-item ID, attempt, and execution ID. Store full
  tracebacks in logs rather than in Table entities.
- Azure Tables appears sufficient for the modest run/shard metadata volume.
  Its key/attribute model has no secondary indexes or bulk update by filter;
  entity-group batches are limited to 100 operations in one partition, and
  entity/string size limits apply. None is currently a blocker for this
  metadata use. Avoid storing logs or large payloads there.
- Table Storage and Cosmos Serverless costs are both expected to be negligible
  at this scale when progress is written periodically rather than per page.
  Cost is not the main selection criterion; deployment simplicity and needed
  query/concurrency behavior are.

## Intended data flow

1. Discover a dated official export; persist its manifest and checksums.
2. Stream each compressed source file through the App Service hosted copier
   into Blob Storage, tracking per-file progress in memory.
3. Dispatch independent, bounded-parallel shard workers that stream and verify
   each compressed XML blob through decompression and a page reader.
4. Retain broad lifeform candidates and supporting taxonomy pages in Snappy
   Parquet shards with complete original wikitext and provenance; finalize only
   after every shard and the alias comparison pass.
5. Extract article text, raw taxobox parameters, and resolved taxobox fields.
6. Produce a deduplicated image manifest with image roles and attribution data.
7. Present selected images through directly linked Wikimedia thumbnails; on
   **Import originals**, queue the selection in memory and run bounded,
   monitored image transfers through the App Service hosted copier.
8. Run optional later processing against persisted outputs.

Use one source file per import and candidate-pool task, and batches of images
per copy job. Keep large payloads in blobs; use Azure Queue for compute work
and the in-memory ledger for copy work.

## Sources and extraction considerations

- Primary source: official monthly **MediaWiki Content File Exports**, current
  revisions rather than full history. Legacy XML dumps are scheduled for
  retirement in January 2027.
- The October 2026 English current export was measured at 46,611,684,453 bytes
  compressed across 19 files (46.6 decimal GB), excluding image binaries. This is
  a dated observation, not a fixed size or future capacity guarantee.
- Its one-pass candidate-pool run completed all 19 shards in 19,829.5 seconds
  (5 hours 30 minutes) on a Standard_F1s VM and retained 503,735 candidate
  pages. At the observed West Europe Linux pay-as-you-go list rate of
  $0.0568/hour, that interval represents about $0.31 in VM compute, excluding
  storage and transactions. The VM was already allocated; this is a workload
  attribution, not necessarily an incremental bill amount.
- Streaming avoids retaining the decompressed dump, but still requires reading
  and decompressing all input bytes to discover relevant pages.
- Recognize the taxobox template family and aliases, including Taxobox,
  Automatic taxobox, Speciesbox, and specialized variants. Use Wikidata taxon
  information and sitelinks as a complementary coverage signal, not as a silent
  replacement for Wikipedia classification.
- Automatic taxoboxes can obtain ancestors from separate Template:Taxonomy/...
  pages and Lua/template logic. Article parameters alone do not necessarily
  contain the displayed classification.
- Preserve raw taxobox parameters separately from resolved/displayed values,
  recording provenance for both. Keep page ID, title, revision ID, timestamp,
  and export identity with selected content.
- Official Wikimedia Enterprise snapshots are an optional source of rendered
  HTML and wikitext for expanded taxoboxes and text extraction. Monthly access
  requires a free account. Whether to use these, resolve taxonomy locally, or
  fetch selected rendered pages remains an experimental decision.
- Resolve image filenames using Wikipedia/Commons APIs. Distinguish taxobox
  photographs, range maps, and other illustrations. Preserve file metadata and
  attribution. Copy the current original file, not a rendered thumbnail or
  resized derivative, for the first image-loading pipeline. Record the source
  file revision timestamp, SHA-1, byte size, and dimensions where available so
  a later source overwrite can be distinguished from a local stale refresh.
  The dashboard may request small Wikimedia thumbnails for visible preview and
  job-list rows without storing those as separate image inventory items. Obtain
  license badges from the file-description metadata exposed by Wikimedia's
  `imageinfo` `extmetadata` API, including `LicenseShortName`, `LicenseUrl`,
  `UsageTerms`, `Artist`, `Credit`, and `NonFree` when available. These fields can
  contain HTML, may be missing, and are unreliable for some multi-licensed
  files; render them safely and retain the source file-page link for review.
- Keep borderline candidates for later inspection so selection rules can be
  improved without rescanning upstream data unnecessarily.

## Reliability and operating costs

- Bound memory, use backpressure, and checkpoint at source-file/shard boundaries.
- Design work and output paths to tolerate duplicate queue deliveries and retries.
- Mark outputs complete only after successful processing; recover interrupted work.
- Persist retry counts and per-item failures. Define cancellation and worker
  recovery behavior explicitly.
- Show bytes processed, pages scanned/retained, images completed/failed, timings,
  and execution identifiers in the dashboard.
- Limit source downloads and image requests independently, honoring upstream
  rate limits and retry guidance.
- Workers have no compute charge between executions on Consumption, but retained
  storage, database operations, logs, and any container registry remain billable.
  A running worker waiting on network I/O still incurs compute charges.
- Job framework, Cosmos partitioning, Azure region, and final compute-worker
  resource sizing are not
  decided. Avoid choosing a job framework that introduces an unintended
  always-on SQL/Redis dependency.

## Remaining validation before automation

Test the hosted streaming copier on one real Wikimedia shard, including its
byte progress, sustained throughput, and effect on dashboard responsiveness;
validate length and SHA-256 in the following scan. Benchmark one-shard container jobs at
the F1s-like resource size, then increase concurrency while watching source
throughput and retries. Continue checking representative manual and automatic
taxoboxes, aliases, redirects, extinct taxa, and specialized boxes. Measure
unique image references and compare raw markup with rendered taxoboxes to
identify missing dependencies before implementing later stages.

## References

- [Content File Exports](https://wikitech.wikimedia.org/wiki/MediaWiki_Content_File_Exports)
- [October 2026 English current export](https://dumps.wikimedia.org/other/mediawiki_content_current/enwiki/2026-10-01/xml/bzip2/)
- [Wikimedia download notices](https://dumps.wikimedia.org/)
- [Speciesbox documentation](https://en.wikipedia.org/wiki/Template:Speciesbox/doc)
- [Wikidata Taxonomy project](https://www.wikidata.org/wiki/Wikidata:WikiProject_Taxonomy)
- [Enterprise snapshots](https://enterprise.wikimedia.com/docs/snapshot/)
- [Images API](https://www.mediawiki.org/wiki/API:Images)
- [Imageinfo API](https://www.mediawiki.org/wiki/API:Imageinfo)
- [Container Apps Jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
- [Container Apps billing](https://learn.microsoft.com/en-us/azure/container-apps/billing)
- [Local taxobox parsing results](LOCAL_PARSE_RUN.md)
- [Copy Blob API, progress, and billing](https://learn.microsoft.com/en-us/rest/api/storageservices/copy-blob)
- [Put Blob From URL fallback](https://learn.microsoft.com/en-us/rest/api/storageservices/put-blob-from-url)
- [Azure bandwidth pricing](https://azure.microsoft.com/en-us/pricing/details/bandwidth/)
- [Azure retail VM prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices)
- [Cosmos DB Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless)
- [SQL Serverless auto-resume](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-auto-pause-resume?view=azuresql)
