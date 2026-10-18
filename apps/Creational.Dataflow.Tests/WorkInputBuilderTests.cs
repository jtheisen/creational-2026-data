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

    private sealed class FixedReader(IReadOnlyDictionary<string, OutputManifest> manifests) : IOutputManifestReader
    {
        public Task<OutputManifest> ReadAsync(string markerUri, CancellationToken ct) => Task.FromResult(manifests[markerUri]);
    }
}
