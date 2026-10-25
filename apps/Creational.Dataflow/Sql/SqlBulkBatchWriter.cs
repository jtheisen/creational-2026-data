using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Sql;

public sealed record SqlCatalogCheckpoint(long NextOrdinal, bool Completed);
public sealed record SqlCatalogBatchTransform(string TemporaryTable, string AfterBulkSql);

public sealed class SqlBulkBatchWriter(string schema)
{
    private readonly string _schema = Identifier(schema);

    public async Task<SqlCatalogCheckpoint> GetCheckpointAsync(SqlConnection connection, string loadKey,
        string sourceFingerprint, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT SourceFingerprint,NextOrdinal,Completed FROM [{_schema}].CatalogLoadCheckpoint WHERE LoadKey=@key", connection);
        command.Parameters.Add("@key", SqlDbType.VarChar, 768).Value = loadKey;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(0, false);
        ValidateFingerprint(reader.GetString(0), sourceFingerprint);
        return new(reader.GetInt64(1), reader.GetBoolean(2));
    }

    public async Task WriteAsync(SqlConnection connection, string table, string loadKey,
        string sourceFingerprint, SqlCatalogBatch batch, CancellationToken cancellationToken,
        SqlCatalogBatchTransform? transform = null)
    {
        var tableName = Identifier(table);
        var destination = transform is null ? $"[{_schema}].[{tableName}]" : TemporaryIdentifier(transform.TemporaryTable);
        if (batch.Rows.Rows.Count == 0) throw new ArgumentException("Bulk batch is empty.", nameof(batch));
        var startOrdinal = checked(batch.NextOrdinal - batch.Rows.Rows.Count);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var current = new SqlCommand($"SELECT SourceFingerprint,NextOrdinal,Completed FROM [{_schema}].CatalogLoadCheckpoint WITH (UPDLOCK,HOLDLOCK) WHERE LoadKey=@key", connection, transaction);
            current.Parameters.Add("@key", SqlDbType.VarChar, 768).Value = loadKey;
            string? recordedFingerprint = null;
            long recordedOrdinal = 0;
            var completed = false;
            await using (var reader = await current.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    recordedFingerprint = reader.GetString(0);
                    recordedOrdinal = reader.GetInt64(1);
                    completed = reader.GetBoolean(2);
                }
            }
            if (recordedFingerprint is not null) ValidateFingerprint(recordedFingerprint, sourceFingerprint);
            if (completed && recordedOrdinal != batch.NextOrdinal)
                throw new InvalidOperationException($"Catalog load {loadKey} is already complete.");
            if (recordedOrdinal == batch.NextOrdinal)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
            if (recordedOrdinal != startOrdinal)
                throw new InvalidOperationException($"Checkpoint for {loadKey} is {recordedOrdinal}, but batch starts at {startOrdinal}.");

            using (var bulk = new SqlBulkCopy(connection,
                SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock,
                transaction)
            {
                DestinationTableName = destination,
                BatchSize = batch.Rows.Rows.Count,
                BulkCopyTimeout = 300,
                EnableStreaming = true
            })
            {
                foreach (DataColumn column in batch.Rows.Columns)
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                using var source = batch.Rows.CreateDataReader();
                await bulk.WriteToServerAsync(source, cancellationToken);
            }
            if (transform is not null)
            {
                await using var finish = new SqlCommand(transform.AfterBulkSql, connection, transaction)
                {
                    CommandTimeout = 300
                };
                await finish.ExecuteNonQueryAsync(cancellationToken);
            }

            var sql = recordedFingerprint is null
                ? $"INSERT INTO [{_schema}].CatalogLoadCheckpoint(LoadKey,SourceFingerprint,NextOrdinal) VALUES (@key,@fingerprint,@next)"
                : $"UPDATE [{_schema}].CatalogLoadCheckpoint SET NextOrdinal=@next,UpdatedAt=SYSUTCDATETIME() WHERE LoadKey=@key";
            await using var checkpoint = new SqlCommand(sql, connection, transaction);
            checkpoint.Parameters.Add("@key", SqlDbType.VarChar, 768).Value = loadKey;
            if (recordedFingerprint is null)
                checkpoint.Parameters.Add("@fingerprint", SqlDbType.VarChar, 64).Value = sourceFingerprint;
            checkpoint.Parameters.AddWithValue("@next", batch.NextOrdinal);
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (InvalidOperationException) { } // Commit or server-side abort already ended the transaction.
            catch (SqlException) { } // Preserve the original write failure.
            throw;
        }
    }

    public async Task MarkCompletedAsync(SqlConnection connection, string loadKey, string sourceFingerprint,
        long nextOrdinal, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"""
            UPDATE [{_schema}].CatalogLoadCheckpoint
            SET Completed=1,UpdatedAt=SYSUTCDATETIME()
            WHERE LoadKey=@key AND SourceFingerprint=@fingerprint AND NextOrdinal=@next;
            IF @@ROWCOUNT=0 AND @next=0 AND NOT EXISTS
                (SELECT 1 FROM [{_schema}].CatalogLoadCheckpoint WHERE LoadKey=@key)
                INSERT INTO [{_schema}].CatalogLoadCheckpoint
                    (LoadKey,SourceFingerprint,NextOrdinal,Completed)
                VALUES (@key,@fingerprint,0,1);
            """, connection);
        command.Parameters.Add("@key", SqlDbType.VarChar, 768).Value = loadKey;
        command.Parameters.Add("@fingerprint", SqlDbType.VarChar, 64).Value = sourceFingerprint;
        command.Parameters.AddWithValue("@next", nextOrdinal);
        if (await command.ExecuteNonQueryAsync(cancellationToken) < 1)
            throw new InvalidOperationException($"Cannot complete {loadKey}: source or checkpoint differs.");
    }

    private static string Identifier(string value) => Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant) ? value : throw new ArgumentException("Invalid SQL identifier.");

    private static string TemporaryIdentifier(string value) => Regex.IsMatch(value, "^#[A-Za-z][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant) ? value : throw new ArgumentException("Invalid SQL temporary table name.");

    private static void ValidateFingerprint(string recorded, string requested)
    {
        if (!recorded.Equals(requested, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Catalog source fingerprint changed during a resumable load.");
    }
}
