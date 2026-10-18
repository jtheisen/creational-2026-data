# DuckDB.NET Azure Parquet feasibility probe

This is a throwaway .NET console probe for the proposed dashboard/control-plane
queries. It was run on the existing `wikipedia-dev` Ubuntu VM on 2026-10-17,
with .NET SDK 10.0.402 installed in `~/duckdb-net-probe/dotnet`,
`DuckDB.NET.Data.Full` 1.5.6.1, and native DuckDB 1.5.6. The VM had 1.8 GiB
RAM. The probe reads the 2026-10-01 candidate-pool Parquet shards from Azure
Blob and two local name-index Parquet files. It does not write to Blob.

Run on that VM after copying `Probe.csproj`, `Program.cs`, and the two Parquet
files to `~/duckdb-net-probe` and `~/duckdb-net-probe/data` respectively:

```sh
cd ~/duckdb-net-probe
./dotnet/dotnet build -c Release
PROBE_AUTH_MODE=access_token PROBE_DATA_DIR="$PWD/data" \
  ./dotnet/dotnet bin/Release/net10.0/Probe.dll
```

Observed timings on the VM (one run; warm cache and network conditions vary):

| Query | Result | Elapsed |
| --- | ---: | ---: |
| Acquire VM managed-identity token with Azure.Identity | success | 0.36 s |
| One remote shard row count | 7,754 | 0.07 s |
| All 19 remote shards row count | 503,735 | 0.81 s |
| All-shard `title LIKE 'Gadus%'` filter | 2 | 30.24 s |
| Local full Wikipedia/OTT name join | 427,482 joined rows | 0.68 s |
| Local selected-page join | 3 pages | 0.35 s |
| Local `Gadus` subtree recursive join | 5 pages | 1.26 s |

The complete run took 34.36 s and peaked at 201,080 KiB resident memory.
The all-shard row count can use Parquet metadata; the title filter actually
reads remote column data, so its 30 s is the more useful latency signal for
interactive queries. The local joins were quick on this small VM, but the
dashboard's App Service still needs its own capacity/latency test.

The DuckDB Azure extension's default transport failed with a CA-certificate
error on this Ubuntu VM. `SET azure_transport_option_type='curl'` fixed it.
Direct `PROVIDER managed_identity` succeeded for a single file but hit an
Azure IMDS HTTP 429 when globbing all 19 shards, even with one DuckDB thread.
The successful run acquired one token through `Azure.Identity` and passed it
to DuckDB using `PROVIDER access_token`; it made the same all-shard query
without the IMDS throttling. A long-lived service must refresh that token
before expiration and recreate/replace its DuckDB secret. The probe does not
implement that lifecycle. The source blobs currently allow anonymous reads,
so the successful blob reads alone do not establish that storage RBAC is
correct; the token acquisition and secret creation themselves succeeded.

For the dashboard, this supports using DuckDB.NET for selection planning over
small local Parquet catalogs. The 30 s remote scan suggests downloading or
materializing the needed columns locally first, then reserving Blob-backed
queries for background work or infrequent refreshes. This is a feasibility
result, not a final deployment decision.
