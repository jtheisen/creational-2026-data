# Unattended complete-export extraction

**Historical run:** this two-pass service was stopped and disabled after six
completed extraction shards. The one-pass replacement is documented in
[CANDIDATE_POOL_RUN.md](CANDIDATE_POOL_RUN.md). Existing `extract-v2/` outputs
and the complete alias index remain available.

The `wikimedia-extract` systemd service on `wikipedia-dev` runs
`extract_blob_set.py` as `azureuser`, using `/home/azureuser/wikimedia-venv`.

Source:
`https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/`

Destination:
`https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/extract-v2/`

## Behavior

1. Validate the imported manifest, completion marker, 19 source blobs, and
   destination read/write/delete access using the VM managed identity.
2. Stream each compressed source blob to index template redirects, verifying
   its SHA-256. Save an index checkpoint per shard to `_indexes/` in Azure.
3. Resolve taxobox aliases across the entire export and save `aliases.json`.
4. Stream each source blob again, verifying SHA-256, to select taxobox articles,
   taxonomy dependencies, and a separate Taxonbar-without-taxobox review set.
5. Upload each shard's outputs and verify their lengths/checksum metadata.
   Publish its `_COMPLETE.json` last. Remove the local temporary extracts.
6. Check all shard outputs and publish the overall `_COMPLETE.json`.

No complete dump file is stored locally. Only one shard's compressed extracts
occupy temporary disk space. CPU/memory are bounded by streaming, individual
page size, and the template-redirect index. This is a single-worker job; it does
not need a second VM or a database. No image files are fetched.

Each source stem has its own output directory, e.g.:

```text
extract-v2/enwiki-2026-10-01-p10p1113635/
  candidates.ndjson.gz
  dependencies.ndjson.gz
  review.ndjson.gz
  report.json
  _COMPLETE.json
```

The extractor includes the spike's style-skipping and template-name comment
handling fixes and the known Automated taxobox alias. It preserves original
wikitext, IDs, revision timestamp, source identity, and selection/review reasons.
It does not expand taxonomy, render prose, decide final biological subjects, or
download images. Dependency files are subsets, not complete rendering closures.

The existing local investigative artifacts are not overwritten. Schema version
2 adds the review stream, source checksum, and alias scope to the report.

## Progress, retry, and restart

```sh
ssh wikipedia-dev 'sudo systemctl status wikimedia-extract --no-pager'
ssh wikipedia-dev 'tail -20 /home/azureuser/extract_blob_set.log'
ssh wikipedia-dev 'sudo systemctl stop wikimedia-extract'
ssh wikipedia-dev 'sudo systemctl start wikimedia-extract'
```

`_STATUS.json` in the destination records the last completed indexing/extraction
shard. The log includes progress within the current shard. A run first indexes
all 19 blobs, so candidate artifacts appear only after the indexing phase.

Transient storage requests retry with backoff. A failed index/extraction shard
restarts from its beginning, up to three attempts per process. On failure,
systemd retries after five minutes, including after reboot. Persisted valid
indexes and completed outputs are skipped. An exclusive local lock prevents
duplicate workers on this VM.

A fingerprint covers the source manifest, worker code, and parser version.
The runner refuses to mix different fingerprints under the same destination
prefix: use a new version prefix for changed code or configuration. This is
intentional; updating scripts in place during a run is not supported.

Incomplete uploads never count as a completed shard. Uncommitted blocks expire
in Azure; partially committed artifacts are replaced on retry. If the VM is
abruptly killed, `extract-work/` can contain orphaned scratch directories;
inspect and remove those only while the worker is stopped. Disk exhaustion,
permanent access failures, or malformed source pages need intervention and
remain visible in logs. No notification service is configured.

Check prerequisites without starting extraction (briefly creates and deletes an
access-probe blob):

```sh
ssh wikipedia-dev 'wikimedia-venv/bin/python extract_blob_set.py --check'
```

Run that only while the service is stopped, because it also takes the worker lock.
Processing time is CPU-dependent and should be estimated from this VM's first
shards; the transfer job's 2–3 hour estimate does not apply to extraction.

## Redirect-matcher repair, October 16

The initial worker stalled on shard 4 in a catastrophically backtracking regex
for leading comments in template redirects. A process stack sample confirmed
that the CPU time was spent inside that regex. `redirect_target()` now scans
leading whitespace and comments linearly. A timeout-bounded regression covers
comment-heavy non-redirects and valid redirects; the full suite passes.

`migrate_redirect_fix.py` is the one-time maintenance operation used with the
worker stopped. It preserves the previous run and three completed index
checkpoints under `_migrations/redirect-linear-fix/`, validates their source
checksums, and migrates them to the repaired code fingerprint. This is a
controlled compatibility exception to the normal new-prefix rule, because
redirect recognition semantics and extraction output format are unchanged.
Shard 4 restarts from its beginning. Do not rerun that migration after the
run has already been migrated.
