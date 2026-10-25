using System.Reflection;
using System.Text.RegularExpressions;

namespace Creational.Dataflow.Sql;

public static partial class SqlCatalogSchema
{
    public static string ValidateName(string schema) => SchemaName().IsMatch(schema)
        ? schema : throw new ArgumentException("Expected a simple SQL schema name.", nameof(schema));

    public static string Render(string schema)
        => RenderResource(schema, "001_catalog.sql");

    public static string RenderLegacy(string schema)
        => RenderResource(schema, "000_catalog_legacy.sql");

    public static string RenderUtf8Conversion(string schema)
        => RenderResource(schema, "003_utf8_catalog.sql");

    public static IReadOnlyList<string> RenderUtf8ConversionSteps(string schema)
        => RenderUtf8Conversion(schema).ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string RenderPageCompression(string schema)
        => RenderResource(schema, "004_page_compression.sql");

    public static string RenderCompactImages(string schema)
        => RenderResource(schema, "005_compact_images.sql");

    public static string RenderImageBatchTable() => """
        CREATE TABLE #CatalogImageBatch (
            SourceWiki VARCHAR(16) NOT NULL,
            CanonicalFileTitle VARCHAR(1536) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
            MetadataStatus VARCHAR(32) NOT NULL,
            OriginalBytes BIGINT NULL,
            SourceSha1 VARCHAR(40) NULL,
            UploadedAt DATETIME2(0) NULL,
            Mime VARCHAR(128) NULL,
            MediaType VARCHAR(32) NULL
        )
        """;

    public static IReadOnlyList<string> RenderPageCompressionSteps(string schema)
        => RenderPageCompression(schema).ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string RenderTaxonPageDfsColumn(string schema)
        => RenderResource(schema, "002_taxon_page_dfs.sql");

    private static string RenderResource(string schema, string fileName)
    {
        ValidateName(schema);
        var resourceName = "Creational.Dataflow.Sql.Migrations." + fileName;
        using var stream = typeof(SqlCatalogSchema).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded migration {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__SCHEMA__", schema, StringComparison.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
