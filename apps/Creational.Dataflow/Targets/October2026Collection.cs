namespace Creational.Dataflow.Targets;

public static class October2026Collection
{
    public const string Collection = "enwiki/2026-10-01";

    public static IReadOnlyList<TargetDefinition> Build(IReadOnlyList<ManifestShard> shards)
    {
        if (shards.Count == 0 || shards.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != shards.Count)
            throw new ArgumentException("A nonempty manifest with unique shard keys is required", nameof(shards));

        TargetId Id(string kind, string key = "all") => new(Collection, kind, key);
        TargetDefinition Define(string kind, string key, string recipe, IReadOnlyList<TargetId> dependencies,
            string[] outputs, params TargetId[] gates) =>
            new(Id(kind, key), recipe, dependencies, gates, outputs);

        var result = new List<TargetDefinition>
        {
            Define("source-manifest", "all", "2026-10-01", [], ["SHA256SUMS"]),
            Define("ott-source", "all", "ott3.3", [], ["taxonomy.tsv"])
        };
        foreach (var shard in shards)
        {
            result.Add(Define("dump-shard", shard.Key, "2026-10-01", [Id("source-manifest")], [shard.FileName]));
            result.Add(Define("alias-index", shard.Key, "extract-v2", [Id("dump-shard", shard.Key)], ["index.json"]));
        }
        result.Add(Define("alias-seed", "all", "extract-v2", shards.Select(s => Id("alias-index", s.Key)).ToArray(), ["aliases.json"]));
        foreach (var shard in shards)
            result.Add(Define("candidate-pool-shard", shard.Key, "candidate-pool-v1",
                [Id("dump-shard", shard.Key), Id("alias-seed")],
                ["pool.parquet", "dependencies.parquet", "redirects.json", "report.json"]));
        result.Add(Define("candidate-pool-finalize", "all", "candidate-pool-v1",
            shards.Select(s => Id("candidate-pool-shard", s.Key)).Append(Id("alias-seed")).ToArray(),
            ["aliases-discovered.json"]));
        foreach (var shard in shards)
            result.Add(Define("parse-shard", shard.Key, "candidate-parse-v1",
                [Id("candidate-pool-shard", shard.Key), Id("alias-seed")],
                ["articles.parquet", "taxoboxes.parquet", "taxobox_parameters.parquet", "image_references.parquet", "report.json"],
                Id("candidate-pool-finalize")));
        result.Add(Define("parse-finalize", "all", "candidate-parse-v1",
            shards.Select(s => Id("parse-shard", s.Key)).ToArray(), []));
        result.Add(Define("ott-names", "all", "ott3.3", [Id("ott-source")], ["taxonomy_names.parquet", "report.json"]));
        result.Add(Define("ott-correlation", "all", "ott-wikipedia-correlation-v3",
            [Id("parse-finalize"), Id("ott-names")],
            ["wiki_names.parquet", "exact_matches.parquet", "match_status.parquet", "near_name_candidates.parquet", "report.json"]));
        result.Add(Define("source-set", "all", "image-sources-v1",
            [Id("source-manifest")], ["pins.json"]));
        result.Add(Define("enwiki-image-dump", "all", "2026-10-01",
            [Id("source-set")], [ImageDumpPins.Enwiki.FileName]));
        result.Add(Define("commons-image-dump", "all", "2026-10-01",
            [Id("source-set")], [ImageDumpPins.Commons.FileName]));
        result.Add(Define("enwiki-image-metadata", "all", "image-sql-v1",
            [Id("enwiki-image-dump")], ["images.parquet", "report.json"]));
        result.Add(Define("commons-image-metadata", "all", "image-sql-v1",
            [Id("commons-image-dump"), ..shards.Select(s => Id("parse-shard", s.Key))],
            ["images.parquet", "report.json"]));
        result.Add(Define("resolved-image-metadata", "all", "image-resolution-v1",
            [Id("enwiki-image-metadata"), Id("commons-image-metadata"), ..shards.Select(s => Id("parse-shard", s.Key))],
            ["resolved_images.parquet", "report.json"]));
        return result;
    }
}
