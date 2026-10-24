using Creational.Dataflow.Sql;
using DuckDB.NET.Data;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class TaxonDfsTests
{
    [Fact]
    public void IncludesUnnamedIntermediateTaxaInLeafRange()
    {
        var intervals = TaxonDfs.Build([
            new(13, 10), new(12, 11), new(10, null), new(11, 10)
        ]).ToDictionary(x => x.OttId);

        Assert.Equal((0, 4), (intervals[10].Start, intervals[10].EndExclusive));
        Assert.Equal((1, 3), (intervals[11].Start, intervals[11].EndExclusive));
        Assert.Equal((2, 3), (intervals[12].Start, intervals[12].EndExclusive));
        Assert.Equal((3, 4), (intervals[13].Start, intervals[13].EndExclusive));
    }

    [Fact]
    public void RejectsIncompleteOrCyclicEdges()
    {
        Assert.Throws<InvalidDataException>(() => TaxonDfs.Build([new(10, null), new(11, 99)]));
        Assert.Throws<InvalidDataException>(() => TaxonDfs.Build([new(10, null), new(11, 12), new(12, 11)]));
        Assert.Throws<InvalidDataException>(() => TaxonDfs.Build([new(10, null), new(10, null)]));
    }

    [Fact]
    public void PinnedOttHierarchyProducesOneDenseRootInterval()
    {
        var path = Environment.GetEnvironmentVariable("CREATIONAL_OTT_EDGES_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT ott_id,parent_ott_id FROM read_parquet('{path.Replace("'", "''")}')";
        using var reader = command.ExecuteReader();
        var edges = new List<TaxonEdge>(4_528_126);
        while (reader.Read())
            edges.Add(new TaxonEdge(checked((int)reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : checked((int)reader.GetInt64(1))));

        var intervals = TaxonDfs.Build(edges);
        Assert.Equal(4_528_126, intervals.Length);
        var root = Assert.Single(intervals, x => x.ParentOttId is null);
        Assert.Equal(0, root.Start);
        Assert.Equal(intervals.Length, root.EndExclusive);
    }
}
