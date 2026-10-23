using System.Security.Cryptography;
using System.Text;

namespace Creational.Dataflow.Images;

public sealed record ThumbnailImportJob(string Id, int Width, string[] Titles, string Status,
    DateTimeOffset CreatedAt, string? Error = null)
{
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

public sealed record ThumbnailRendition(string RowKey, string Title, int Width, string BlobName,
    string Status, long? Bytes = null, string? Sha256 = null, string? SourceUrl = null,
    string? Error = null, bool Stale = false);

public sealed record ThumbnailAsset(string RowKey, string Title, string Curation);

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
