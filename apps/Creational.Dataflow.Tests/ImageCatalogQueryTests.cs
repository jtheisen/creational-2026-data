using Creational.Dataflow.Images;
using DuckDB.NET.Data;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ImageCatalogQueryTests
{
    [Fact]
    public void OttBranchIncludesDescendantsAndDeduplicatesFiles()
    {
        using var fixture = new CatalogFixture();
        var page = ImageCatalogQuery.Query(fixture.Root,
            ImageSelection.Parse("10"), 0, 10);

        Assert.Equal(2, page.PageCount);
        Assert.Equal(2, page.CandidateCount);
        Assert.Equal(1, page.UnresolvedCount);
        Assert.Equal(["File:A.jpg", "File:B.jpg"], page.Items.Select(x => x.FileTitle));
    }

    [Fact]
    public void WikipediaPageIsOneArticleAndUnknownOttIdIsEmpty()
    {
        using var fixture = new CatalogFixture();
        var wiki = ImageCatalogQuery.Query(fixture.Root,
            ImageSelection.Parse("https://en.wikipedia.org/wiki/Child_species"), 0, 10);
        var unknown = ImageCatalogQuery.Query(fixture.Root, ImageSelection.Parse("999"), 0, 10);

        Assert.Equal(1, wiki.PageCount);
        Assert.Equal(["File:A.jpg", "File:B.jpg"], wiki.Items.Select(x => x.FileTitle));
        Assert.Empty(unknown.Items);
        Assert.Equal(0, unknown.CandidateCount);
    }

    [Fact]
    public void KnownOriginalBytesCountEachResolvedFileOnce()
    {
        using var fixture = new CatalogFixture();
        fixture.AddResolvedMetadata();

        var page = ImageCatalogQuery.Query(fixture.Root, ImageSelection.Parse("10"), 0, 10);

        Assert.Equal(125, page.KnownOriginalBytes);
        Assert.Equal(1, page.MetadataResolvedCount);
        Assert.Equal(1, page.MetadataMissingCount);
        Assert.Equal(0, page.MetadataAmbiguousCount);
    }

    [Fact]
    public void UnsupportedMediaDoesNotContributeToImageSize()
    {
        using var fixture = new CatalogFixture();
        fixture.AddResolvedMetadata("unsupported-media", 1000000);

        var page = ImageCatalogQuery.Query(fixture.Root, ImageSelection.Parse("10"), 0, 10);

        Assert.Equal(125, page.KnownOriginalBytes);
        Assert.Equal(1, page.MetadataResolvedCount);
        Assert.Equal(0, page.MetadataMissingCount);
        Assert.Equal(1, page.MetadataExcludedCount);
        Assert.Equal(0, page.MetadataAmbiguousCount);
    }

    [Fact]
    public void FilenameCaseAfterFirstLetterRemainsDistinct()
    {
        using var fixture = new CatalogFixture();
        fixture.AddCaseVariant();

        var page = ImageCatalogQuery.Query(fixture.Root, ImageSelection.Parse("10"), 0, 10);

        Assert.Equal(3, page.CandidateCount);
        Assert.Contains(page.Items, item => item.FileTitle == "File:A.jpg");
        Assert.Contains(page.Items, item => item.FileTitle == "File:A.JPG");
    }

    private sealed class CatalogFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "creational-image-test-" + Guid.NewGuid().ToString("N"));

        public CatalogFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "parse", "one"));
            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            Write(db, "edges.parquet", "SELECT * FROM (VALUES (10, NULL), (11, 10), (20, NULL)) t(ott_id,parent_ott_id)");
            Write(db, "matches.parquet", "SELECT * FROM (VALUES (1,100,10), (2,200,11), (3,300,20)) t(page_id,revision_id,ott_id)");
            Write(db, "parse/one/articles.parquet", "SELECT * FROM (VALUES (1,100,'Parent'), (2,200,'Child species'), (3,300,'Other')) t(page_id,revision_id,title)");
            Write(db, "parse/one/image_references.parquet", "SELECT * FROM (VALUES (1,100,'taxobox_parameter','File:A.jpg','A'), (2,200,'file_link','File:A.jpg','A'), (2,200,'taxobox_parameter','File:B.jpg','B'), (2,200,'gallery_entry',NULL,'{{bad}}'), (3,300,'file_link','File:C.jpg','C')) t(page_id,revision_id,source_kind,file_title,raw_value)");
        }

        public void AddResolvedMetadata(string secondStatus = "missing", long? secondBytes = null)
        {
            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            Write(db, "resolved_images.parquet", $"SELECT * FROM (VALUES ('A.jpg',125,'enwiki-local'), ('B.jpg',{(secondBytes is null ? "NULL" : secondBytes.ToString())},'{secondStatus}')) t(canonical_key,original_bytes,status)");
        }

        public void AddCaseVariant()
        {
            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            var current = Path.Combine(Root, "parse/one/image_references.parquet").Replace("'", "''");
            Write(db, "parse/one/variant.parquet", $"SELECT * FROM read_parquet('{current}') UNION ALL SELECT 2,200,'file_link','File:A.JPG','A'");
            File.Move(Path.Combine(Root, "parse/one/variant.parquet"), Path.Combine(Root, "parse/one/image_references.parquet"), true);
        }

        private void Write(DuckDBConnection db, string relative, string select)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"COPY ({select}) TO '{Path.Combine(Root, relative).Replace("'", "''")}' (FORMAT PARQUET)";
            cmd.ExecuteNonQuery();
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
