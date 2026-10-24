using Creational.Dataflow.Sql;
using DuckDB.NET.Data;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCatalogBatcherTests
{
    [Fact]
    public async Task PreservesRowsAndCheckpointsAcrossBatchBoundaries()
    {
        using var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT CAST(i AS INTEGER) AS OttId FROM range(5) t(i) ORDER BY OttId";
        using var reader = command.ExecuteReader();
        var batches = new List<SqlCatalogBatch>();
        await foreach (var batch in SqlCatalogBatcher.ReadAsync(reader, 2, 0, CancellationToken.None))
            batches.Add(batch);

        Assert.Equal([2L, 4L, 5L], batches.Select(x => x.NextOrdinal));
        Assert.Equal([0, 1, 2, 3, 4], batches.SelectMany(x => x.Rows.Rows.Cast<System.Data.DataRow>())
            .Select(x => (int)x[0]));
    }

    [Fact]
    public async Task ResumeSkipsAlreadyCommittedRows()
    {
        using var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT CAST(i AS INTEGER) AS OttId FROM range(5) t(i) ORDER BY OttId";
        using var reader = command.ExecuteReader();
        var batches = new List<SqlCatalogBatch>();
        await foreach (var batch in SqlCatalogBatcher.ReadAsync(reader, 2, 3, CancellationToken.None))
            batches.Add(batch);

        Assert.Single(batches);
        Assert.Equal(5, batches[0].NextOrdinal);
        Assert.Equal([3, 4], batches[0].Rows.Rows.Cast<System.Data.DataRow>().Select(x => (int)x[0]));
    }
}
