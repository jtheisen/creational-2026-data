using System.Text.RegularExpressions;
using Creational.Dataflow.Storage;

namespace Creational.Dataflow.Targets;

public sealed class SourceManifestLoader(IBlobStore blobs)
{
    public const string SourcePrefix = "https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/";
    private static readonly Regex Line = new(
        "^(?<sha>[0-9a-f]{64})\\s+(?<name>enwiki-2026-10-01-(?<key>[a-zA-Z0-9]+)\\.xml\\.bz2)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<ManifestShard>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var stream = await blobs.OpenReadAsync(SourcePrefix + "SHA256SUMS", cancellationToken);
        using var reader = new StreamReader(stream);
        var result = new List<ManifestShard>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var match = Line.Match(line);
            if (!match.Success) throw new InvalidDataException($"Invalid shard manifest line: {line}");
            var name = match.Groups["name"].Value;
            var info = await blobs.HeadAsync(SourcePrefix + name, cancellationToken);
            // A missing shard must remain in the graph so Import can restore it.
            result.Add(new(match.Groups["key"].Value, name, info?.Length ?? 0, match.Groups["sha"].Value));
        }
        if (result.Count == 0 || result.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("Shard manifest is empty or has duplicate keys");
        return result;
    }
}
