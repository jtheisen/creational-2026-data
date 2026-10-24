using Creational.Dataflow.Sql;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCatalogLoaderTests
{
    [Fact]
    public async Task RefusesIncompleteShardSetBeforeConnectingToSql()
    {
        var root = Path.Combine(Path.GetTempPath(), "creational-incomplete-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "parse", "a"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "parse", "a", "articles.parquet"), []);
            File.WriteAllBytes(Path.Combine(root, "parse", "a", "image_references.parquet"), []);
            var loader = new SqlCatalogLoader(new SqlConnectionFactory("Server=invalid"));
            await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(root,
                Path.Combine(root, "taxonomy_names.parquet"), 1, "enwiki-test", 1, "ott-test", 10,
                ["a", "b"], CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
