using Creational.Dataflow.Sql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCatalogSchemaTests
{
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
            await ExecuteAsync($"INSERT INTO [{schema}].Collection(CollectionId,CollectionKey) VALUES (1,N'enwiki-2026-10-01')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonomyTree(TreeId,TreeKey) VALUES (1,N'ott3.3')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Taxon(TreeId,OttId,ParentOttId,DfsStart,DfsEndExclusive) VALUES (1,10,NULL,1,4),(1,11,10,2,4),(1,12,11,3,4)", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].TaxonName(TreeId,OttId,Name) VALUES (1,10,N'Life'),(1,12,N'Child species')", connection, transaction);
            await ExecuteAsync($"INSERT INTO [{schema}].Image(SourceWiki,CanonicalFileTitle) VALUES ('enwiki',N'File:Example.jpg'),('commonswiki',N'File:Example.jpg')", connection, transaction);

            Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Taxon WHERE TreeId=1 AND DfsStart>=1 AND DfsStart<4", connection, transaction));
            Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].Image WHERE CanonicalFileTitle=N'File:Example.jpg'", connection, transaction));
            Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM [{schema}].TaxonName WHERE OttId=11", connection, transaction));
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

    private static async Task<int> ScalarAsync(string sql, SqlConnection connection, SqlTransaction transaction)
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
