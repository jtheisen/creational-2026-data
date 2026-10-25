using Creational.Dataflow.Sql;
using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCatalogSchemaTests
{
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
                "TaxonName", "Page", "Image", "Taxon", "Collection", "TaxonomyTree", "CatalogLoadCheckpoint" })
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
        SqlConnection connection, SqlTransaction transaction)
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
