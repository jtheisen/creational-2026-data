# Collection import target graph

Status: approved design, 2026-10-17.

## Purpose and scope

The Blazor Server control plane lists Wikimedia input collections and starts an
end-to-end import for a selected collection. The first release may list only
the pinned English Wikipedia 2026-10-01 current-content export. It imports dump
shards, produces the candidate pool and parsed taxobox tables, imports Open
Tree Taxonomy (OTT) 3.3, and produces the existing OTT–Wikipedia correlation.
Image resolution, preview, and original downloads are outside this collection
pipeline. Existing verified work must be adopted rather than recomputed.

The UI shows *targets*: named units of work with one or more output files and
one completion record. It is a stage/target overview, not a Blob file browser.
An operator can mark a target stale and run the collection import again. That
reruns only the target and any descendants whose inputs actually change.

## Existing assets to adopt

The 2026-10-17 read-only Blob inventory found 19 dump shards plus `SHA256SUMS`
and `_COMPLETE.json` under
`wikimedia-imports/enwiki/2026-10-01/` (46,611,686,601 total stored bytes,
including metadata files). It found 19 candidate-pool groups under
`wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/`, each with
`pool.parquet`, `dependencies.parquet`, `redirects.json`, `report.json`, and
`_COMPLETE.json`, plus the overall markers and reports (99 blobs, 817,276,137
bytes). The older `extract-v2/` prefix contains the alias seed and redirect
indexes; its unfinished extraction outputs are historical and must not be
mistaken for the current candidate pool.

The local workspace has 19 complete `results/candidate-parse-v1/` groups
(235,159,174 bytes), OTT 3.3's 416,869,933-byte source TSV and two converted
outputs (138,554,601 bytes), and five OTT correlation v3 outputs (78,795,362
bytes). These are not currently adopted as cloud targets. Bootstrap uploads
and verifies these local assets once, adds group completion manifests where
they do not exist, and only then records them as complete in Azure Tables.
The correlation remains labeled investigative; adopting it preserves work but
does not promote suggested matches to validated taxon identities.

## Target graph

Definitions and edges live in versioned application code. Azure Tables stores
target instances and their outcomes, not an arbitrary user-editable graph.
For a collection with `N` shards, the initial definitions are:

| Target kind | Instances | Depends on | Output group |
| --- | --- | --- | --- |
| `source-manifest` | 1 | Pinned collection URL | Checksums and shard inventory |
| `dump-shard` | N | Manifest entry | One verified `.xml.bz2` Blob |
| `alias-index` | N | Corresponding dump shard | Redirect-index JSON |
| `alias-seed` | 1 | All alias indexes | Resolved aliases JSON |
| `candidate-pool-shard` | N | Corresponding dump shard and alias seed | Four files and completion marker |
| `candidate-pool-finalize` | 1 | All candidate-pool shards and alias seed | Overall marker, discovered-alias check |
| `parse-shard` | N | Corresponding pool shard and alias seed; pool finalizer must succeed before it starts | Four Parquet files, report, completion marker |
| `parse-finalize` | 1 | All parse shards | Overall parsed-output manifest |
| `ott-source` | 1 | Pinned OTT 3.3 URL | Verified taxonomy TSV Blob |
| `ott-names` | 1 | OTT source | Names Parquet, report, completion marker |
| `ott-correlation` | 1 | Parse finalizer and OTT names | Four Parquet files, report, completion marker |

Alias indexes and the alias seed can be adopted from `extract-v2/` for the
first collection. A new collection needs a fresh seed or an explicitly
validated carried-forward seed before candidate-pool work starts. If the
candidate-pool finalizer discovers aliases that differ from the seed, it fails
without publishing a successful overall marker. The operator or a later
automated flow can create a new seed/recipe version and rerun affected pools.
There is no dependency cycle.

The parser also consumes the alias seed to identify taxobox families. The pool
finalizer is a readiness gate for parsers, not a content fingerprint
dependency of every parser. If one pool shard changes, only its corresponding
parser should become dirty; changing the finalizer marker alone must not force
all 19 parsers to run.

## Target identity, validity, and staleness

Each target has a stable identity such as
`enwiki/2026-10-01/candidate-pool-shard/p10p1113635`; the UI groups identities
by stage and collection. An Azure Table entity stores the target kind and key,
declared recipe version, current completion-marker Blob path, verified output
fingerprint, dependency-output fingerprints used at completion, completion
time, last outcome/error, `staleRevision`, and `satisfiedStaleRevision`.
The output manifest contains the exact member paths, lengths, checksums, and
producer/source provenance. Derive the output fingerprint from verified member
content and its semantic format, not the attempt prefix or marker timestamp.
Store manifests in Blob, not as large Table
properties. Table ETags protect concurrent updates to a target's revisions and
completion pointer.

