using Azure;
using Azure.Data.Tables;
using Creational.Dataflow.Execution;

namespace Creational.Dataflow.Storage;

public sealed class TableCollectionSettingsRepository(TableClient table) : ICollectionSettingsRepository
{
    private const string PartitionKey = "__settings";

    public async Task<int> GetParallelismAsync(string collection, CancellationToken cancellationToken)
    {
        var key = Uri.EscapeDataString(collection);
        var row = await table.GetEntityIfExistsAsync<TableEntity>(PartitionKey, key,
            cancellationToken: cancellationToken);
        if (row.HasValue) return ReadParallelism(row.Value!);

        try
        {
            await table.AddEntityAsync(new TableEntity(PartitionKey, key)
            {
                ["Parallelism"] = CollectionParallelism.Default
            }, cancellationToken);
            return CollectionParallelism.Default;
        }
        catch (RequestFailedException error) when (error.Status == 409)
        {
            row = await table.GetEntityIfExistsAsync<TableEntity>(PartitionKey, key,
                cancellationToken: cancellationToken);
            return ReadParallelism(row.Value!);
        }
    }

    public async Task SetParallelismAsync(string collection, int parallelism, CancellationToken cancellationToken)
    {
        CollectionParallelism.Validate(parallelism);
        var row = new TableEntity(PartitionKey, Uri.EscapeDataString(collection))
        {
            ["Parallelism"] = parallelism
        };
        await table.UpsertEntityAsync(row, TableUpdateMode.Replace, cancellationToken);
    }

    private static int ReadParallelism(TableEntity row) =>
        CollectionParallelism.Validate(row.GetInt32("Parallelism") ?? CollectionParallelism.Default);
}
