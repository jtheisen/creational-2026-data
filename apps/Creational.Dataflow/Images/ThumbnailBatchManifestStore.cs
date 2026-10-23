using System.Text.Json;
using Azure.Storage.Blobs;

namespace Creational.Dataflow.Images;

public interface IThumbnailBatchManifestStore
{
    Task<string> SaveAsync(ThumbnailBatchManifest manifest, CancellationToken ct);
    Task<ThumbnailBatchManifest> ReadAsync(string path, CancellationToken ct);
}

public sealed class AzureThumbnailBatchManifestStore(BlobServiceClient service) : IThumbnailBatchManifestStore
{
    private readonly BlobContainerClient _container = service.GetBlobContainerClient("wikimedia-extracts");

    public async Task<string> SaveAsync(ThumbnailBatchManifest manifest, CancellationToken ct)
    {
        var path = $"image-jobs/batches/{manifest.BatchId}/manifest.json";
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest));
        await _container.GetBlobClient(path).UploadAsync(stream, overwrite: false, cancellationToken: ct);
        return path;
    }

    public async Task<ThumbnailBatchManifest> ReadAsync(string path, CancellationToken ct)
    {
        var result = await _container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct);
        await using var content = result.Value.Content;
        return await JsonSerializer.DeserializeAsync<ThumbnailBatchManifest>(content, cancellationToken: ct)
            ?? throw new InvalidDataException("Image batch manifest is empty.");
    }
}
