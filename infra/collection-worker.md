# Collection worker deployment

Build the image from the repository root with
`docker build -f workers/collection/Dockerfile -t <registry>/creational-collection-worker:<version> .`.
The root `.dockerignore` excludes downloaded dumps, local results, virtual
environments, and private keys from the build context. Push the image to a
registry accessible to the Container Apps environment.

The [worker image workflow](../.github/workflows/worker-image.yml) tests the
Python code and publishes `ghcr.io/jtheisen/creational-collection-worker`
on worker-related pushes to `main` or by manual dispatch. Its tag includes the
Git commit SHA; the workflow summary records the immutable image digest.
GitHub initially creates a container package as private, so set the package's
visibility to **Public** in its package settings before configuring the
Container Apps Job for anonymous pulls.

For this project, use a **public GitHub Container Registry (GHCR)** package
such as `ghcr.io/<owner>/creational-collection-worker@sha256:<digest>`.
This avoids a standing Azure Container Registry tier charge. Configure the
Container Apps Job's registry server as `ghcr.io` without credentials for a
public package (for example, `az containerapp job registry set -n <job> -g
<resource-group> --server ghcr.io`). Keep `ContainerJob__Image` pinned to an
immutable digest; update it deliberately with each worker release. Container
Apps still requires the custom image because the worker has project code and
Python/native dependencies that a stock image does not contain. See
[Microsoft's GHCR guidance](https://learn.microsoft.com/en-us/azure/container-apps/github-actions)
and the [job registry command](https://learn.microsoft.com/en-us/cli/azure/containerapp/job/registry?view=azure-cli-latest).
GitHub currently lists public container packages and their bandwidth as free;
check its [billing policy](https://docs.github.com/en/billing/concepts/product-billing/github-packages)
again before deployment because that policy can change.

Create one **Manual** Azure Container Apps Job using that image. Start with
1 vCPU and 2 GiB memory for the pool/parser shard; measure the OTT correlation
job separately, since its DuckDB join may need more memory. The image's default
entry point is `python -m workers.collection`. Give the job's managed identity
`Storage Blob Data Contributor` on `creational`, scoped to the required
containers if possible. It reads input manifests and artifacts and publishes
an immutable attempt output group with `_COMPLETE.json` last.

The App Service control plane starts an execution through the [manual job start
API](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/jobs/start?view=rest-resource-manager-containerapps-2025-07-01)
and supplies the entire container execution template, including image, CPU,
memory, command, and target-specific arguments. Set these App Service settings:

| Setting | Value |
| --- | --- |
| `ContainerJob__ResourceId` | Full ARM ID of the Manual Container Apps Job |
| `ContainerJob__Image` | Pushed image tag, preferably immutable |
| `ContainerJob__ContainerName` | Container name in the job template; defaults to `collection-worker` |
| `CREATIONAL_STORAGE_ACCOUNT_URL` | `https://creational.blob.core.windows.net` |
| `Copies__UserAgent` | A descriptive product/version plus a real project contact URL or email |
| `Copies__VersioningEnabled` | `true` only after confirming Blob versioning in the storage account portal |

The App Service identity needs `Storage Blob Data Owner` (or a custom role with
`deleteBlobVersion/action`) to soft-delete superseded versions, plus `Storage
Table Data Contributor` and `Storage Queue Data Contributor` on the storage
account. It also needs `Microsoft.App/jobs/start/action` and job-execution read permission on the
Container Apps Job. [Container Apps Jobs Operator](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
includes start permission. The VM identity used for bootstrap needs Table Data
Contributor as well as its existing Blob rights.

Before exposing the dashboard, enable App Service Authentication and require
sign-in for every request. The UI can mark targets stale and start billable
imports; it must not be reachable anonymously.

Create the `collection-work` Storage Queue before starting the web app.
The queue dispatcher claims each target with an Azure Table ETag before calling
the job start API, and records the returned execution name. The coordinator
polls that execution until it succeeds or fails, verifies the worker's marker
and member Blobs, and then updates the target row. Detailed worker output lives
in Container Apps logs; the target row holds a concise failure.

The first live test should mark one small parse shard stale and run one import.
Check that one job execution starts, publishes under its attempt prefix, and
updates only that target and content-changed descendants. Do not enable a broad
rerun until this test passes.
