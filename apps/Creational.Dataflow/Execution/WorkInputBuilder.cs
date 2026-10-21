using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs.Models;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Execution;

public interface IOutputManifestReader
{
    Task<OutputManifest> ReadAsync(string markerUri, CancellationToken cancellationToken);
}

public sealed class AzureOutputManifestReader(IBlobStore blobs) : IOutputManifestReader
{
    public async Task<OutputManifest> ReadAsync(string markerUri, CancellationToken cancellationToken)
    {
        await using var stream = await blobs.OpenReadAsync(markerUri, cancellationToken);
        return await JsonSerializer.DeserializeAsync<OutputManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
            ?? throw new InvalidDataException($"Empty output manifest {markerUri}");
    }
}

public sealed class WorkInputBuilder(IOutputManifestReader reader, IReadOnlyList<ManifestShard> shards,
    AzureBlobStore? blobs = null)
{
    private readonly Dictionary<string, ManifestShard> _shards = shards.ToDictionary(s => s.Key);
    private const string InputsPrefix = "https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/work-inputs/";

    public async Task<JsonObject> BuildPayloadAsync(TargetDefinition target,
        IReadOnlyDictionary<TargetId, TargetState> states, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["Target"] = JsonSerializer.SerializeToNode(target.Id),
            ["InputFingerprints"] = JsonSerializer.SerializeToNode(target.ContentDependencies.ToDictionary(
                id => id.ToString(), id => states[id].Fingerprint ?? throw new InvalidDataException($"Missing input fingerprint {id}")))
        };
        TargetId Id(string kind, string key = "all") => new(target.Id.Collection, kind, key);
        async Task<ArtifactDescriptor> Artifact(TargetId id, string name)
        {
            if (!states.TryGetValue(id, out var state) || state.ManifestPath is null)
                throw new InvalidDataException($"Missing manifest for {id}");
            var manifest = await reader.ReadAsync(state.ManifestPath, ct);
            return manifest.Artifacts.Single(a => a.Name == name);
        }

        switch (target.Id.Kind)
        {
            case "alias-index":
            case "candidate-pool-shard":
                {
                    var shard = _shards[target.Id.Key];
                    var dump = await Artifact(Id("dump-shard", target.Id.Key), shard.FileName);
                    payload["dump"] = new JsonObject
                    {
                        ["url"] = SourceManifestLoader.SourcePrefix + shard.FileName,
                        ["sha256"] = shard.Sha256, ["bytes"] = dump.Length
                    };
                    if (target.Id.Kind == "candidate-pool-shard")
                        payload["seed"] = (await Artifact(Id("alias-seed"), "aliases.json")).Path;
                    break;
                }
            case "alias-seed":
                payload["indexes"] = new JsonArray((await Task.WhenAll(_shards.Keys.Order().Select(async key =>
                    (JsonNode?)JsonValue.Create((await Artifact(Id("alias-index", key), "index.json")).Path)))).ToArray());
                break;
            case "candidate-pool-finalize":
                payload["seed"] = (await Artifact(Id("alias-seed"), "aliases.json")).Path;
                payload["pool_redirects"] = new JsonArray((await Task.WhenAll(_shards.Keys.Order().Select(async key =>
                    (JsonNode?)JsonValue.Create((await Artifact(Id("candidate-pool-shard", key), "redirects.json")).Path)))).ToArray());
                break;
            case "parse-shard":
                {
                    var pool = await Artifact(Id("candidate-pool-shard", target.Id.Key), "pool.parquet");
                    payload["pool"] = new JsonObject { ["url"] = pool.Path, ["sha256"] = pool.Sha256 };
                    payload["seed"] = (await Artifact(Id("alias-seed"), "aliases.json")).Path;
                    break;
                }
            case "parse-finalize":
                break;
            case "ott-names":
                payload["taxonomy"] = (await Artifact(Id("ott-source"), "taxonomy.tsv")).Path;
                break;
            case "ott-correlation":
                {
                    var parse = new JsonArray();
                    foreach (var key in _shards.Keys.Order())
                    {
                        var item = new JsonObject { ["key"] = key };
                        foreach (var name in new[] { "articles.parquet", "taxoboxes.parquet", "taxobox_parameters.parquet" })
                            item[name] = (await Artifact(Id("parse-shard", key), name)).Path;
                        parse.Add(item);
                    }
                    payload["parse_shards"] = parse;
                    payload["ott_names"] = (await Artifact(Id("ott-names"), "taxonomy_names.parquet")).Path;
                    break;
                }
            case "source-set":
                payload["pins"] = JsonSerializer.SerializeToNode(new
                {
                    articleManifest = (await Artifact(Id("source-manifest"), "SHA256SUMS")).Path,
                    imageDumps = new[] { ImageDumpPins.Enwiki, ImageDumpPins.Commons }
                });
                break;
            case "enwiki-image-metadata":
                payload["dump"] = (await Artifact(Id("enwiki-image-dump"), ImageDumpPins.Enwiki.FileName)).Path;
                payload["source_dump_sha1"] = ImageDumpPins.Enwiki.Sha1;
                break;
            case "commons-image-metadata":
                payload["dump"] = (await Artifact(Id("commons-image-dump"), ImageDumpPins.Commons.FileName)).Path;
                payload["source_dump_sha1"] = ImageDumpPins.Commons.Sha1;
                payload["image_references"] = await ImageReferencesAsync();
                break;
            case "resolved-image-metadata":
                payload["enwiki_metadata"] = (await Artifact(Id("enwiki-image-metadata"), "images.parquet")).Path;
                payload["commons_metadata"] = (await Artifact(Id("commons-image-metadata"), "images.parquet")).Path;
                payload["image_references"] = await ImageReferencesAsync();
                break;
            default:
                throw new InvalidOperationException($"{target.Id.Kind} is not a compute target");
        }
        return payload;

        async Task<JsonArray> ImageReferencesAsync() => new((await Task.WhenAll(_shards.Keys.Order().Select(async key =>
            (JsonNode?)JsonValue.Create((await Artifact(Id("parse-shard", key), "image_references.parquet")).Path)))).ToArray());
    }

    public async Task<string> PublishAsync(TargetDefinition target,
        IReadOnlyDictionary<TargetId, TargetState> states, string attemptId, CancellationToken ct)
    {
        if (blobs is null) throw new InvalidOperationException("Blob publishing is not configured");
        var payload = await BuildPayloadAsync(target, states, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var uri = InputsPrefix + Uri.EscapeDataString(attemptId) + ".json";
        await using var stream = new MemoryStream(bytes);
        await blobs.Client(uri).UploadAsync(stream, new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string>
            {
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        }, ct);
        return uri;
    }
}
