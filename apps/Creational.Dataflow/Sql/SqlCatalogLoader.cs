using System.Data;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Sql;

public sealed record SqlCatalogLoadReport(short CollectionId, short TreeId,
    IReadOnlyDictionary<string, long> SourceRows, long LinkedImageReferences);

public sealed class SqlCatalogLoader(SqlConnectionFactory connections, string schema = "catalog")
{
    private readonly string _schema = SqlCatalogSchema.ValidateName(schema);
    private readonly SqlBulkBatchWriter _writer = new(schema);

    public async Task<SqlCatalogLoadReport> LoadAsync(string root, string namesPath,
        short collectionId, string collectionKey, short treeId, string treeKey,
        int batchSize, IReadOnlyCollection<string> expectedShardKeys, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var inputs = CatalogInputs.Create(root, namesPath, expectedShardKeys);
        await using var sql = await connections.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(sql, cancellationToken);
        await EnsureVersionAsync(sql, "Collection", "CollectionId", "CollectionKey", collectionId,
            collectionKey, inputs.CollectionFingerprint, cancellationToken);
        await EnsureVersionAsync(sql, "TaxonomyTree", "TreeId", "TreeKey", treeId,
            treeKey, inputs.TreeFingerprint, cancellationToken);

        var counts = new Dictionary<string, long>();
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();

        var taxonCheckpoint = await _writer.GetCheckpointAsync(sql, $"Taxon:{treeId}", inputs.TreeFingerprint,
            cancellationToken);
        if (!taxonCheckpoint.Completed)
        {
            var intervals = ReadIntervals(duck, inputs.Root);
            var ordinal = taxonCheckpoint.NextOrdinal;
            while (ordinal < intervals.LongLength)
            {
                var rows = new DataTable();
                rows.Columns.Add("TreeId", typeof(short));
                rows.Columns.Add("OttId", typeof(int));
                rows.Columns.Add("ParentOttId", typeof(int));
                rows.Columns.Add("DfsStart", typeof(int));
                rows.Columns.Add("DfsEndExclusive", typeof(int));
                var end = Math.Min(intervals.LongLength, ordinal + batchSize);
                for (var i = ordinal; i < end; i++)
                {
                    var taxon = intervals[checked((int)i)];
                    rows.Rows.Add(treeId, taxon.OttId, taxon.ParentOttId is { } parent ? parent : DBNull.Value,
                        taxon.Start, taxon.EndExclusive);
                }
                await _writer.WriteAsync(sql, "Taxon", $"Taxon:{treeId}", inputs.TreeFingerprint,
                    new SqlCatalogBatch(rows, end), cancellationToken);
                ordinal = end;
            }
            await _writer.MarkCompletedAsync(sql, $"Taxon:{treeId}", inputs.TreeFingerprint,
                intervals.LongLength, cancellationToken);
            counts["Taxon"] = intervals.LongLength;
        }
        else counts["Taxon"] = taxonCheckpoint.NextOrdinal;

        counts["TaxonName"] = await LoadQueryAsync(sql, duck, "TaxonName", $"TaxonName:{treeId}",
            inputs.NameFingerprint, SqlCatalogQueries.TaxonNamesFile(inputs.NamesPath, treeId), batchSize,
            cancellationToken);
        counts["Page"] = await LoadQueryAsync(sql, duck, "Page", $"Page:{collectionId}",
            inputs.PageFingerprint, SqlCatalogQueries.Pages(inputs.Root, collectionId), batchSize,
            cancellationToken);
        counts["TaxonPage"] = await LoadQueryAsync(sql, duck, "TaxonPage", $"TaxonPage:{treeId}:{collectionId}",
            inputs.CorrelationFingerprint, SqlCatalogQueries.TaxonPages(inputs.Root, treeId, collectionId),
            batchSize, cancellationToken);

        await CreateImageBatchTableAsync(sql, cancellationToken);
        var imageTransform = new SqlCatalogBatchTransform("#CatalogImageBatch", $"""
            INSERT INTO [{_schema}].[Image]
                (SourceWiki,CanonicalFileTitle,MetadataStatus,OriginalBytes,SourceSha1,UploadedAt,Mime,MediaType)
            SELECT b.SourceWiki,b.CanonicalFileTitle,b.MetadataStatus,b.OriginalBytes,b.SourceSha1,
                   b.UploadedAt,b.Mime,b.MediaType
            FROM #CatalogImageBatch b
            WHERE NOT EXISTS (
                SELECT 1 FROM [{_schema}].[Image] i WITH (UPDLOCK,HOLDLOCK)
                WHERE i.SourceWiki=b.SourceWiki
                  AND i.CanonicalFileTitle=b.CanonicalFileTitle COLLATE Latin1_General_100_BIN2);
            DELETE FROM #CatalogImageBatch;
            """);
        counts["Image"] = await LoadQueryAsync(sql, duck, "Image", $"Image:{collectionId}",
            inputs.ImageFingerprint, SqlCatalogQueries.Images(inputs.Root), batchSize,
            cancellationToken, imageTransform);
        counts["PageImage"] = await LoadQueryAsync(sql, duck, "PageImage", $"PageImage:{collectionId}",
            inputs.ReferenceFingerprint, SqlCatalogQueries.PageImages(inputs.Root, collectionId),
            batchSize, cancellationToken);

        long linked;
        while (true)
        {
            await using var link = new SqlCommand($"""
                UPDATE TOP (5000) p SET ImageId=i.ImageId
                FROM [{_schema}].[PageImage] p
                JOIN [{_schema}].[Image] i ON i.SourceWiki=p.SourceWiki
                 AND i.CanonicalFileTitle=p.FileTitle COLLATE Latin1_General_100_BIN2
                WHERE p.CollectionId=@collection AND p.ImageId IS NULL;
                """, sql) { CommandTimeout = 120 };
            link.Parameters.Add("@collection", SqlDbType.SmallInt).Value = collectionId;
            if (await link.ExecuteNonQueryAsync(cancellationToken) == 0) break;
        }
        await using (var verify = new SqlCommand($"""
            SELECT COUNT_BIG(*) FROM [{_schema}].[PageImage]
            WHERE CollectionId=@collection AND SourceWiki IS NOT NULL AND ImageId IS NULL;
            SELECT COUNT_BIG(*) FROM [{_schema}].[PageImage]
            WHERE CollectionId=@collection AND ImageId IS NOT NULL;
            """, sql))
        {
            verify.Parameters.Add("@collection", SqlDbType.SmallInt).Value = collectionId;
            await using var result = await verify.ExecuteReaderAsync(cancellationToken);
            await result.ReadAsync(cancellationToken);
            var unlinked = result.GetInt64(0);
            await result.NextResultAsync(cancellationToken);
            await result.ReadAsync(cancellationToken);
            linked = result.GetInt64(0);
            if (unlinked > 0)
                throw new InvalidDataException($"{unlinked} resolved page images have no global image identity.");
        }
        return new(collectionId, treeId, counts, linked);
    }

