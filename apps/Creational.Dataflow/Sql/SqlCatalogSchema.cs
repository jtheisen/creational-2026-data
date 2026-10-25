using System.Reflection;
using System.Text.RegularExpressions;

namespace Creational.Dataflow.Sql;

public static partial class SqlCatalogSchema
{
    public static string ValidateName(string schema) => SchemaName().IsMatch(schema)
        ? schema : throw new ArgumentException("Expected a simple SQL schema name.", nameof(schema));

    public static string Render(string schema)
        => RenderResource(schema, "001_catalog.sql");

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
