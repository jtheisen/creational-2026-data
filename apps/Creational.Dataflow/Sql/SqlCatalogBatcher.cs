using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace Creational.Dataflow.Sql;

public sealed record SqlCatalogBatch(DataTable Rows, long NextOrdinal);

public static class SqlCatalogBatcher
{
    public static async IAsyncEnumerable<SqlCatalogBatch> ReadAsync(DbDataReader reader, int batchSize,
        long startOrdinal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(startOrdinal);
        var template = new DataTable();
        for (var i = 0; i < reader.FieldCount; i++)
            template.Columns.Add(reader.GetName(i), reader.GetFieldType(i));

        var rows = template.Clone();
        long ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (ordinal++ < startOrdinal) continue;
            var row = rows.NewRow();
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            rows.Rows.Add(row);
            if (rows.Rows.Count < batchSize) continue;
            yield return new SqlCatalogBatch(rows, ordinal);
            rows = template.Clone();
        }
        if (rows.Rows.Count > 0) yield return new SqlCatalogBatch(rows, ordinal);
    }
}
