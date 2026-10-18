# One-pass candidate pool

The October 2026 run completed all 19 shards in 5 hours 30 minutes on the
Standard_F1s VM, producing 503,735 candidate pages. The intended automated
version uses bounded-parallel, one-shard-per-job workers; see
[ARCHITECTURE.md](ARCHITECTURE.md). This document records the completed VM run.

The `wikimedia-pool` systemd service on `wikipedia-dev` replaces the unfinished
two-pass `wikimedia-extract` run. The old service is disabled; its completed
outputs under `extract-v2/` are retained. The new run reads each of the 19
October 2026 source blobs once and writes to a separate prefix:

`https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/`

For each source shard, `pool.parquet` holds main-namespace, non-redirect pages
with a broad taxobox-template-name or Taxonbar signal. It stores the **entire
original revision wikitext** as a Snappy-compressed column, plus page ID,
title, namespace, revision ID, timestamp, source filename, and selection
signal. The XML page wrapper is not retained. `dependencies.parquet` holds
taxonomy template and Autotaxobox module pages, also with original wikitext.
`redirects.json` holds template redirect targets; `report.json` has counts and
source identity. A shard's `_COMPLETE.json` is written after all four uploads
are verified. No full dump file is stored on the VM disk.

This stage deliberately tolerates false positives such as apparent calls
inside `nowiki`. A later parsing stage can reject those using the preserved
wikitext without rereading the large bzip2 source. Selection remains based on
taxobox-family names plus Taxonbar; it does not claim to include every page
about lifeforms. Images are not downloaded here.

The [taxobox alias seed](GLOSSARY.md#taxobox-alias-seed) comes from the already
completed 19-shard redirect index in `extract-v2/aliases.json`. The one-pass run
also records all template redirects. Before publishing the overall
`_COMPLETE.json`, it resolves
the discovered aliases and requires an exact match with the seed. If a future
snapshot adds or changes a taxobox alias, the job leaves the shard pools
available but marks the overall run incomplete; it must be rerun with a new
seed and destination version. This prevents silently missing pages.

`_RUN.json` pins the source manifest, seed, code, and PyArrow version via a
fingerprint. `_STATUS.json` reports the last completed shard and current
phase. Repeated runs verify and skip completed shards; transient failures retry
the current shard. Scratch output is removed after each attempt. Source SHA-256
is verified while streaming before any shard output is uploaded.

```sh
ssh wikipedia-dev 'sudo systemctl status wikimedia-pool --no-pager'
ssh wikipedia-dev 'sudo tail -20 /home/azureuser/build_pool_set.log'
ssh wikipedia-dev 'sudo systemctl stop wikimedia-pool'
ssh wikipedia-dev 'sudo systemctl start wikimedia-pool'
```

The service uses the VM managed identity for Blob operations and restarts five
minutes after a failure. An exclusive local lock prevents two instances on
the same VM. `build_pool_set.py --check` validates source inventory, alias
seed, and destination access without starting a run. Do not edit the scripts
in place during a run: a code change requires a new destination version.

## Local analysis copy

Run `.venv/bin/python -u download_candidate_pool.py` from the project root to
download the completed pool into ignored `data/candidate-pool-v1/`. The script
checks the overall and per-shard completion markers, downloads both Parquet
files plus reports and redirect maps for all 19 shards, and verifies every
artifact's length and SHA-256. It skips already verified files on reruns.

The local copy contains 503,735 candidate rows in 791,086,781 bytes of
`pool.parquet` files and 151,392 dependency rows in 14,035,395 bytes of
`dependencies.parquet` files. JSON metadata and redirect maps bring the local
directory to about 810 MB. The raw input dump is not needed for later parsing
of these selected pages.

The completed local parsing pass and its derived schemas are documented in
[LOCAL_PARSE_RUN.md](LOCAL_PARSE_RUN.md).
