using Creational.Dataflow.Sql;
using DuckDB.NET.Data;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCatalogQueriesTests
{
    [Fact]
    public void KeepsUnknownImageSourcesAndCountsMarkupWithoutTitles()
    {
        var root = Path.Combine(Path.GetTempPath(), "creational-sql-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "parse", "one"));
        try
        {
            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            Copy(db, "SELECT 1::BIGINT AS page_id,10::BIGINT AS revision_id,'Animal' AS title UNION ALL SELECT 2,20,'Plant'",
                Path.Combine(root, "parse", "one", "articles.parquet"));
            Copy(db, "SELECT 1::BIGINT AS page_id,10::BIGINT AS revision_id,0::INTEGER AS reference_index,'taxobox' AS source_kind,'File:Known.jpg' AS file_title UNION ALL SELECT 1,10,1,'gallery',NULL UNION ALL SELECT 2,20,0,'taxobox','File:Unknown.jpg'",
                Path.Combine(root, "parse", "one", "image_references.parquet"));
            Copy(db, "SELECT 'Known.jpg' AS canonical_key,'File:Known.jpg' AS file_title,'commonswiki' AS source_wiki",
                Path.Combine(root, "resolved_images.parquet"));

            using var pages = db.CreateCommand();
            pages.CommandText = SqlCatalogQueries.Pages(root, 1);
            using (var rows = pages.ExecuteReader())
            {
                Assert.True(rows.Read());
                Assert.Equal(1L, rows.GetInt64(1));
                Assert.Equal(1, rows.GetInt32(4));
                Assert.True(rows.Read());
                Assert.Equal(2L, rows.GetInt64(1));
                Assert.Equal(0, rows.GetInt32(4));
                Assert.False(rows.Read());
            }

            using var references = db.CreateCommand();
            references.CommandText = SqlCatalogQueries.PageImages(root, 1);
            using (var rows = references.ExecuteReader())
            {
                Assert.True(rows.Read());
                Assert.Equal("File:Known.jpg", rows.GetString(3));
                Assert.Equal("commonswiki", rows.GetString(4));
                Assert.True(rows.Read());
                Assert.Equal("File:Unknown.jpg", rows.GetString(3));
                Assert.True(rows.IsDBNull(4));
                Assert.False(rows.Read());
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PinnedCatalogDoesNotMultiplyImageReferences()
    {
        var root = Environment.GetEnvironmentVariable("CREATIONAL_CATALOG_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        using var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        using var expected = db.CreateCommand();
        expected.CommandText = $"SELECT COUNT(*) FROM read_parquet('{Path.Combine(root, "parse/*/image_references.parquet").Replace("'", "''")}') WHERE file_title IS NOT NULL";
        var count = Convert.ToInt64(expected.ExecuteScalar());
        using var actual = db.CreateCommand();
        actual.CommandText = $"SELECT COUNT(*) FROM ({SqlCatalogQueries.PageImages(root, 1)})";
        Assert.Equal(count, Convert.ToInt64(actual.ExecuteScalar()));
    }

    [Fact]
    public void CatalogInputsDeduplicateMatchRowsButKeepCrossWikiImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "creational-sql-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            Copy(db, "SELECT 10::BIGINT AS ott_id,'Life' AS name UNION ALL SELECT 12,'Species'",
                Path.Combine(root, "taxonomy_names.parquet"));
            Copy(db, "SELECT 12::BIGINT AS ott_id,100::BIGINT AS page_id,'exact' AS method UNION ALL SELECT 12,100,'exact'",
                Path.Combine(root, "matches.parquet"));
            Copy(db, "SELECT 'enwiki' AS source_wiki,'File:Same.jpg' AS file_title,100::BIGINT AS original_bytes,'enwiki-local' AS status,'20200101000000' AS uploaded_at,'a1' AS sha1_base36,'image/jpeg' AS mime,'BITMAP' AS media_type UNION ALL SELECT 'commonswiki','File:Same.jpg',200,'commons','20210101000000','b2','image/jpeg','BITMAP'",
                Path.Combine(root, "resolved_images.parquet"));

            Assert.Equal(2, Count(db, SqlCatalogQueries.TaxonNames(root, 1)));
            Assert.Equal(1, Count(db, SqlCatalogQueries.TaxonPages(root, 1, 1)));
            Assert.Equal(2, Count(db, SqlCatalogQueries.Images(root)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PinnedCatalogProjectionsHaveStableKeysAndReferentialCoverage()
    {
        var root = Environment.GetEnvironmentVariable("CREATIONAL_CATALOG_ROOT");
        var namesPath = Environment.GetEnvironmentVariable("CREATIONAL_TAXON_NAMES_PATH");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(namesPath)) return;
        using var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        var edges = SqlCatalogQueries.Edges(root);
        var names = SqlCatalogQueries.TaxonNamesFile(namesPath, 1);
        var pages = SqlCatalogQueries.Pages(root, 1);
        var links = SqlCatalogQueries.TaxonPages(root, 1, 1);
        var images = SqlCatalogQueries.Images(root);
        var references = SqlCatalogQueries.PageImages(root, 1);
        Assert.Equal(4_528_126, Count(db, edges));
        Assert.Equal(3_157_341, Count(db, names));
        Assert.Equal(503_735, Count(db, pages));
        Assert.Equal(400_545, Count(db, images));
        Assert.Equal(478_479, Count(db, references));
        Assert.Equal(0, Count(db, $"SELECT OttId FROM ({names}) GROUP BY OttId HAVING COUNT(*)>1"));
        Assert.Equal(0, Count(db, $"SELECT OttId,PageId FROM ({links}) GROUP BY OttId,PageId HAVING COUNT(*)>1"));
        Assert.Equal(0, Count(db, $"SELECT SourceWiki,CanonicalFileTitle FROM ({images}) GROUP BY 1,2 HAVING COUNT(*)>1"));
        Assert.Equal(0, Count(db, $"SELECT r.PageId FROM ({references}) r ANTI JOIN ({pages}) p USING (PageId)"));
        Assert.Equal(0, Count(db, $"SELECT l.OttId FROM ({links}) l ANTI JOIN ({edges}) e ON l.OttId=e.ott_id"));
        Assert.Equal(0, Count(db, $"SELECT l.PageId FROM ({links}) l ANTI JOIN ({pages}) p USING (PageId)"));
        Assert.Equal(0, Count(db, $"SELECT r.PageId FROM ({references}) r ANTI JOIN ({images}) i ON r.SourceWiki=i.SourceWiki AND r.FileTitle=i.CanonicalFileTitle WHERE r.SourceWiki IS NOT NULL"));
    }

    private static int Count(DuckDBConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({sql})";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Copy(DuckDBConnection db, string select, string path)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"COPY ({select}) TO '{path.Replace("'", "''")}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }
}
