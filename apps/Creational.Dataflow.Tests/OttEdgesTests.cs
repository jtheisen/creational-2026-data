using Creational.Dataflow.Images;
using DuckDB.NET.Data;
using Xunit;

namespace Creational.Dataflow.Tests;

public class OttEdgesTests
{
    [Fact]
    public async Task KeepsIntermediateAndUnnamedTaxaInTheHierarchy()
    {
        var root = Path.Combine(Path.GetTempPath(), "creational-edges-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "taxonomy.tsv");
            var output = Path.Combine(root, "edges.parquet");
            await File.WriteAllTextAsync(source, "uid\t|\tparent_uid\t|\tname\t|\t\n10\t|\t\t|\tlife\t|\t\n11\t|\t10\t|\ttechnical node\t|\t\n12\t|\t11\t|\tChild species\t|\t\n");

            await OttEdges.BuildAsync(source, output, CancellationToken.None);

            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM read_parquet('{output.Replace("'", "''")}') WHERE ott_id=12 AND parent_ott_id=11";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
