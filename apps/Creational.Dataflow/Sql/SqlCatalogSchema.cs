using System.Reflection;
using System.Text.RegularExpressions;

namespace Creational.Dataflow.Sql;

public static partial class SqlCatalogSchema
{
    public static string Render(string schema)
    {
        if (!SchemaName().IsMatch(schema))
            throw new ArgumentException("Expected a simple SQL schema name.", nameof(schema));
        const string resourceName = "Creational.Dataflow.Sql.Migrations.001_catalog.sql";
        using var stream = typeof(SqlCatalogSchema).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded migration {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__SCHEMA__", schema, StringComparison.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
