using Azure.Storage.Blobs.Models;
using Azure;
using Creational.Dataflow.Storage;

namespace Creational.Dataflow.Transfers;

public sealed class AzureBlockBlobWriter(AzureBlobStore blobs) : IBlockBlobWriter
{
    public async Task StageAsync(string destination, string blockId, byte[] bytes, int count, CancellationToken ct)
    {
        await using var stream = new MemoryStream(bytes, 0, count, writable: false);
        await blobs.BlockClient(destination).StageBlockAsync(blockId, stream, cancellationToken: ct);
    }

    public async Task CommitAsync(string destination, IReadOnlyList<string> blockIds, long length, string sha256,
        string? expectedCurrentETag, CancellationToken ct)
    {
        await blobs.BlockClient(destination).CommitBlockListAsync(blockIds,
            new CommitBlockListOptions
            {
                Metadata = new Dictionary<string, string> { ["sha256"] = sha256 },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
                Conditions = expectedCurrentETag is null
                    ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                    : new BlobRequestConditions { IfMatch = new ETag(expectedCurrentETag) }
            }, ct);
    }

    public async Task<bool> VerifyAsync(string destination, long length, string sha256, CancellationToken ct)
    {
        var info = await blobs.HeadAsync(destination, ct);
        return info?.Length == length && string.Equals(info.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DeleteVersionAsync(string destination, string versionId, CancellationToken ct)
    {
        await blobs.Client(destination).WithVersion(versionId).DeleteIfExistsAsync(cancellationToken: ct);
    }
}
