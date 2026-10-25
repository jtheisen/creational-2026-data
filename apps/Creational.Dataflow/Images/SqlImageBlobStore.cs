using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Creational.Dataflow.Images;

public sealed record SqlResizeManifest(string BatchId, IReadOnlyList<SqlResizeManifestItem> Items);
public sealed record SqlResizeManifestItem(int ImageId, long SourceBytes, string SourceSha256);
public sealed record SqlResizeResult(string BatchId, IReadOnlyList<SqlResizeResultItem> Items);
public sealed record SqlResizeResultItem(int ImageId, string SourceSha256,
    IReadOnlyList<SqlResizeShapeResult> Shapes);
public sealed record SqlResizeShapeResult(int Width, string Status, int? Bytes, string? Sha256,
    string? Error);

public sealed class SqlImageBlobStore(BlobServiceClient service, TokenCredential credential)
{
    private readonly BlobContainerClient _legacy = service.GetBlobContainerClient("wikimedia-extracts");
    private readonly BlobContainerClient _images = service.GetBlobContainerClient("images");
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AccessToken _sourceToken;

    public static string RenditionPath(int imageId, int width) => $"creatures/w{width}/{imageId}";
    public static string MetadataPath(int imageId) => $"metadata/{imageId}.json";

    public BlobClient LegacyClient(string path) => _legacy.GetBlobClient(path);