A target is current only when its marker and all required members verify,
its recipe version matches the declared version, its recorded dependency
fingerprints match the current dependencies, and
`staleRevision == satisfiedStaleRevision`. An absent or partial output group
is missing, regardless of what Table Storage says. A source manifest is pinned
by its checksum; unexpected mutation under the same collection ID is an error,
not silent replacement.

`Mark stale` increments only that target's `staleRevision`. It does not update
all descendants in Table Storage. During planning, descendants are blocked
until stale dependencies finish. After a rebuilt dependency finishes, its
verified output fingerprint determines whether descendants need rebuilding.
If the output is unchanged, they remain current. Marking a target stale while
it runs leaves its new revision unsatisfied; the running attempt can only
satisfy the revision it captured at start. A recipe change is an explicit
version bump, rather than an incidental source-code hash change that would
invalidate all expensive legacy results.

For example, marking one `parse-shard` stale runs only that parser target; the
correlation reruns if that shard's verified output fingerprint changes. Marking
one `dump-shard` stale recopies it, but the candidate pool remains current if
the same bytes and checksum result. Marking `ott-correlation` stale reruns only
the correlation. The planner reports both the work it will run and why.

## Execution and publication

Clicking **Import collection** evaluates the graph and starts eligible work in
topological order. A second click while the collection is active returns the
existing run rather than launching duplicate targets. Independent shard
targets can run with configured limits. The App Service hosted copier handles
external source transfers; CPU-heavy transforms run as Container Apps jobs.
Azure Queue remains the dispatch mechanism for compute work. The control plane
re-evaluates readiness after each target completion rather than storing a
second copy of the dependency graph in Table Storage.

The copier's live per-file byte ledger is in memory, as agreed. A page reload
reads the same process ledger; an App Service restart may abandon a copy. The
durable target row records the last completed or failed outcome, not every byte.
On a later Import, an abandoned copy is treated as incomplete and can be
started again. Existing verified Blob outputs remain untouched until a new
attempt is complete. For generated groups, write to an attempt/version prefix,
verify every member, then publish a completion marker and move the Table
completion pointer. The existing adopted prefixes remain valid until a
replacement group is published. Dump and image overwrites use staged blocks;
do not commit an incomplete transfer over a valid current Blob. After a
verified replacement is published, explicitly delete the superseded Blob
version by ID; with blob soft delete enabled, Azure retains that version for
the configured retention period and then removes it permanently. A failed
cleanup leaves the new current version usable and is reported for retry.

Workers write concise failures to their target entity and detailed diagnostics
to logs. The control plane also reconciles Container Apps execution status for
workers that terminate before writing a failure. A failed target blocks its
dependents but leaves other independent targets and prior verified outputs
available. Retrying a collection replans from verified state.

## Dashboard

The collection list initially contains the hardcoded October 2026 export and
can later be populated from official Wikimedia manifests. The collection page
shows stage groups, target counts by current/missing/stale/running/failed state,
and each target's name, output-group summary, last completion, stale reason,
and error. It offers **Mark stale** per target and **Import collection** for
the graph. A plan preview makes the selected work and downstream effects
visible before a rerun. It does not expose arbitrary Blob navigation or
individual artifact deletion. Blazor components refresh their view of Table
state and the live copy ledger at regular intervals.

## Bootstrap and validation

Bootstrap is idempotent and non-destructive. It verifies the existing dump
manifest, shard lengths and hashes, candidate-pool completion markers and
members, and alias seed/index provenance. It uploads the local parse, OTT,
and correlation groups to their versioned Blob prefixes, computes any missing
per-file checksums, verifies uploaded lengths and hashes, and writes completion
markers last. Only verified groups become current Table targets. A rerun skips
already adopted groups and reports any discrepancy for inspection; it never
overwrites an existing verified group merely to make Table state look current.

The first **Import collection** after a successful bootstrap should schedule
no expensive work. A stale parse-shard test should schedule that target and
only content-changed descendants. A stale dump-shard test should recopy one
shard without touching the other 18 and should leave descendants current if
the checksum is unchanged. Tests must also cover a missing group member, a
failed worker, concurrent stale marking, and an App Service restart during a
copy. The restart test verifies safe retry on the next manual import, not
automatic continuation.

## Boundaries

Image-related work, arbitrary collection discovery, taxon identity review,
and automatic recovery of in-flight App Service copies are separate work.
The current schema and target definitions should permit later collections
without assuming that every future source has exactly 19 shards.
