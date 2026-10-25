using Creational.Dataflow.Sql;
using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCatalogSchemaTests
{
    [Fact]
    public async Task CapacityPreflightMeasuresUtf8BytesOfUnicodeText()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "ct_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
        await using (var create = new SqlCommand($"CREATE SCHEMA [{schema}]", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var create = new SqlCommand($"CREATE TABLE [{schema}].[Samples](Name NVARCHAR(20) NOT NULL)", connection))
                await create.ExecuteNonQueryAsync();
            await using (var insert = new SqlCommand($"INSERT INTO [{schema}].[Samples](Name) VALUES (N'cat'),(N'é'),(N'中'),(N'𐀀')", connection))
                await insert.ExecuteNonQueryAsync();

            var result = await SqlCatalogCapacity.ReadAsync(connection, schema, CancellationToken.None);
            var name = result.TextColumns.Single(x => x.Table == "Samples" && x.Column == "Name");
            Assert.Equal(4, name.MaxUtf8Bytes);
            Assert.Equal(60, name.SafeUtf8Bytes);
            Assert.Equal(4, result.Tables.Single(x => x.Name == "Samples").Rows);
        }
        finally
        {
            await using var drop = new SqlCommand($"DROP TABLE IF EXISTS [{schema}].[Samples]; DROP SCHEMA [{schema}]", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BulkCheckpointCommitsRowsOnceAndRejectsChangedInputs()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "ct_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
        await using (var create = new SqlCommand($"CREATE SCHEMA [{schema}]", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var migrate = new SqlCommand(SqlCatalogSchema.Render(schema), connection))
                await migrate.ExecuteNonQueryAsync();
            await using (var collection = new SqlCommand($"INSERT INTO [{schema}].Collection(CollectionId,CollectionKey) VALUES (1,N'test')", connection))
                await collection.ExecuteNonQueryAsync();

            var writer = new SqlBulkBatchWriter(schema);
            const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var first = new SqlCatalogBatch(Pages(1, 2), 2);
            await writer.WriteAsync(connection, "Page", "Page:1", fingerprint, first, CancellationToken.None);
            Assert.Equal(2, (await writer.GetCheckpointAsync(connection, "Page:1", fingerprint,
                CancellationToken.None)).NextOrdinal);

            var second = new SqlCatalogBatch(Pages(3), 3);
            await writer.WriteAsync(connection, "Page", "Page:1", fingerprint, second, CancellationToken.None);
            await writer.WriteAsync(connection, "Page", "Page:1", fingerprint, second, CancellationToken.None);
            await writer.MarkCompletedAsync(connection, "Page:1", fingerprint, 3, CancellationToken.None);
            Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Page", connection, null));
            Assert.True((await writer.GetCheckpointAsync(connection, "Page:1", fingerprint,
                CancellationToken.None)).Completed);
            await writer.MarkCompletedAsync(connection, "Empty:1", fingerprint, 0, CancellationToken.None);
            Assert.True((await writer.GetCheckpointAsync(connection, "Empty:1", fingerprint,
                CancellationToken.None)).Completed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.GetCheckpointAsync(connection,
                "Page:1", new string('b', 64), CancellationToken.None));
        }
        finally
        {
            foreach (var table in new[] { "PageImage", "Rendition", "ImageRights", "TaxonPage",
                "TaxonName", "Page", "Image", "Taxon", "Collection", "TaxonomyTree", "CatalogLoadCheckpoint",
                "RenditionShape", "LicenseCategory" })
            {
                await using var dropTable = new SqlCommand($"IF OBJECT_ID(N'{schema}.{table}',N'U') IS NOT NULL DROP TABLE [{schema}].[{table}]", connection);
                await dropTable.ExecuteNonQueryAsync();
            }
            await using var drop = new SqlCommand($"DROP SCHEMA [{schema}]", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static DataTable Pages(params long[] ids)
    {
        var rows = new DataTable();
        rows.Columns.Add("CollectionId", typeof(short));
        rows.Columns.Add("PageId", typeof(long));
        rows.Columns.Add("Title", typeof(string));
        rows.Columns.Add("RevisionId", typeof(long));
        rows.Columns.Add("UnresolvedImageCount", typeof(int));
        foreach (var id in ids) rows.Rows.Add((short)1, id, $"Page {id}", id, 0);
        return rows;
    }

    [Fact]
    public async Task CatalogKeepsGlobalImagesAndUnnamedTaxonAncestors()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var schema = "ct_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
        await using (var create = new SqlCommand($"CREATE SCHEMA [{schema}]", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await ExecuteAsync(SqlCatalogSchema.Render(schema), connection, transaction);
            await ExecuteAsync(SqlCatalogSchema.RenderTaxonPageDfsColumn(schema), connection, transaction);
            await ExecuteAsync(SqlCatalogSchema.RenderTaxonPageDfsColumn(schema), connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Collection(CollectionId,CollectionKey) VALUES (1,N'enwiki-2026-10-01')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonomyTree(TreeId,TreeKey) VALUES (1,N'ott3.3')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Taxon(TreeId,OttId,ParentOttId,DfsStart,DfsEndExclusive) VALUES (1,10,NULL,1,4),(1,11,10,2,4),(1,12,11,3,4)", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonName(TreeId,OttId,Name) VALUES (1,10,N'Life'),(1,12,N'Child species')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Image(SourceWiki,CanonicalFileTitle) VALUES ('enwiki',N'File:Example.jpg'),('commonswiki',N'File:Example.jpg')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Page(CollectionId,PageId,Title) VALUES (1,100,N'Example animal')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonPage(TreeId,OttId,CollectionId,PageId,MatchKind,DfsStart) VALUES (1,12,1,100,'exact',3)", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].PageImage(CollectionId,PageId,ReferenceIndex,FileTitle,SourceWiki,ImageId,Role) VALUES (1,100,0,N'File:Unknown.jpg',NULL,NULL,'taxobox')", connection, transaction);

            Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Taxon WHERE TreeId=1 AND DfsStart>=1 AND DfsStart<4", connection, transaction));
            Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Image WHERE CanonicalFileTitle=N'File:Example.jpg'", connection, transaction));
            Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].TaxonName WHERE OttId=11", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].PageImage WHERE ImageId IS NULL AND FileTitle=N'File:Unknown.jpg'", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].TaxonPage WHERE DfsStart>=2 AND DfsStart<4", connection, transaction));
            Assert.Equal("smallint", await TypeAsync(schema, "Collection", "CollectionId", connection, transaction));
            Assert.Equal("int", await TypeAsync(schema, "Taxon", "OttId", connection, transaction));
            Assert.Equal("bigint", await TypeAsync(schema, "Page", "PageId", connection, transaction));
            Assert.Equal("varchar", await TypeAsync(schema, "TaxonName", "Name", connection, transaction));
            Assert.Equal("varchar", await TypeAsync(schema, "Image", "CanonicalFileTitle", connection, transaction));
            var duplicate = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
                $"INSERT INTO [{schema}].Image(SourceWiki,CanonicalFileTitle) VALUES ('enwiki',N'File:Example.jpg')",
                connection, transaction));
            Assert.True(duplicate.Number is 2601 or 2627);
            await transaction.RollbackAsync();
        }
        finally
        {
            await using var drop = new SqlCommand($"DROP SCHEMA [{schema}]", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Utf8MigrationPreservesNamesAndBinaryDistinctImageTitles()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "ct_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
        await using (var create = new SqlCommand($"CREATE SCHEMA [{schema}]", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await ExecuteAsync(SqlCatalogSchema.RenderLegacy(schema), connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Collection(CollectionId,CollectionKey) VALUES (1,N'enwiki')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonomyTree(TreeId,TreeKey) VALUES (1,N'ott')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Taxon(TreeId,OttId,DfsStart,DfsEndExclusive) VALUES (1,1,1,2)", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonName(TreeId,OttId,Name) VALUES (1,1,N'中 𐀀 é')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Page(CollectionId,PageId,Title) VALUES (1,1,N'Élan 𐀀')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Image(SourceWiki,CanonicalFileTitle,SourceSha1) VALUES ('commonswiki',N'File:Élan.jpg','abc'),('commonswiki',N'File:élan.jpg','def')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].PageImage(CollectionId,PageId,ReferenceIndex,FileTitle,SourceWiki,Role) VALUES (1,1,0,N'File:Élan.jpg','commonswiki','taxobox')", connection, transaction);

            var steps = SqlCatalogSchema.RenderUtf8ConversionSteps(schema);
            Assert.True(steps.Count >= 10);
            await ExecuteAsync(steps[0], connection, transaction);
            foreach (var step in steps) await ExecuteAsync(step, connection, transaction);
            foreach (var step in steps) await ExecuteAsync(step, connection, transaction);

            Assert.Equal("varchar", await TypeAsync(schema, "TaxonName", "Name", connection, transaction));
            Assert.Equal("varchar", await TypeAsync(schema, "Page", "Title", connection, transaction));
            Assert.Equal("varchar", await TypeAsync(schema, "Image", "CanonicalFileTitle", connection, transaction));
            Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Image", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].TaxonName WHERE Name='中 𐀀 é'", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].PageImage WHERE FileTitle='File:Élan.jpg'", connection, transaction));
            await transaction.RollbackAsync();
        }
        finally
        {
            await using var drop = new SqlCommand($"DROP SCHEMA [{schema}]", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PageCompressionCanResumeWithoutLosingCatalogRows()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "ct_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
        await using (var create = new SqlCommand($"CREATE SCHEMA [{schema}]", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await ExecuteAsync(SqlCatalogSchema.Render(schema), connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Collection(CollectionId,CollectionKey) VALUES (1,'enwiki')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonomyTree(TreeId,TreeKey) VALUES (1,'ott')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Taxon(TreeId,OttId,DfsStart,DfsEndExclusive) VALUES (1,1,1,2)", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonName(TreeId,OttId,Name) VALUES (1,1,'Élan 𐀀')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Page(CollectionId,PageId,Title) VALUES (1,1,'Élan')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Image(SourceWiki,CanonicalFileTitle) VALUES ('commonswiki','File:Élan.jpg')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].PageImage(CollectionId,PageId,ReferenceIndex,FileTitle,SourceWiki,Role) VALUES (1,1,0,'File:Élan.jpg','commonswiki','taxobox')", connection, transaction);

            var steps = SqlCatalogSchema.RenderPageCompressionSteps(schema);
            Assert.True(steps.Count >= 6);
            await ExecuteAsync(steps[0], connection, transaction);
            foreach (var step in steps) await ExecuteAsync(step, connection, transaction);
            foreach (var step in steps) await ExecuteAsync(step, connection, transaction);

            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].TaxonName WHERE Name='Élan 𐀀'", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].PageImage WHERE FileTitle='File:Élan.jpg'", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM sys.partitions WHERE object_id=OBJECT_ID(N'{schema}.TaxonName') AND index_id=1 AND data_compression_desc='PAGE'", connection, transaction));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM sys.partitions WHERE object_id=OBJECT_ID(N'{schema}.Image') AND index_id=1 AND data_compression_desc='PAGE'", connection, transaction));
            await transaction.RollbackAsync();
        }
        finally
        {
            await using var drop = new SqlCommand($"DROP SCHEMA [{schema}]", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CompactImageMigrationAndRenditionBackfillAreRepeatable()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "ct_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = await new SqlConnectionFactory(connectionString).OpenAsync(CancellationToken.None);
        await using (var create = new SqlCommand($"CREATE SCHEMA [{schema}]", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var old = new SqlCommand(SqlCatalogSchema.RenderLegacy(schema), connection))
                await old.ExecuteNonQueryAsync();
            await using (var images = new SqlCommand($"""
                INSERT INTO [{schema}].Image(SourceWiki,CanonicalFileTitle,MetadataStatus)
                VALUES ('commonswiki','File:Animal.jpg','commons'),
                       ('commonswiki','File:Unsupported.svg','unsupported-media');
                """, connection))
                await images.ExecuteNonQueryAsync();

            var migration = SqlCatalogSchema.RenderCompactImages(schema);
            await using (var rights = new SqlCommand($"INSERT INTO [{schema}].ImageRights(ImageId) SELECT TOP 1 ImageId FROM [{schema}].Image", connection))
                await rights.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var blocked = new SqlCommand(migration, connection);
                await blocked.ExecuteNonQueryAsync();
            });
            await using (var clear = new SqlCommand($"DELETE FROM [{schema}].ImageRights", connection))
                await clear.ExecuteNonQueryAsync();
            await using (var oldRendition = new SqlCommand($"INSERT INTO [{schema}].Rendition(ImageId,Variant,Status) SELECT TOP 1 ImageId,'w250','pending' FROM [{schema}].Image", connection))
                await oldRendition.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var blocked = new SqlCommand(migration, connection);
                await blocked.ExecuteNonQueryAsync();
            });
            await using (var clear = new SqlCommand($"DELETE FROM [{schema}].Rendition", connection))
                await clear.ExecuteNonQueryAsync();
            await using (var migrate = new SqlCommand(migration, connection))
                await migrate.ExecuteNonQueryAsync();
            await using (var migrate = new SqlCommand(migration, connection))
                await migrate.ExecuteNonQueryAsync();

            await using (var alter = new SqlCommand($"UPDATE [{schema}].RenditionShape SET RecipeVersion='changed' WHERE ShapeId=2", connection))
                await alter.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var blocked = new SqlCommand(migration, connection);
                await blocked.ExecuteNonQueryAsync();
            });
            await using (var restore = new SqlCommand($"UPDATE [{schema}].RenditionShape SET RecipeVersion='webp-from-w250-v1' WHERE ShapeId=2", connection))
                await restore.ExecuteNonQueryAsync();

            Assert.Equal(5, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].RenditionShape", connection, null));
            Assert.Equal(6, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].LicenseCategory", connection, null));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].RenditionShape WHERE ShapeId=1 AND PathKey='w250' AND SourceKind='wikimedia'", connection, null));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].RenditionShape WHERE ShapeId=2 AND PathKey='w20' AND SourceKind='derived'", connection, null));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].LicenseCategory WHERE LicenseCategoryId=6 AND Code='missing'", connection, null));
            Assert.Equal("varchar", await TypeAsync(schema, "RenditionShape", "PathKey", connection, null));
            Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Image WHERE LicenseCategoryId IS NULL", connection, null));

            var loader = new SqlCatalogLoader(new SqlConnectionFactory(connectionString), schema);
            Assert.Equal(10, await loader.EnsureRenditionsAsync(connection, 1, CancellationToken.None));
            Assert.Equal(0, await loader.EnsureRenditionsAsync(connection, 1, CancellationToken.None));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Rendition r JOIN [{schema}].Image i ON i.ImageId=r.ImageId WHERE r.ShapeId=1 AND i.MetadataStatus='unsupported-media'", connection, null));
            Assert.Equal(1, await ScalarAsync(SqlCatalogWorkQueries.EligibleSourceRenditionCount(schema), connection, null));

            var animal = $"(SELECT ImageId FROM [{schema}].Image WHERE CanonicalFileTitle='File:Animal.jpg')";
            await using (var suspend = new SqlCommand($"UPDATE [{schema}].Image SET Priority=-1 WHERE ImageId={animal}", connection))
                await suspend.ExecuteNonQueryAsync();
            Assert.Equal(0, await ScalarAsync(SqlCatalogWorkQueries.EligibleSourceRenditionCount(schema), connection, null));
            await using (var resume = new SqlCommand($"UPDATE [{schema}].Image SET Priority=0 WHERE ImageId={animal}", connection))
                await resume.ExecuteNonQueryAsync();
            await using (var set = new SqlCommand($"UPDATE [{schema}].Rendition SET Complete=1,Bytes=123,Stale=1,Failed=1 WHERE ImageId={animal} AND ShapeId=1", connection))
                await set.ExecuteNonQueryAsync();
            Assert.Equal(0, await loader.EnsureRenditionsAsync(connection, 1, CancellationToken.None));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Rendition WHERE ImageId={animal} AND ShapeId=1 AND Complete=1 AND Stale=1 AND Failed=1 AND Bytes=123", connection, null));
            await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var invalid = new SqlCommand($"UPDATE [{schema}].Rendition SET Bytes=NULL WHERE ImageId={animal} AND ShapeId=1", connection);
                await invalid.ExecuteNonQueryAsync();
            });
            await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var invalid = new SqlCommand($"UPDATE [{schema}].Rendition SET Complete=0 WHERE ImageId={animal} AND ShapeId=1", connection);
                await invalid.ExecuteNonQueryAsync();
            });
        }
        finally
        {
            await using var drop = new SqlCommand($"""
                DROP TABLE IF EXISTS [{schema}].[PageImage], [{schema}].[Rendition], [{schema}].[ImageRights],
                    [{schema}].[TaxonPage], [{schema}].[TaxonName], [{schema}].[Page], [{schema}].[Image],
                    [{schema}].[Taxon], [{schema}].[Collection], [{schema}].[TaxonomyTree],
                    [{schema}].[CatalogLoadCheckpoint], [{schema}].[RenditionShape], [{schema}].[LicenseCategory];
                DROP SCHEMA [{schema}];
                """, connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecuteAsync(string sql, SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string sql, SqlConnection connection, SqlTransaction? transaction)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TypeAsync(string schema, string table, string column,
        SqlConnection connection, SqlTransaction? transaction)
    {
        await using var command = new SqlCommand("""
            SELECT TYPE_NAME(c.user_type_id)
            FROM sys.columns c JOIN sys.tables t ON c.object_id=t.object_id
            JOIN sys.schemas s ON t.schema_id=s.schema_id
            WHERE s.name=@schema AND t.name=@table AND c.name=@column
            """, connection, transaction);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Column missing"));
    }
}
