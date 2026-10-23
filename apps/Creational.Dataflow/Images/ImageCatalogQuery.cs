using DuckDB.NET.Data;

namespace Creational.Dataflow.Images;

public sealed record ImageCandidate(string FileTitle, string ArticleTitle, string SourceKind);

public sealed record ImageCatalogPage(int PageCount, int CandidateCount, int UnresolvedCount,
    IReadOnlyList<ImageCandidate> Items)
{
    public long? KnownOriginalBytes { get; init; }
    public int? MetadataResolvedCount { get; init; }
    public int? MetadataMissingCount { get; init; }
    public int? MetadataExcludedCount { get; init; }
    public int? MetadataAmbiguousCount { get; init; }
}

public static class ImageCatalogQuery
{
    public static ImageCatalogPage Query(string root, ImageSelection selection, int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));

        string PathSql(string relative) => Quote(Path.Combine(root, relative).Replace('\\', '/'));
        var articles = PathSql("parse/*/articles.parquet");
        var images = PathSql("parse/*/image_references.parquet");
        var source = selection.Kind switch
        {
            ImageSelectionKind.WikipediaPage => $"""
                SELECT DISTINCT page_id,revision_id,title
                FROM read_parquet({articles})
                WHERE lower(title)=lower({Quote(selection.Value)})
                """,
            ImageSelectionKind.OttBranch => $"""
                WITH RECURSIVE descendants(ott_id) AS (
                    SELECT ott_id FROM read_parquet({PathSql("edges.parquet")})
                    WHERE ott_id={long.Parse(selection.Value)}
                    UNION
                    SELECT o.ott_id FROM read_parquet({PathSql("edges.parquet")}) o
                    JOIN descendants d ON o.parent_ott_id=d.ott_id
                )
                SELECT DISTINCT a.page_id,a.revision_id,a.title
                FROM descendants d
                JOIN read_parquet({PathSql("matches.parquet")}) m ON m.ott_id=d.ott_id
                JOIN read_parquet({articles}) a USING (page_id,revision_id)
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };

        using var db = new DuckDBConnection("Data Source=:memory:");
        db.Open();
        Execute(db, $"CREATE TEMP TABLE selected_pages AS {source}");
        var pageCount = Scalar(db, "SELECT count(*) FROM selected_pages");
        Execute(db, $"""
            CREATE TEMP TABLE selected_images AS
            SELECT i.file_title,s.title AS article_title,i.source_kind
            FROM selected_pages s JOIN read_parquet({images}) i USING (page_id,revision_id)
            """);
        var unresolved = Scalar(db, "SELECT count(*) FROM selected_images WHERE file_title IS NULL");
        Execute(db, """
            CREATE TEMP TABLE candidate_files AS
            SELECT file_title,article_title,source_kind FROM (
                SELECT *,row_number() OVER (
                    PARTITION BY upper(left(replace(file_title,'_',' '),6)) || substring(replace(file_title,'_',' '),7)
                    ORDER BY article_title,source_kind,file_title
                ) AS row_number
                FROM selected_images WHERE file_title IS NOT NULL
            ) WHERE row_number=1
            """);
        var candidateCount = Scalar(db, "SELECT count(*) FROM candidate_files");
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT file_title,article_title,source_kind
            FROM candidate_files
            ORDER BY lower(file_title),file_title
            LIMIT {limit} OFFSET {offset}
            """;
        using var reader = command.ExecuteReader();
        var items = new List<ImageCandidate>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        var page = new ImageCatalogPage(pageCount, candidateCount, unresolved, items);
        var metadata = Path.Combine(root, "resolved_images.parquet");
        if (!File.Exists(metadata)) return page;
        using var totals = db.CreateCommand();
        totals.CommandText = $"""
            SELECT CAST(coalesce(sum(CASE WHEN r.status IN ('enwiki-local','commons') THEN r.original_bytes END),0) AS BIGINT),
                   count(*) FILTER (WHERE r.status IN ('enwiki-local','commons')),
                   count(*) FILTER (WHERE r.status IS NULL OR r.status = 'missing'),
                   count(*) FILTER (WHERE r.status = 'unsupported-media'),
                   count(*) FILTER (WHERE r.status = 'ambiguous')
            FROM candidate_files c
            LEFT JOIN read_parquet({Quote(metadata.Replace('\\', '/'))}) r
              ON upper(left(replace(substring(c.file_title,6),'_',' '),1)) ||
                 substring(replace(substring(c.file_title,6),'_',' '),2)=r.canonical_key
            """;
        using var totalsReader = totals.ExecuteReader();
        totalsReader.Read();
        return page with
        {
            KnownOriginalBytes = Convert.ToInt64(totalsReader.GetValue(0)),
            MetadataResolvedCount = checked((int)Convert.ToInt64(totalsReader.GetValue(1))),
            MetadataMissingCount = checked((int)Convert.ToInt64(totalsReader.GetValue(2))),
            MetadataExcludedCount = checked((int)Convert.ToInt64(totalsReader.GetValue(3))),
            MetadataAmbiguousCount = checked((int)Convert.ToInt64(totalsReader.GetValue(4)))
        };
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    private static void Execute(DuckDBConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Scalar(DuckDBConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return checked((int)Convert.ToInt64(command.ExecuteScalar()));
    }
}