    private async Task<long> LoadQueryAsync(SqlConnection sql, DuckDBConnection duck, string table,
        string loadKey, string fingerprint, string query, int batchSize, CancellationToken ct,
        SqlCatalogBatchTransform? transform = null)
    {
        var checkpoint = await _writer.GetCheckpointAsync(sql, loadKey, fingerprint, ct);
        if (checkpoint.Completed) return checkpoint.NextOrdinal;
        using var command = duck.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        long ordinal = checkpoint.NextOrdinal;
        await foreach (var batch in SqlCatalogBatcher.ReadAsync(reader, batchSize, ordinal, ct))
        {
            await _writer.WriteAsync(sql, table, loadKey, fingerprint, batch, ct, transform);
            ordinal = batch.NextOrdinal;
        }
        await _writer.MarkCompletedAsync(sql, loadKey, fingerprint, ordinal, ct);
        return ordinal;
    }

    private static TaxonInterval[] ReadIntervals(DuckDBConnection duck, string root)
    {
        using var command = duck.CreateCommand();
        command.CommandText = SqlCatalogQueries.Edges(root);
        using var reader = command.ExecuteReader();
        var edges = new List<TaxonEdge>();
        while (reader.Read())
            edges.Add(new(checked((int)reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : checked((int)reader.GetInt64(1))));
        return TaxonDfs.Build(edges);
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var probe = new SqlCommand("SELECT OBJECT_ID(@name, 'U')", connection);
        probe.Parameters.AddWithValue("@name", $"{_schema}.CatalogLoadCheckpoint");
        if (await probe.ExecuteScalarAsync(ct) is not DBNull) return;
        await using var createSchema = new SqlCommand($"IF SCHEMA_ID(N'{_schema}') IS NULL EXEC(N'CREATE SCHEMA [{_schema}]')", connection);
        await createSchema.ExecuteNonQueryAsync(ct);
        await using var migration = new SqlCommand(SqlCatalogSchema.Render(_schema), connection)
        { CommandTimeout = 120 };
        await migration.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureVersionAsync(SqlConnection connection, string table, string idColumn,
        string keyColumn, short id, string key, string fingerprint, CancellationToken ct)
    {
        await using var command = new SqlCommand($"""
            IF NOT EXISTS (SELECT 1 FROM [{_schema}].[{table}] WHERE [{idColumn}]=@id)
                INSERT INTO [{_schema}].[{table}]([{idColumn}],[{keyColumn}],SourceFingerprint)
                VALUES (@id,@key,@fingerprint);
            SELECT [{keyColumn}],SourceFingerprint FROM [{_schema}].[{table}] WHERE [{idColumn}]=@id;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.SmallInt).Value = id;
        command.Parameters.Add("@key", SqlDbType.NVarChar, 128).Value = key;
        command.Parameters.Add("@fingerprint", SqlDbType.Char, 64).Value = fingerprint;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != key || reader.IsDBNull(1) ||
            !reader.GetString(1).Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{table} ID {id} already names a different input version.");
    }

    private static async Task CreateImageBatchTableAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            CREATE TABLE #CatalogImageBatch (
                SourceWiki VARCHAR(16) NOT NULL,
                CanonicalFileTitle NVARCHAR(512) NOT NULL,
                MetadataStatus VARCHAR(32) NOT NULL,
                OriginalBytes BIGINT NULL,
                SourceSha1 CHAR(40) NULL,
                UploadedAt DATETIME2(0) NULL,
                Mime VARCHAR(128) NULL,
                MediaType VARCHAR(32) NULL
            )
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal sealed record CatalogInputs(string Root, string NamesPath, string TreeFingerprint,
    string NameFingerprint, string PageFingerprint, string CorrelationFingerprint,
    string ImageFingerprint, string ReferenceFingerprint, string CollectionFingerprint)
{
    public static CatalogInputs Create(string root, string namesPath,
        IReadOnlyCollection<string> expectedShardKeys)
    {
        root = Path.GetFullPath(root);
        namesPath = Path.GetFullPath(namesPath);
        string Fingerprint(params string[] paths)
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                digest.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
                using var file = File.OpenRead(path);
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = file.Read(buffer)) > 0) digest.AppendData(buffer.AsSpan(0, count));
            }
            return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        string[] ParseFiles(string name) => Directory.GetFiles(Path.Combine(root, "parse"), name,
            SearchOption.AllDirectories);
        var articles = ParseFiles("articles.parquet");
        var refs = ParseFiles("image_references.parquet");
        var expected = expectedShardKeys.ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0 || expected.Count != expectedShardKeys.Count ||
            !articles.Select(x => Path.GetFileName(Path.GetDirectoryName(x)!)).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected) ||
            !refs.Select(x => Path.GetFileName(Path.GetDirectoryName(x)!)).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected) || articles.Length != expected.Count || refs.Length != expected.Count)
            throw new InvalidDataException("Catalog parse shards do not match the source manifest.");
        var tree = Fingerprint(Path.Combine(root, "edges.parquet"));
        var names = Fingerprint(namesPath);
        var pages = Fingerprint(articles.Concat(refs).ToArray());
        var matches = Fingerprint(Path.Combine(root, "matches.parquet"));
        var images = Fingerprint(Path.Combine(root, "resolved_images.parquet"));
        var references = Fingerprint(refs.Append(Path.Combine(root, "resolved_images.parquet")).ToArray());
        var collection = Fingerprint(articles.Concat(refs).Append(Path.Combine(root, "matches.parquet"))
            .Append(Path.Combine(root, "resolved_images.parquet")).ToArray());
        return new(root, namesPath, tree, names, pages, matches, images, references, collection);
    }
}
