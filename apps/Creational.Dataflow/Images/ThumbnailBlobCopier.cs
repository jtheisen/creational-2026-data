using System.Net;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Creational.Dataflow.Images;

public sealed class WikimediaThrottledException(TimeSpan delay) : Exception("Wikimedia requested a slower pace.")
{
    public TimeSpan Delay { get; } = delay;
}

public sealed record ThumbnailCopyResult(long Bytes, string Sha256);

public interface IThumbnailBlobCopier
{
    Task<bool> IsCurrentAsync(ThumbnailRendition rendition, CancellationToken ct);
    Task<ThumbnailCopyResult?> RecoverAsync(ThumbnailRendition rendition, CancellationToken ct);
    Task<ThumbnailCopyResult> CopyAsync(string sourceUrl, ThumbnailRendition rendition, CancellationToken ct);
}

public sealed class ThumbnailBlobCopier(HttpClient http, BlobServiceClient service, WikimediaRequestPacer pacer) : IThumbnailBlobCopier
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly BlobContainerClient _container = service.GetBlobContainerClient("wikimedia-extracts");

    public async Task<bool> IsCurrentAsync(ThumbnailRendition rendition, CancellationToken ct)
    {
        if (rendition.Status != "complete" || rendition.Stale || rendition.Bytes is null || rendition.Sha256 is null)
            return false;
        var response = await _container.GetBlobClient(rendition.BlobName).ExistsAsync(ct);
        if (!response.Value) return false;
        var properties = await _container.GetBlobClient(rendition.BlobName).GetPropertiesAsync(cancellationToken: ct);
        return properties.Value.ContentLength == rendition.Bytes &&
            properties.Value.Metadata.TryGetValue("sha256", out var hash) &&
            string.Equals(hash, rendition.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ThumbnailCopyResult?> RecoverAsync(ThumbnailRendition rendition, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(rendition.BlobName);
        if (!(await blob.ExistsAsync(ct)).Value) return null;
        var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        if (properties.ContentLength <= 0 || !properties.Metadata.TryGetValue("sha256", out var hash) ||
            (rendition.Stale && string.Equals(hash, rendition.Sha256, StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!await ContentMatchesAsync(blob, hash, ct)) return null;
        return new ThumbnailCopyResult(properties.ContentLength, hash);
    }

    public async Task<ThumbnailCopyResult> CopyAsync(string sourceUrl, ThumbnailRendition rendition, CancellationToken ct)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url) || !Allowed(url))
            throw new InvalidOperationException("The image source is not an allowed Wikimedia URL.");

        using var response = await GetAllowedAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests &&
            response.Content.Headers.ContentLength is > 0 and <= 256 &&
            (await response.Content.ReadAsStringAsync(ct)).Contains(
                "Too many thumbnail requests for failing image", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Wikimedia reports too many thumbnail requests for a failing image.");
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new WikimediaThrottledException(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxBytes)
            throw new InvalidOperationException("Thumbnail exceeds the 16 MiB import limit.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new InvalidOperationException("Thumbnail exceeds the 16 MiB import limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        if (buffer.Length == 0) throw new InvalidOperationException("Wikimedia returned an empty thumbnail.");
        var hash = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))).ToLowerInvariant();
        var blob = _container.GetBlobClient(rendition.BlobName);
        var old = await blob.ExistsAsync(ct) ? (await blob.GetPropertiesAsync(cancellationToken: ct)).Value : null;
        if (old is not null && string.IsNullOrWhiteSpace(old.VersionId))
            throw new InvalidOperationException("Blob versioning is required before replacing an imported thumbnail.");

        buffer.Position = 0;
        await blob.UploadAsync(buffer, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream" },
            Metadata = new Dictionary<string, string> { ["sha256"] = hash }
        }, ct);
        var committed = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        if (committed.ContentLength != buffer.Length || !committed.Metadata.TryGetValue("sha256", out var recorded) || recorded != hash)
            throw new InvalidOperationException("The uploaded thumbnail did not verify.");
        if (!await ContentMatchesAsync(blob, hash, ct))
            throw new InvalidOperationException("The uploaded thumbnail content checksum did not verify.");
        if (old is not null)
            await blob.WithVersion(old.VersionId).DeleteIfExistsAsync(cancellationToken: ct);
        return new(buffer.Length, hash);
    }

    private static async Task<bool> ContentMatchesAsync(BlobClient blob, string hash, CancellationToken ct)
    {
        var downloaded = await blob.DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = downloaded.Value.Content;
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> GetAllowedAsync(Uri url, CancellationToken ct)
    {
        for (var redirect = 0; redirect < 4; redirect++)
        {
            await pacer.WaitAsync(ct);
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || !Uri.TryCreate(url, location, out var next) || !Allowed(next))
                throw new InvalidOperationException("Wikimedia thumbnail redirected to an unexpected host.");
            url = next;
        }
        throw new InvalidOperationException("Wikimedia thumbnail redirected too many times.");
    }

    private static bool Allowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("thumb.wikimedia.org", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("en.wikipedia.org", StringComparison.OrdinalIgnoreCase));
}