    public async Task<string> WriteResizeManifestAsync(Guid batchId, IReadOnlyList<ImageWorkClaim> claims,
        CancellationToken ct)
    {
        var items = new List<SqlResizeManifestItem>(claims.Count);
        foreach (var claim in claims)
        {
            var blob = _images.GetBlobClient(RenditionPath(claim.ImageId, 250));
            var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            if (properties.ContentLength is < 1 or > 16 * 1024 * 1024 ||
                !properties.Metadata.TryGetValue("sha256", out var hash) || hash.Length != 64)
                throw new InvalidDataException($"Source Blob for image {claim.ImageId} is not verified.");
            items.Add(new(claim.ImageId, properties.ContentLength, hash));
        }
        var path = $"sql-resize/manifests/{batchId:N}.json";
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
            new SqlResizeManifest(batchId.ToString("N"), items)));
        await _legacy.GetBlobClient(path).UploadAsync(stream, overwrite: false, cancellationToken: ct);
        return path;
    }

    public async Task<SqlResizeResult?> ReadResizeResultAsync(Guid batchId, CancellationToken ct)
    {
        var blob = _legacy.GetBlobClient($"sql-resize/results/{batchId:N}.json");
        if (!(await blob.ExistsAsync(ct)).Value) return null;
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        var result = await JsonSerializer.DeserializeAsync<SqlResizeResult>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Resize result marker is empty.");
        if (!string.Equals(result.BatchId, batchId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resize result marker has a different batch ID.");
        return result;
    }

    public async Task<SqlResizeManifest> ReadResizeManifestAsync(string path, CancellationToken ct)
    {
        var response = await _legacy.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        return await JsonSerializer.DeserializeAsync<SqlResizeManifest>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Resize manifest is empty.");
    }

    public async Task<bool> VerifyDerivedAsync(int imageId, int width, int expectedBytes,
        string expectedHash, string sourceHash, string recipe, CancellationToken ct)
    {
        var blob = _images.GetBlobClient(RenditionPath(imageId, width));
        if (!await VerifyAsync(blob, expectedBytes, expectedHash, ct)) return false;
        var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        return properties.ContentType == "image/webp" &&
            properties.Metadata.TryGetValue("source_sha256", out var source) && source == sourceHash &&
            properties.Metadata.TryGetValue("recipe", out var actualRecipe) && actualRecipe == recipe;
    }

    public Task<bool> VerifySourceAsync(int imageId, long expectedBytes, string expectedHash,
        CancellationToken ct) => VerifyAsync(RenditionPath(imageId, 250), expectedBytes, expectedHash, ct);

    public async Task SaveMetadataAsync(int imageId, ImageInfoSnapshot snapshot, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? leaseIsCurrent = null)
    {
        var blob = _images.GetBlobClient(MetadataPath(imageId));
        var old = await blob.ExistsAsync(ct) ? (await blob.GetPropertiesAsync(cancellationToken: ct)).Value : null;
        if (old is not null && string.IsNullOrWhiteSpace(old.VersionId))
            throw new InvalidOperationException("Blob versioning is required to replace imageinfo metadata.");
        if (leaseIsCurrent is not null && !await leaseIsCurrent(ct))
            throw new InvalidOperationException("Metadata lease expired before publishing.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        using var stream = new MemoryStream(bytes);
        var attemptId = Guid.NewGuid().ToString("N");
        try
        {
            var written = (await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Metadata = new Dictionary<string, string> { ["write_attempt"] = attemptId },
                Conditions = old is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } :
                    new BlobRequestConditions { IfMatch = old.ETag }
            }, ct)).Value;
            var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            if (properties.ETag != written.ETag || properties.ContentLength != bytes.Length)
                throw new InvalidDataException("Imageinfo snapshot did not verify.");
            if (leaseIsCurrent is not null && !await leaseIsCurrent(ct))
                throw new InvalidOperationException("Metadata lease expired before verification finished.");
        }
        catch
        {
            await RestoreOwnedAsync(blob, old, attemptId, CancellationToken.None);
            throw;
        }
        if (old is not null && !string.IsNullOrWhiteSpace(old.VersionId))
            await DeletePreviousVersionAsync(blob, old.VersionId, ct);
    }

    public async Task<ImageInfoSnapshot> LoadMetadataAsync(int imageId, CancellationToken ct)
    {
        var blob = _images.GetBlobClient(MetadataPath(imageId));
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.TryGetProperty("Metadata", out _))
            return root.Deserialize<ImageInfoSnapshot>() ?? throw new InvalidDataException("Imageinfo snapshot is empty.");
        var page = root.GetProperty("Page");
        var title = page.GetProperty("title").GetString() ?? throw new InvalidDataException("Imageinfo title missing.");
        var sourceWiki = root.GetProperty("SourceWiki").GetString()!;
        var status = root.GetProperty("Status").GetString()!;
        return new(root.GetProperty("RequestedTitle").GetString()!, sourceWiki, page.GetRawText(),
            new ImageMetadata(title, status == "complete"
                ? WikimediaImageInfo.ThumbnailRedirect(sourceWiki, title, 250) : null,
                null, null, null, null, null, null, true, null), null, null, status);
    }

    public async Task EnsureContainerAsync(CancellationToken ct) =>
        await _images.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);

    public async Task<bool> CopyLegacyAsync(string sourcePath, string destinationPath, long expectedBytes,
        string? expectedSha256, CancellationToken ct)
    {
        var source = _legacy.GetBlobClient(sourcePath);
        var destination = _images.GetBlobClient(destinationPath);
        var sourceProperties = (await source.GetPropertiesAsync(cancellationToken: ct)).Value;
        if (sourceProperties.ContentLength != expectedBytes)
            throw new InvalidDataException($"Legacy Blob size differs: {sourcePath}");
        if (expectedSha256 is null)
        {
            var response = await source.DownloadStreamingAsync(cancellationToken: ct);
            await using var content = response.Value.Content;
            expectedSha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, ct));
        }
        if (await VerifyAsync(destination, expectedBytes, expectedSha256, ct)) return true;
        var old = await destination.ExistsAsync(ct)
            ? (await destination.GetPropertiesAsync(cancellationToken: ct)).Value : null;
        if (old is not null && string.IsNullOrWhiteSpace(old.VersionId))
            throw new InvalidOperationException("Blob versioning is required to replace an adopted image.");
        if (expectedSha256 is not null && sourceProperties.Metadata.TryGetValue("sha256", out var sourceHash) &&
            !sourceHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Legacy Blob checksum metadata differs: {sourcePath}");
        var metadata = new Dictionary<string, string>(sourceProperties.Metadata, StringComparer.OrdinalIgnoreCase);
        if (expectedSha256 is not null) metadata["sha256"] = expectedSha256;
        var attemptId = Guid.NewGuid().ToString("N");
        metadata["write_attempt"] = attemptId;
        var token = await SourceTokenAsync(ct);
        var options = new BlobSyncUploadFromUriOptions
        {
            SourceAuthentication = new HttpAuthorization("Bearer", token.Token),
            SourceConditions = new BlobRequestConditions { IfMatch = sourceProperties.ETag },
            HttpHeaders = new BlobHttpHeaders { ContentType = sourceProperties.ContentType },
            Metadata = metadata,
            DestinationConditions = old is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } :
                new BlobRequestConditions { IfMatch = old.ETag }
        };
        try
        {
            var written = (await _images.GetBlockBlobClient(destinationPath)
                .SyncUploadFromUriAsync(source.Uri, options, ct)).Value;
            var properties = (await destination.GetPropertiesAsync(cancellationToken: ct)).Value;
            if (properties.ETag != written.ETag ||
                !await VerifyAsync(destination, expectedBytes, expectedSha256, ct))
                throw new InvalidDataException($"Copied Blob failed verification: {destinationPath}");
        }
        catch
        {
            await RestoreOwnedAsync(destination, old, attemptId, CancellationToken.None);
            throw;
        }
        if (old is not null)
            await DeletePreviousVersionAsync(destination, old.VersionId, ct);
        return false;
    }

    public static async Task DeletePreviousVersionAsync(BlobClient blob, string versionId, CancellationToken ct)
    {
        try
        {
            await blob.WithVersion(versionId).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch (RequestFailedException error) when (error.ErrorCode == "OperationNotAllowedOnAutomaticSnapshot")
        {
            // Azure retains this automatic snapshot under the account's retention policy.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The new version is already verified. Lifecycle cleanup must not change publication success.
            Console.Error.WriteLine($"Previous Blob version cleanup deferred for {blob.Name}: {error.Message}");
        }
    }

    public static async Task RestorePreviousAsync(BlobClient blob, BlobProperties? previous,
        ETag writtenEtag, CancellationToken ct)
    {
        try
        {
            if (previous is null)
            {
                await blob.DeleteIfExistsAsync(conditions: new BlobRequestConditions
                { IfMatch = writtenEtag }, cancellationToken: ct);
                return;
            }
            if (string.IsNullOrWhiteSpace(previous.VersionId))
                throw new InvalidOperationException("Previous Blob version is missing.");
            var prior = blob.WithVersion(previous.VersionId);
            var download = await prior.DownloadStreamingAsync(cancellationToken: ct);
            await using var contents = download.Value.Content;
            await blob.UploadAsync(contents, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = writtenEtag },
                HttpHeaders = new BlobHttpHeaders { ContentType = previous.ContentType },
                Metadata = new Dictionary<string, string>(previous.Metadata)
            }, ct);
        }
        catch (RequestFailedException error) when (error.Status == 412)
        {
            // Another writer replaced this version; never restore over its result.
        }
    }

    public static async Task RestoreOwnedAsync(BlobClient blob, BlobProperties? previous,
        string attemptId, CancellationToken ct)
    {
        BlobProperties current;
        try { current = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value; }
        catch (RequestFailedException error) when (error.Status == 404) { return; }
        if (current.Metadata.TryGetValue("write_attempt", out var owner) && owner == attemptId)
            await RestorePreviousAsync(blob, previous, current.ETag, ct);
    }

    private async Task<AccessToken> SourceTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_sourceToken.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
                _sourceToken = await credential.GetTokenAsync(
                    new TokenRequestContext(["https://storage.azure.com/.default"]), ct);
            return _sourceToken;
        }
        finally { _tokenGate.Release(); }
    }

    public async Task<bool> VerifyAsync(string path, long expectedBytes, string? expectedSha256, CancellationToken ct) =>
        await VerifyAsync(_images.GetBlobClient(path), expectedBytes, expectedSha256, ct);

    private static async Task<bool> VerifyAsync(BlobClient blob, long expectedBytes, string? expectedSha256,
        CancellationToken ct)
    {
        if (!(await blob.ExistsAsync(ct)).Value) return false;
        var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        if (properties.ContentLength != expectedBytes) return false;
        if (expectedSha256 is null) return true;
        if (!properties.Metadata.TryGetValue("sha256", out var stored) ||
            !stored.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return false;
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
