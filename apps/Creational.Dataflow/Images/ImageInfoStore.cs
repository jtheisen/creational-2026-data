using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;

namespace Creational.Dataflow.Images;

public interface IImageInfoStore
{
    Task<IReadOnlyDictionary<string, StoredImageInfo>> EnsureAsync(
        IReadOnlyList<ImageCandidate> candidates, CancellationToken ct);
}

public interface IImageInfoSnapshotStore
{
    Task<string> SaveAsync(ImageInfoSnapshot snapshot, CancellationToken ct);
}

public sealed class AzureImageInfoSnapshotStore(BlobServiceClient service) : IImageInfoSnapshotStore
{
    public async Task<string> SaveAsync(ImageInfoSnapshot snapshot, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.RequestedTitle)))
            .ToLowerInvariant();
        var path = $"image-jobs/imageinfo/{hash[..2]}/{hash}/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json";
        using var page = JsonDocument.Parse(snapshot.RawJson);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.RequestedTitle, snapshot.SourceWiki, snapshot.Status,
            FetchedAt = DateTimeOffset.UtcNow,
            RequestProperties = "canonicaltitle|timestamp|sha1|size|mime|mediatype|extmetadata",
            Page = page.RootElement
        });
        using var stream = new MemoryStream(bytes);
        await service.GetBlobContainerClient("wikimedia-extracts").GetBlobClient(path)
            .UploadAsync(stream, overwrite: false, cancellationToken: ct);
        return path;
    }
}

public sealed class ImageInfoStore(IThumbnailImportRepository repository, WikimediaImageInfo api,
    IImageInfoSnapshotStore snapshots) : IImageInfoStore
{
    public async Task<IReadOnlyDictionary<string, StoredImageInfo>> EnsureAsync(
        IReadOnlyList<ImageCandidate> candidates, CancellationToken ct)
    {
        var result = new Dictionary<string, StoredImageInfo>(StringComparer.Ordinal);
        var missing = new List<ImageCandidate>();
        foreach (var candidate in candidates.DistinctBy(x => x.FileTitle, StringComparer.Ordinal))
        {
            var stored = await repository.GetImageInfoAsync(candidate.FileTitle, ct);
            if (stored is { Stale: false } &&
                (candidate.SourceWiki is null || candidate.SourceWiki == stored.SourceWiki))
                result[candidate.FileTitle] = stored;
            else missing.Add(candidate);
        }
        foreach (var group in missing.GroupBy(x => x.SourceWiki ?? "enwiki", StringComparer.Ordinal))
        {
            var fetched = await api.FetchSnapshotsAsync(group.Select(x => x.FileTitle).ToArray(), group.Key, ct);
            if (group.Key == "enwiki")
            {
                var unknown = group.Where(x => x.SourceWiki is null && fetched[x.FileTitle].Status == "missing")
                    .Select(x => x.FileTitle).ToArray();
                if (unknown.Length > 0)
                {
                    var commons = await api.FetchSnapshotsAsync(unknown, "commonswiki", ct);
                    foreach (var title in unknown)
                        if (commons[title].Status == "complete")
                            fetched = new Dictionary<string, ImageInfoSnapshot>(fetched, StringComparer.Ordinal)
                            { [title] = commons[title] };
                }
            }
            foreach (var candidate in group)
            {
                var snapshot = fetched[candidate.FileTitle];
                var path = await snapshots.SaveAsync(snapshot, ct);
                var data = snapshot.Metadata;
                var stored = new StoredImageInfo(candidate.FileTitle, snapshot.SourceWiki,
                    snapshot.Status, path, DateTimeOffset.UtcNow, snapshot.Sha1, snapshot.UploadedAt,
                    data.OriginalBytes, data.License, data.LicenseUrl, data.Attribution, data.Error,
                    ResolvedTitle: data.FileTitle);
                await repository.SaveImageInfoAsync(stored, ct);
                result[candidate.FileTitle] = stored;
            }
        }
        return result;
    }
}
