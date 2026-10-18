# Unattended dump mirror

This records the October 2026 VM-based copy. The intended automated import is
documented in [ARCHITECTURE.md](ARCHITECTURE.md): the control plane should first
try `Put Blob From URL`, with a separate streaming job as fallback.

The `wikimedia-copy` systemd service runs on `wikipedia-dev` as `azureuser`.
It mirrors all 19 files in the October 1, 2026 English current export to:

`https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/`

`copy_dump_set.py` uses the managed identity, Python's standard library, curl,
and the identity-token helper in `stream_dump.py`. It streams 16 MiB blocks;
no complete source file is written to local disk. It saves the source manifest,
checks each SHA-256 before committing a blob, and records that checksum in blob
metadata. Existing blobs are skipped only if their length and recorded checksum
match. The first two files are explicitly trusted based on the prior verified
single-file transfers, with their lengths checked again.

Transient Azure operations are retried; failed source streams restart that file
up to five times before the script continues to other files. A failed overall
run exits nonzero and systemd retries after five minutes, skipping completed
files. An exclusive lock prevents simultaneous runs. The service is enabled at
boot. Completion is marked by `_COMPLETE.json` after checking all 19 blobs.
Unused uncommitted blocks from failed attempts are left for Azure expiry.

Commands:

```sh
ssh wikipedia-dev 'sudo systemctl status wikimedia-copy --no-pager'
ssh wikipedia-dev 'tail -20 /home/azureuser/copy_dump_set.log'
ssh wikipedia-dev 'sudo systemctl stop wikimedia-copy'
ssh wikipedia-dev 'sudo systemctl start wikimedia-copy'
```

The service becomes inactive after a successful run; check the log for the final
`complete` record and Blob Storage for `_COMPLETE.json`. To inspect the inventory
with `python3 copy_dump_set.py --check`, first let the service finish or stop it:
the inventory command also takes the exclusive lock.

Source/network failures can extend the roughly 2–3 hour estimate. Repeated
permanent failures remain visible in the log and require intervention; no
notification service is configured. Stopping the service leaves completed blobs
intact. Failed files restart from their beginning, not from a partial stream.
