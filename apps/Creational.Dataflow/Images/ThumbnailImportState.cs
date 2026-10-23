using System.Security.Cryptography;
using System.Text;

namespace Creational.Dataflow.Images;

public sealed record ThumbnailImportJob(string Id, int Width, string[] Titles, string Status,
    DateTimeOffset CreatedAt, string? Error = null)
{
    public string? BatchId { get; init; }
    public int? StartOffset { get; init; }

    public static ThumbnailImportJob Create(IReadOnlyList<string> titles, int width)
    {
        if (width is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(width));
        var distinct = titles.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length is < 1 or > 24 || distinct.Length != titles.Count)
            throw new ArgumentException("Select between 1 and 24 distinct image titles.", nameof(titles));
        return new(Guid.NewGuid().ToString("N"), width, distinct, "pending", DateTimeOffset.UtcNow);
    }
}

public sealed record ThumbnailImportBatch(string Id, ImageSelection Selection, int StartOffset,
    int RequestedCount, int NextOffset, string Status, DateTimeOffset CreatedAt,
    int FailedJobs = 0, string? Error = null)
{
    public string? ManifestPath { get; init; }
    public int Width { get; init; } = 250;
    public int CopiedImages { get; init; }
    public int SkippedImages { get; init; }
    public int FailedImages { get; init; }
    public int EndOffset => checked(StartOffset + RequestedCount);

    public string ChildId(int offset) => $"{Id}-{offset:D8}";

    public static ThumbnailImportBatch Create(ImageSelection selection, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        _ = checked(offset + count);
        return new(Guid.NewGuid().ToString("N"), selection, offset, count, offset,
            "pending", DateTimeOffset.UtcNow);
    }
}

public sealed record ThumbnailBatchManifest(string BatchId, ImageCandidate[] Images);

public sealed record ThumbnailBatchItem(string BatchId, int Offset, string Title, string Status,
    long? Bytes = null, string? Error = null)
{
    public string RowKey => $"{BatchId}-{Offset:D8}";
}

public sealed record ThumbnailRendition(string RowKey, string Title, int Width, string BlobName,
    string Status, long? Bytes = null, string? Sha256 = null, string? SourceUrl = null,
    string? Error = null, bool Stale = false);

public sealed record ThumbnailAsset(string RowKey, string Title, string Curation);

public sealed record StoredImageInfo(string Title, string SourceWiki, string Status, string RawPath,
    DateTimeOffset FetchedAt, string? Sha1, DateTimeOffset? UploadedAt,
    long? OriginalBytes, string? License, string? LicenseUrl, string? Attribution,
    string? Error, bool Stale = false, string? ResolvedTitle = null)
{
    public ImageMetadata ToDisplay(int width) => new(Title,
        Status == "complete" ? WikimediaImageInfo.ThumbnailRedirect(SourceWiki, ResolvedTitle ?? Title, width) : null,
        Status == "complete" ? $"https://{(SourceWiki == "enwiki" ? "en.wikipedia.org" : "commons.wikimedia.org")}/wiki/{Uri.EscapeDataString((ResolvedTitle ?? Title).Replace(' ', '_'))}" : null,
        null, OriginalBytes, License, LicenseUrl, Attribution,
        string.IsNullOrWhiteSpace(License) || string.IsNullOrWhiteSpace(LicenseUrl), Error);
}

public sealed record ThumbnailIdentity(string RowKey, string BlobName)
{
    public static ThumbnailIdentity For(string title, int width)
    {
        if (!title.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || title.Length <= 5)
            throw new ArgumentException("Expected a Wikimedia File title.", nameof(title));
        if (width is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(width));
        var filename = title[5..].Replace('_', ' ');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filename.Normalize(NormalizationForm.FormC)))).ToLowerInvariant();
        return new($"{hash}-{width}", $"thumbnails/{width}px/{hash[..2]}/{hash}/{filename}");
    }
}
