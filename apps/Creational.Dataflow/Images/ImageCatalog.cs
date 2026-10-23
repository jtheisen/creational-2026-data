using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Creational.Dataflow.Execution;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Images;

public interface IImageCatalog
{
    Task<ImageCatalogPage> QueryAsync(ImageSelection selection, int offset, int limit, CancellationToken cancellationToken);
    Task<int> SnapshotAsync(ImageSelection selection, int chunkSize,
        Func<IReadOnlyList<ImageCandidate>, CancellationToken, Task> sink, CancellationToken cancellationToken);
}

public sealed class ImageCatalog(ITargetRepository targets, IOutputManifestReader manifests,
    IBlobStore blobs, IReadOnlyList<ManifestShard> shards) : IImageCatalog
{
    private const string EdgesMarker = "https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/image-preview-catalog-v1/ott3.3/_COMPLETE.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _version;
    private string? _root;

    public async Task<ImageCatalogPage> QueryAsync(ImageSelection selection, int offset, int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await EnsureLocalAsync(cancellationToken);
            return await Task.Run(() => ImageCatalogQuery.Query(root, selection, offset, limit), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> SnapshotAsync(ImageSelection selection, int chunkSize,
        Func<IReadOnlyList<ImageCandidate>, CancellationToken, Task> sink, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await EnsureLocalAsync(cancellationToken);
            return await Task.Run(() => ImageCatalogQuery.StreamAsync(root, selection, chunkSize, sink,
                cancellationToken), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> EnsureLocalAsync(CancellationToken ct)
    {
        var states = await targets.GetCollectionAsync(October2026Collection.Collection, ct);
        var needed = new List<(TargetId Id, string Name, string Relative)>();
        var ott = new TargetId(October2026Collection.Collection, "ott-source", "all");
        var correlation = new TargetId(October2026Collection.Collection, "ott-correlation", "all");
        needed.Add((ott, "edges.parquet", "edges.parquet"));
        needed.Add((correlation, "exact_matches.parquet", "matches.parquet"));
        foreach (var shard in shards)
        {
            var id = new TargetId(October2026Collection.Collection, "parse-shard", shard.Key);
            needed.Add((id, "articles.parquet", $"parse/{shard.Key}/articles.parquet"));
            needed.Add((id, "image_references.parquet", $"parse/{shard.Key}/image_references.parquet"));
        }
        var metadataId = new TargetId(October2026Collection.Collection, "resolved-image-metadata", "all");
        if (states.TryGetValue(metadataId, out var metadataState) && metadataState.ManifestPath is not null &&
            metadataState.Fingerprint is not null && metadataState.Outcome == "complete" && !metadataState.IsStale)
            needed.Add((metadataId, "resolved_images.parquet", "resolved_images.parquet"));

        var requiredStates = needed.Select(x => x.Id).Distinct().Select(id =>
            states.TryGetValue(id, out var state) && state.ManifestPath is not null && state.Fingerprint is not null
                ? state : throw new InvalidOperationException($"Image preview needs a completed {id} target"))
            .ToArray();
        var signature = string.Join('\n', requiredStates.Select(s => $"{s.Id}:{s.Fingerprint}:{s.ManifestPath}"));
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        if (version == _version && _root is not null) return _root;

        var root = Path.Combine(Path.GetTempPath(), "creational-image-catalog", version);
        Directory.CreateDirectory(root);
        var manifestMap = new Dictionary<TargetId, OutputManifest>();
        foreach (var state in requiredStates)
            manifestMap[state.Id] = await manifests.ReadAsync(state.ManifestPath!, ct);
        await using var markerStream = await blobs.OpenReadAsync(EdgesMarker, ct);
        var edgeMarker = await JsonSerializer.DeserializeAsync<EdgeCatalogMarker>(markerStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
            ?? throw new InvalidDataException("Missing OTT hierarchy marker");
        var sourceHash = manifestMap[ott].Artifacts.Single(a => a.Name == "taxonomy.tsv").Sha256;
        if (!edgeMarker.SourceSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OTT hierarchy artifact is stale; rebuild it from the current taxonomy.tsv");
        foreach (var item in needed)
        {
            var artifact = item.Name == "edges.parquet"
                ? new ArtifactDescriptor(item.Name, edgeMarker.Path, edgeMarker.Length, edgeMarker.Sha256, "parquet")
                : manifestMap[item.Id].Artifacts.Single(a => a.Name == item.Name);
            var destination = Path.Combine(root, item.Relative);
            if (await MatchesAsync(destination, artifact, ct)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using var source = await blobs.OpenReadAsync(artifact.Path, ct);
                await using var output = File.Create(partial);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[4 * 1024 * 1024];
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    digest.AppendData(buffer, 0, read);
                    length += read;
                }
                await output.FlushAsync(ct);
                if (length != artifact.Length ||
                    !Convert.ToHexString(digest.GetHashAndReset()).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Image catalog artifact failed verification: {artifact.Name}");
                File.Move(partial, destination, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        var oldRoot = _root;
        _version = version;
        _root = root;
        if (oldRoot is not null && oldRoot != root && Directory.Exists(oldRoot))
            Directory.Delete(oldRoot, recursive: true);
        return root;
    }

    private static async Task<bool> MatchesAsync(string path, ArtifactDescriptor artifact, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Length) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record EdgeCatalogMarker(string SourceSha256, string Path, long Length, string Sha256);
}
