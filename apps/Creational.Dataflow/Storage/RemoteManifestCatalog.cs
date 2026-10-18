using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Storage;

public sealed class RemoteManifestCatalog(AzureBlobStore blobs, IReadOnlyList<ManifestShard> shards) : IManifestCatalog
{
    private const string Imports = "https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/";
    private const string Extracts = "https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/";
    private readonly Dictionary<string, ManifestShard> _shards = shards.ToDictionary(s => s.Key);

    public static string NormalizedMarkerPath(TargetId id) =>
        $"{Extracts}target-manifests/adopted/{Uri.EscapeDataString(id.Kind)}/{Uri.EscapeDataString(id.Key)}.json";

    public async Task<CatalogEntry?> LoadAsync(TargetDefinition definition, CancellationToken cancellationToken)
    {
        var id = definition.Id;
        var direct = DirectMarker(id);
        if (direct is not null)
        {
            var loaded = await ReadManifestAsync(direct, cancellationToken);
            return loaded is null ? null : new(direct, loaded);
        }

        var normalized = NormalizedMarkerPath(id);
        var saved = await ReadManifestAsync(normalized, cancellationToken);
        if (saved is not null) return new(normalized, saved);
        var artifacts = new List<ArtifactDescriptor>();
        switch (id.Kind)
        {
            case "source-manifest":
                artifacts.Add(await DescriptorAsync("SHA256SUMS", Imports + "SHA256SUMS", null, cancellationToken));
                artifacts.Add(await DescriptorAsync("_COMPLETE.json", Imports + "_COMPLETE.json", null, cancellationToken));
                break;
            case "dump-shard":
                {
                    var shard = _shards[id.Key];
                    artifacts.Add(await DescriptorAsync(shard.FileName, Imports + shard.FileName, shard.Sha256, cancellationToken));
                    break;
                }
            case "alias-index":
                {
                    var shard = _shards[id.Key];
                    var url = Extracts + "extract-v2/_indexes/" + shard.FileName + ".json";
                    artifacts.Add(await DescriptorAsync("index.json", url, null, cancellationToken));
                    break;
                }
            case "alias-seed":
                artifacts.Add(await DescriptorAsync("aliases.json", Extracts + "extract-v2/aliases.json", null, cancellationToken));
                break;
            case "candidate-pool-shard":
                {
                    var shard = _shards[id.Key];
                    var prefix = Extracts + "candidate-pool-v1/" + shard.FileName.Replace(".xml.bz2", "") + "/";
                    var marker = await ReadJsonAsync(prefix + "_COMPLETE.json", cancellationToken)
                        ?? throw new InvalidOperationException($"Missing pool marker for {id.Key}");
                    if (marker.RootElement.GetProperty("source_sha256").GetString() != shard.Sha256)
                        throw new InvalidOperationException($"Pool source checksum differs for {id.Key}");
                    foreach (var member in marker.RootElement.GetProperty("artifacts").EnumerateObject())
                        artifacts.Add(await DescriptorAsync(member.Name, prefix + member.Name,
                            member.Value.GetProperty("sha256").GetString(), cancellationToken));
                    break;
                }
            case "candidate-pool-finalize":
                artifacts.Add(await DescriptorAsync("aliases-discovered.json", Extracts + "candidate-pool-v1/aliases-discovered.json", null, cancellationToken));
                artifacts.Add(await DescriptorAsync("_COMPLETE.json", Extracts + "candidate-pool-v1/_COMPLETE.json", null, cancellationToken));
                break;
            case "parse-finalize":
                // The marker records child fingerprints; it has no extra data artifact.
                break;
            default:
                throw new InvalidOperationException($"No legacy catalog mapping for {id.Kind}");
        }
        return new(normalized, new(id, definition.RecipeVersion, artifacts, new Dictionary<string, string>()), true);
    }

    public async Task PublishAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.NeedsPublication) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry.Manifest);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        try
        {
            await using var stream = new MemoryStream(bytes);
            await blobs.Client(entry.MarkerPath).UploadAsync(stream, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = new Dictionary<string, string> { ["sha256"] = hash },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var existing = await ReadManifestAsync(entry.MarkerPath, cancellationToken);
            if (existing is null || JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(entry.Manifest))
                throw new InvalidOperationException($"Different normalized marker already exists at {entry.MarkerPath}", exception);
        }
    }

    private static string? DirectMarker(TargetId id) => id.Kind switch
    {
        "parse-shard" => Extracts + "candidate-parse-v1/enwiki-2026-10-01-" + id.Key + "/_COMPLETE.json",
        "ott-source" => "https://creational.blob.core.windows.net/wikimedia-imports/opentree/ott3.3/_COMPLETE.json",
        "ott-names" => Extracts + "ott3.3/_COMPLETE.json",
        "ott-correlation" => Extracts + "ott-wikipedia-correlation-v3/_COMPLETE.json",
        _ => null
    };

    private async Task<OutputManifest?> ReadManifestAsync(string url, CancellationToken ct)
    {
        var json = await ReadJsonAsync(url, ct);
        return json is null ? null : json.RootElement.Deserialize<OutputManifest>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private async Task<JsonDocument?> ReadJsonAsync(string url, CancellationToken ct)
    {
        if (await blobs.HeadAsync(url, ct) is null) return null;
        await using var stream = await blobs.OpenReadAsync(url, ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<ArtifactDescriptor> DescriptorAsync(string name, string url, string? knownSha256, CancellationToken ct)
    {
        var info = await blobs.HeadAsync(url, ct) ?? throw new InvalidOperationException($"Missing legacy artifact {url}");
        var hash = knownSha256 ?? info.Sha256;
        if (hash is null)
        {
            await using var stream = await blobs.OpenReadAsync(url, ct);
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }
        var format = name.EndsWith(".parquet", StringComparison.Ordinal) ? "parquet" :
            name.EndsWith(".json", StringComparison.Ordinal) ? "json" :
            name.EndsWith(".bz2", StringComparison.Ordinal) ? "bzip2" : "text";
        return new(name, url, info.Length, hash, format);
    }
}
