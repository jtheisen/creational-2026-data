using System.Text.Json.Nodes;
using Creational.Dataflow.Execution;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class WorkInputBuilderTests
{
    [Fact]
    public async Task PoolShardReceivesOnlyItsDumpAndTheSeed()
    {
        var shards = new[] { new ManifestShard("p1", "enwiki-2026-10-01-p1.xml.bz2", 100, new string('a', 64)) };
        var graph = October2026Collection.Build(shards);
        var target = graph.Single(d => d.Id.Kind == "candidate-pool-shard");
        var seed = graph.Single(d => d.Id.Kind == "alias-seed").Id;
        var dump = graph.Single(d => d.Id.Kind == "dump-shard").Id;
        var states = new Dictionary<TargetId, TargetState>
        {
            [seed] = TargetState.Empty(seed) with { ManifestPath = "seed-marker", Fingerprint = "seed-fingerprint" },
            [dump] = TargetState.Empty(dump) with { ManifestPath = "dump-marker", Fingerprint = "dump-fingerprint" }
        };
        var seedManifest = new OutputManifest(seed, "v1",
            [new("aliases.json", "https://example.test/aliases.json", 1, new string('b', 64), "json")],
            new Dictionary<string, string>());
        var dumpManifest = new OutputManifest(dump, "v1",
            [new(shards[0].FileName, "https://example.test/dump", 123, shards[0].Sha256, "bzip2")],
            new Dictionary<string, string>());
        var reader = new FixedReader(new Dictionary<string, OutputManifest>
        { ["seed-marker"] = seedManifest, ["dump-marker"] = dumpManifest });
        var payload = await new WorkInputBuilder(reader, shards).BuildPayloadAsync(target, states, CancellationToken.None);
        Assert.Equal("https://example.test/aliases.json", payload["seed"]!.GetValue<string>());
        Assert.Equal(shards[0].Sha256, payload["dump"]!["sha256"]!.GetValue<string>());
        Assert.Equal(123, payload["dump"]!["bytes"]!.GetValue<long>());
        Assert.Equal(2, payload["InputFingerprints"]!.AsObject().Count);
    }

    [Fact]
    public async Task CommonsConverterReceivesPinnedDumpAndAllReferenceParquets()
    {
        var shards = new[] { new ManifestShard("p1", "enwiki-p1.xml.bz2", 1, "hash") };
        var graph = October2026Collection.Build(shards);
        var target = graph.Single(d => d.Id.Kind == "commons-image-metadata");
        var dump = graph.Single(d => d.Id.Kind == "commons-image-dump").Id;
        var parse = graph.Single(d => d.Id.Kind == "parse-shard").Id;
        var states = new Dictionary<TargetId, TargetState>
        {
            [dump] = TargetState.Empty(dump) with { ManifestPath = "dump", Fingerprint = "dump-fingerprint" },
            [parse] = TargetState.Empty(parse) with { ManifestPath = "parse", Fingerprint = "parse-fingerprint" }
        };
        var reader = new FixedReader(new Dictionary<string, OutputManifest>
        {
            ["dump"] = new(dump, "v1", [new(ImageDumpPins.Commons.FileName, "https://example.test/commons.gz", 5, new string('a', 64), "gzip-sql")], new Dictionary<string, string>()),
            ["parse"] = new(parse, "v1", [new("image_references.parquet", "https://example.test/refs.parquet", 5, new string('b', 64), "parquet")], new Dictionary<string, string>())
        });

        var payload = await new WorkInputBuilder(reader, shards).BuildPayloadAsync(target, states, CancellationToken.None);

        Assert.Equal("https://example.test/commons.gz", payload["dump"]!.GetValue<string>());
        Assert.Equal("https://example.test/refs.parquet", payload["image_references"]!.AsArray().Single()!.GetValue<string>());
    }

    [Fact]
    public async Task SourceSetRecordsArticleManifestAndBothImagePins()
    {
        var shards = new[] { new ManifestShard("p1", "enwiki-p1.xml.bz2", 1, "hash") };
        var target = October2026Collection.Build(shards).Single(d => d.Id.Kind == "source-set");
        var source = target.ContentDependencies.Single();
        var states = new Dictionary<TargetId, TargetState>
        { [source] = TargetState.Empty(source) with { ManifestPath = "source", Fingerprint = "article-source" } };
        var reader = new FixedReader(new Dictionary<string, OutputManifest>
        { ["source"] = new(source, "v1", [new("SHA256SUMS", "https://example.test/SHA256SUMS", 5, new string('a', 64), "text")], new Dictionary<string, string>()) });

        var payload = await new WorkInputBuilder(reader, shards).BuildPayloadAsync(target, states, CancellationToken.None);

        Assert.Equal("https://example.test/SHA256SUMS", payload["pins"]!["articleManifest"]!.GetValue<string>());
        Assert.Equal(2, payload["pins"]!["imageDumps"]!.AsArray().Count);
        Assert.Equal(ImageDumpPins.Commons.Sha1, payload["pins"]!["imageDumps"]![1]!["Sha1"]!.GetValue<string>());
    }

    private sealed class FixedReader(IReadOnlyDictionary<string, OutputManifest> manifests) : IOutputManifestReader
    {
        public Task<OutputManifest> ReadAsync(string markerUri, CancellationToken ct) => Task.FromResult(manifests[markerUri]);
    }
}
