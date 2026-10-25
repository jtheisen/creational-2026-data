using System.Data;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Sql;

public sealed record SqlCatalogTableSize(string Name, long Rows, decimal ReservedMiB, decimal UsedMiB);
public sealed record SqlCatalogIndexSize(string Table, string Name, int IndexId, string Compression,
    decimal ReservedMiB, decimal UsedMiB);
public sealed record SqlCatalogTextColumn(string Table, string Column, string DataType,
    int DeclaredBytes, string Collation, int? MaxUtf8Bytes, int? SafeUtf8Bytes);
public sealed record SqlCatalogCapacityReport(decimal FileAllocatedMiB, decimal? FileMaximumMiB,
    IReadOnlyList<SqlCatalogTableSize> Tables, IReadOnlyList<SqlCatalogIndexSize> Indexes,
    IReadOnlyList<SqlCatalogTextColumn> TextColumns);
public sealed record SqlPageEstimate(string Table, int IndexId, long CurrentKiB, long EstimatedPageKiB);

public static class SqlCatalogCapacity
{
    public static async Task<SqlCatalogCapacityReport> ReadAsync(SqlConnection connection, string schema,
        CancellationToken ct, Action<string>? progress = null)
    {
        SqlCatalogSchema.ValidateName(schema);
        var tables = new List<SqlCatalogTableSize>();
        var indexes = new List<SqlCatalogIndexSize>();
        var columns = new List<SqlCatalogTextColumn>();

        await using (var size = new SqlCommand("""
            SELECT SUM(CASE WHEN type_desc='ROWS' THEN size ELSE 0 END)*8.0/1024,
                   SUM(CASE WHEN type_desc='ROWS' AND max_size>0 THEN max_size ELSE 0 END)*8.0/1024
            FROM sys.database_files
            """, connection))
        {
            await using var reader = await size.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var allocated = reader.GetDecimal(0);
            decimal? maximum = reader.IsDBNull(1) || reader.GetDecimal(1) == 0 ? null : reader.GetDecimal(1);
            await reader.CloseAsync();

            await using (var command = new SqlCommand("""
                SELECT t.name,COALESCE(i.name,''),i.index_id,p.data_compression_desc,
                       SUM(d.row_count) AS rows,
                       SUM(d.reserved_page_count)*8.0/1024 AS reserved_mib,
                       SUM(d.used_page_count)*8.0/1024 AS used_mib
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.indexes i ON i.object_id=t.object_id
                JOIN sys.partitions p ON p.object_id=i.object_id AND p.index_id=i.index_id
                JOIN sys.dm_db_partition_stats d ON d.object_id=p.object_id
                    AND d.index_id=p.index_id AND d.partition_number=p.partition_number
                WHERE s.name=@schema
                GROUP BY t.name,i.name,i.index_id,p.data_compression_desc
                ORDER BY t.name,i.index_id
                """, connection))
            {
                command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
                await using var rows = await command.ExecuteReaderAsync(ct);
                var grouped = new Dictionary<string, (long Rows, decimal Reserved, decimal Used)>(StringComparer.Ordinal);
                while (await rows.ReadAsync(ct))
                {
                    var table = rows.GetString(0);
                    var indexId = rows.GetInt32(2);
                    var reserved = rows.GetDecimal(5);
                    var used = rows.GetDecimal(6);
                    indexes.Add(new(table, rows.GetString(1), indexId, rows.GetString(3), reserved, used));
                    grouped.TryGetValue(table, out var total);
                    grouped[table] = (indexId <= 1 ? rows.GetInt64(4) : total.Rows,
                        total.Reserved + reserved, total.Used + used);
                }
                tables.AddRange(grouped.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new SqlCatalogTableSize(x.Key, x.Value.Rows, x.Value.Reserved, x.Value.Used)));
            }

            await using var metadata = new SqlCommand("""
                SELECT t.name,c.name,ty.name,c.max_length,c.collation_name
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.columns c ON c.object_id=t.object_id
                JOIN sys.types ty ON ty.user_type_id=c.user_type_id
                WHERE s.name=@schema AND c.system_type_id IN (167,175,231,239)
                ORDER BY t.name,c.column_id
                """, connection);
            metadata.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
            await using var textColumns = await metadata.ExecuteReaderAsync(ct);
            while (await textColumns.ReadAsync(ct))
            {
                var dataType = textColumns.GetString(2);
                var declaredBytes = textColumns.GetInt16(3);
                var collation = textColumns.GetString(4);
                int? safeBytes = declaredBytes < 0 ? null : dataType is "nvarchar" or "nchar"
                    ? checked(declaredBytes / 2 * 3)
                    : collation.EndsWith("_UTF8", StringComparison.OrdinalIgnoreCase)
                        ? declaredBytes : checked(declaredBytes * 3);
                columns.Add(new(textColumns.GetString(0), textColumns.GetString(1),
                    dataType, declaredBytes, collation, null, safeBytes));
            }
            await textColumns.CloseAsync();

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (tables.Single(x => x.Name == column.Table).Rows > 250_000)
                {
                    progress?.Invoke($"Using safe declared bound for {column.Table}.{column.Column}");
                    continue;
                }
                progress?.Invoke($"Checking {column.Table}.{column.Column}");
                var tableIdentifier = Quote(column.Table);
                var columnIdentifier = Quote(column.Column);
                var targetType = column.DeclaredBytes > 4000 ? "VARCHAR(MAX)" : "VARCHAR(8000)";
                await using var bound = new SqlCommand($"""
                    SELECT MAX(DATALENGTH(CONVERT({targetType}, {columnIdentifier}
                        COLLATE Latin1_General_100_CI_AS_SC_UTF8)))
                    FROM {Quote(schema)}.{tableIdentifier}
                    """, connection) { CommandTimeout = 120 };
                var value = await bound.ExecuteScalarAsync(ct);
                columns[i] = column with { MaxUtf8Bytes = value is DBNull or null ? null : Convert.ToInt32(value) };
            }
            return new(allocated, maximum, tables, indexes, columns);
        }
    }

    public static async Task<SqlPageEstimate> EstimatePageAsync(SqlConnection connection, string schema,
        string table, int indexId, CancellationToken ct)
    {
        SqlCatalogSchema.ValidateName(schema);
        if (indexId < 0) throw new ArgumentOutOfRangeException(nameof(indexId));
        await using var command = new SqlCommand("sys.sp_estimate_data_compression_savings", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 300
        };
        command.Parameters.Add("@schema_name", SqlDbType.NVarChar, 128).Value = schema;
        command.Parameters.Add("@object_name", SqlDbType.NVarChar, 128).Value = table;
        command.Parameters.Add("@index_id", SqlDbType.Int).Value = indexId;
        command.Parameters.Add("@partition_number", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@data_compression", SqlDbType.NVarChar, 60).Value = "PAGE";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"No PAGE estimate for {schema}.{table} index {indexId}.");
        return new(table, indexId, Convert.ToInt64(reader.GetValue(4)), Convert.ToInt64(reader.GetValue(5)));
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
