using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class TargetDefinitionTests
{
    [Fact]
    public void NineteenShardsProduceTheCompleteGraph()
    {
        var shards = Enumerable.Range(1, 19)
            .Select(i => new ManifestShard($"p{i}", $"enwiki-p{i}.xml.bz2", 100, $"hash-{i}"))
            .ToArray();

        var graph = October2026Collection.Build(shards);

        Assert.Equal(89, graph.Count);
        Assert.Equal(83, graph.Count(t => t.Id.Kind is "source-manifest" or "dump-shard" or "alias-index" or
            "alias-seed" or "candidate-pool-shard" or "candidate-pool-finalize" or "parse-shard" or
            "parse-finalize" or "ott-source" or "ott-names" or "ott-correlation"));
        foreach (var kind in new[] { "dump-shard", "alias-index", "candidate-pool-shard", "parse-shard" })
            Assert.Equal(19, graph.Count(t => t.Id.Kind == kind));
        foreach (var kind in new[] { "source-manifest", "alias-seed", "candidate-pool-finalize", "parse-finalize", "ott-source", "ott-names", "ott-correlation" })
            Assert.Single(graph, t => t.Id.Kind == kind);
        foreach (var kind in new[] { "source-set", "enwiki-image-dump", "commons-image-dump",
            "enwiki-image-metadata", "commons-image-metadata", "resolved-image-metadata" })
            Assert.Single(graph, t => t.Id.Kind == kind);
    }

    [Fact]
    public void ParseShardDependsOnItsPoolAndAliasSeed()
    {
        var graph = October2026Collection.Build(Shards(2));
        var parse = graph.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p1");
        Assert.Contains(new TargetId(October2026Collection.Collection, "candidate-pool-shard", "p1"), parse.ContentDependencies);
        Assert.Contains(new TargetId(October2026Collection.Collection, "alias-seed", "all"), parse.ContentDependencies);
        Assert.Equal(2, parse.ContentDependencies.Count);
        Assert.Equal("candidate-pool-finalize", Assert.Single(parse.ReadinessGates).Kind);
        Assert.Equal(21, graph.Count);
        Assert.Equal(graph.Count, graph.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void ImageMetadataDependenciesDoNotChangeLegacyTargets()
    {
        var graph = October2026Collection.Build(Shards(2));
        var parse = graph.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p1");
        Assert.Equal("candidate-parse-v1", parse.RecipeVersion);
        Assert.DoesNotContain(parse.ContentDependencies, id => id.Kind.Contains("image") || id.Kind == "source-set");
        var en = graph.Single(t => t.Id.Kind == "enwiki-image-metadata");
        Assert.Equal(["enwiki-image-dump"], en.ContentDependencies.Select(id => id.Kind));
        var commons = graph.Single(t => t.Id.Kind == "commons-image-metadata");
        Assert.Contains(commons.ContentDependencies, id => id.Kind == "commons-image-dump");
        Assert.Equal(2, commons.ContentDependencies.Count(id => id.Kind == "parse-shard"));
        Assert.Equal(2, graph.Single(t => t.Id.Kind == "resolved-image-metadata").ContentDependencies.Count(id => id.Kind.EndsWith("-image-metadata")));
    }

    private static ManifestShard[] Shards(int count) => Enumerable.Range(1, count)
        .Select(i => new ManifestShard($"p{i}", $"enwiki-p{i}.xml.bz2", 100, $"hash-{i}"))
        .ToArray();
}
