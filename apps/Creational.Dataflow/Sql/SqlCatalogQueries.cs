namespace Creational.Dataflow.Sql;

public static class SqlCatalogQueries
{
    public static string Edges(string root) => $"""
        SELECT ott_id,parent_ott_id FROM read_parquet({File(root, "edges.parquet")}) ORDER BY ott_id
        """;

    public static string TaxonNames(string root, short treeId) => TaxonNamesFile(Path.Combine(root, "taxonomy_names.parquet"), treeId);

    public static string TaxonNamesFile(string path, short treeId) => $"""
        SELECT CAST({treeId} AS SMALLINT) AS TreeId,
               CAST(ott_id AS INTEGER) AS OttId,name AS Name
        FROM read_parquet({Quote(path)})
        ORDER BY OttId
        """;

    public static string TaxonPages(string root, short treeId, short collectionId) => $"""
        SELECT CAST({treeId} AS SMALLINT) AS TreeId,
               CAST(ott_id AS INTEGER) AS OttId,
               CAST({collectionId} AS SMALLINT) AS CollectionId,
               page_id AS PageId,MIN(method) AS MatchKind
        FROM read_parquet({File(root, "matches.parquet")})
        GROUP BY ott_id,page_id
        ORDER BY OttId,PageId
        """;

    public static string Images(string root) => $"""
        SELECT source_wiki AS SourceWiki,file_title AS CanonicalFileTitle,
               status AS MetadataStatus,original_bytes AS OriginalBytes,
               sha1_base36 AS SourceSha1,
               try_strptime(uploaded_at,'%Y%m%d%H%M%S') AS UploadedAt,
               mime AS Mime,media_type AS MediaType
        FROM read_parquet({File(root, "resolved_images.parquet")})
        WHERE source_wiki IS NOT NULL AND file_title IS NOT NULL
        QUALIFY row_number() OVER (
            PARTITION BY source_wiki,file_title
            ORDER BY uploaded_at DESC NULLS LAST,sha1_base36 DESC NULLS LAST
        )=1
        ORDER BY SourceWiki,CanonicalFileTitle
        """;

    public static string Pages(string root, short collectionId)
    {
        var articles = File(root, "parse/*/articles.parquet");
        var references = File(root, "parse/*/image_references.parquet");
        return $"""
            WITH unresolved AS (
                SELECT page_id,revision_id,COUNT(*) AS count
                FROM read_parquet({references})
                WHERE file_title IS NULL
                GROUP BY page_id,revision_id
            )
            SELECT CAST({collectionId} AS SMALLINT) AS CollectionId,
                   a.page_id AS PageId,a.title AS Title,a.revision_id AS RevisionId,
                   CAST(COALESCE(u.count,0) AS INTEGER) AS UnresolvedImageCount
            FROM read_parquet({articles}) a
            LEFT JOIN unresolved u USING (page_id,revision_id)
            ORDER BY a.page_id
            """;
    }

    public static string PageImages(string root, short collectionId)
    {
        var references = File(root, "parse/*/image_references.parquet");
        var resolved = File(root, "resolved_images.parquet");
        return $"""
            WITH image_refs AS (
                SELECT page_id,reference_index,source_kind,file_title,
                       upper(left(replace(substring(file_title,6),'_',' '),1)) ||
                       substring(replace(substring(file_title,6),'_',' '),2) AS canonical_key
                FROM read_parquet({references})
                WHERE file_title IS NOT NULL
            )
            SELECT CAST({collectionId} AS SMALLINT) AS CollectionId,
                   i.page_id AS PageId,i.reference_index AS ReferenceIndex,
                   COALESCE(r.file_title,i.file_title) AS FileTitle,
                   r.source_wiki AS SourceWiki,
                   CAST(NULL AS INTEGER) AS ImageId,i.source_kind AS Role
            FROM image_refs i
            LEFT JOIN read_parquet({resolved}) r USING (canonical_key)
            ORDER BY i.page_id,i.reference_index
            """;
    }

    private static string File(string root, string relative) => Quote(Path.Combine(root, relative));

    private static string Quote(string path) => "'" + path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'";
}
