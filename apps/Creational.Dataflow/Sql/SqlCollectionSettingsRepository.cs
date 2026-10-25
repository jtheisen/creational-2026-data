using System.Data;
using Creational.Dataflow.Execution;
using Creational.Dataflow.Storage;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Sql;

public sealed class SqlCollectionSettingsRepository(SqlConnectionFactory factory, string schema)
    : ICollectionSettingsRepository
{
    private readonly string _table = $"[{SqlCatalogSchema.ValidateName(schema)}].[CollectionSetting]";

    public async Task<int> GetParallelismAsync(string collection, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"SELECT Parallelism FROM {_table} WHERE CollectionName=@c", connection);
        command.Parameters.Add("@c", SqlDbType.VarChar, 128).Value = collection;
        var value = await command.ExecuteScalarAsync(ct);
        return CollectionParallelism.Validate(value is null ? CollectionParallelism.Default : Convert.ToInt32(value));
    }

    public async Task SetParallelismAsync(string collection, int parallelism, CancellationToken ct)
    {
        CollectionParallelism.Validate(parallelism);
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            UPDATE {_table} SET Parallelism=@p WHERE CollectionName=@c;
            IF @@ROWCOUNT=0
                INSERT INTO {_table}(CollectionName,Parallelism) VALUES (@c,@p);
            """, connection);
        command.Parameters.Add("@c", SqlDbType.VarChar, 128).Value = collection;
        command.Parameters.Add("@p", SqlDbType.TinyInt).Value = parallelism;
        await command.ExecuteNonQueryAsync(ct);
    }
}
