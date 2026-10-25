using System.Data;
using System.Text.Json;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Sql;

public sealed class SqlTargetRepository(SqlConnectionFactory factory, string schema) : ITargetRepository
{
    private readonly string _table = $"[{SqlCatalogSchema.ValidateName(schema)}].[CollectionTargetState]";
    private readonly string _attemptTable = $"[{SqlCatalogSchema.ValidateName(schema)}].[CollectionTargetAttempt]";

    public async Task<TargetState?> GetTargetAsync(TargetId id, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"SELECT StateJson FROM {_table} WHERE CollectionName=@c AND Kind=@k AND TargetKey=@t", connection);
        AddId(command, id);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : Decode(json);
    }

    public async Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"SELECT StateJson FROM {_table} WHERE CollectionName=@c", connection);
        command.Parameters.Add("@c", SqlDbType.VarChar, 128).Value = collection;
        var result = new Dictionary<TargetId, TargetState>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var state = Decode(reader.GetString(0));
            result.Add(state.Id, state);
        }
        return result;
    }

    public async Task<bool> ImportIfMissingAsync(TargetState state, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = new SqlCommand($"""
                INSERT INTO {_table}(CollectionName,Kind,TargetKey,StateJson,Outcome,AttemptId)
                VALUES (@c,@k,@t,@j,@o,@a)
                """, connection, transaction);
            AddId(command, state.Id);
            command.Parameters.Add("@j", SqlDbType.VarChar, -1).Value = JsonSerializer.Serialize(state);
            command.Parameters.Add("@o", SqlDbType.VarChar, 32).Value = state.Outcome;
            command.Parameters.Add("@a", SqlDbType.VarChar, 64).Value = (object?)state.AttemptId ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
            if (state.AttemptId is not null)
                await RecordAttemptAsync(connection, transaction, state.Id, TargetState.Empty(state.Id), state, ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (SqlException error) when (error.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }
    }

    public Task<TargetState> MarkStaleAsync(TargetId id, string reason, CancellationToken ct) =>
        MutateAsync(id, state => state.MarkStale(reason), ct);

    public Task<TargetState> BeginAttemptAsync(TargetId id, string attemptId, long expectedStaleRevision, CancellationToken ct) =>
        MutateAsync(id, state => state.BeginAttempt(attemptId, expectedStaleRevision), ct);

    public Task<TargetState> QueueAttemptAsync(TargetId id, string attemptId, long expectedStaleRevision,
        string inputManifestUri, CancellationToken ct) =>
        MutateAsync(id, state => state.QueueAttempt(attemptId, expectedStaleRevision, inputManifestUri), ct);

    public Task<TargetState> NoteEnqueuedAsync(TargetId id, string attemptId, CancellationToken ct) =>
        MutateAsync(id, state => state.NoteEnqueued(attemptId), ct);

    public Task<TargetState> BeginLaunchAsync(TargetId id, string attemptId, CancellationToken ct) =>
        MutateAsync(id, state => state.BeginLaunch(attemptId), ct);

    public Task<TargetState> MarkLaunchUncertainAsync(TargetId id, string attemptId, CancellationToken ct) =>
        MutateAsync(id, state => state.MarkLaunchUncertain(attemptId), ct);

    public Task<TargetState> AttachExecutionAsync(TargetId id, string attemptId, string executionId, CancellationToken ct) =>
        MutateAsync(id, state => state.AttachExecution(attemptId, executionId), ct);

    public Task<TargetState> CompleteAttemptAsync(TargetId id, string attemptId, long capturedStaleRevision,
        string manifestPath, string fingerprint, string recipeVersion, IReadOnlyDictionary<string, string> inputs,
        CancellationToken ct, string? warning = null) =>
        MutateAsync(id, state => state.CompleteAttempt(attemptId, capturedStaleRevision,
            manifestPath, fingerprint, recipeVersion, inputs, warning), ct);

    public Task<TargetState> FailAttemptAsync(TargetId id, string attemptId, string error, CancellationToken ct) =>
        MutateAsync(id, state => state.FailAttempt(attemptId, error), ct);

    private async Task<TargetState> MutateAsync(TargetId id, Func<TargetState, TargetState> change, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        for (var retry = 0; retry < 8; retry++)
        {
            TargetState current;
            byte[]? version;
            await using (var read = new SqlCommand($"SELECT StateJson, Version FROM {_table} WHERE CollectionName=@c AND Kind=@k AND TargetKey=@t", connection))
            {
                AddId(read, id);
                await using var reader = await read.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    current = Decode(reader.GetString(0));
                    version = (byte[])reader[1];
                }
                else
                {
                    current = TargetState.Empty(id);
                    version = null;
                }
            }
            var next = change(current);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                await using var write = new SqlCommand(version is null
                    ? $"INSERT INTO {_table}(CollectionName,Kind,TargetKey,StateJson,Outcome,AttemptId) VALUES (@c,@k,@t,@j,@o,@a)"
                    : $"UPDATE {_table} SET StateJson=@j,Outcome=@o,AttemptId=@a,UpdatedAt=SYSUTCDATETIME() WHERE CollectionName=@c AND Kind=@k AND TargetKey=@t AND Version=@v",
                    connection, transaction);
                AddId(write, id);
                write.Parameters.Add("@j", SqlDbType.VarChar, -1).Value = JsonSerializer.Serialize(next);
                write.Parameters.Add("@o", SqlDbType.VarChar, 32).Value = next.Outcome;
                write.Parameters.Add("@a", SqlDbType.VarChar, 64).Value = (object?)next.AttemptId ?? DBNull.Value;
                if (version is not null) write.Parameters.Add("@v", SqlDbType.Binary, 8).Value = version;
                if (await write.ExecuteNonQueryAsync(ct) != 1)
                {
                    await transaction.RollbackAsync(ct);
                    continue;
                }
                await RecordAttemptAsync(connection, transaction, id, current, next, ct);
                await transaction.CommitAsync(ct);
                return next;
            }
            catch (SqlException error) when (error.Number is 2601 or 2627)
            {
                await transaction.RollbackAsync(ct);
            }
        }
        throw new InvalidOperationException($"Could not update {id} after eight SQL revision conflicts");
    }

    private async Task RecordAttemptAsync(SqlConnection connection, SqlTransaction transaction, TargetId id,
        TargetState previous, TargetState next, CancellationToken ct)
    {
        var attemptId = next.AttemptId ?? next.LastAttemptId;
        if (attemptId is null ||
            previous.AttemptId == next.AttemptId && previous.Outcome == next.Outcome && previous.ExecutionId == next.ExecutionId)
            return;
        await using var command = new SqlCommand($"""
            UPDATE {_attemptTable} SET Outcome=@o,ExecutionId=@e,
                FinishedAt=CASE WHEN @active=0 THEN SYSUTCDATETIME() ELSE NULL END,Error=@error
            WHERE AttemptId=@a;
            IF @@ROWCOUNT=0
                INSERT INTO {_attemptTable}(AttemptId,CollectionName,Kind,TargetKey,Outcome,ExecutionId,StartedAt,FinishedAt,Error)
                VALUES (@a,@c,@k,@t,@o,@e,@started,
                    CASE WHEN @active=0 THEN SYSUTCDATETIME() ELSE NULL END,@error);
            """, connection, transaction);
        AddId(command, id);
        command.Parameters.Add("@a", SqlDbType.VarChar, 64).Value = attemptId;
        command.Parameters.Add("@o", SqlDbType.VarChar, 32).Value = next.Outcome;
        command.Parameters.Add("@e", SqlDbType.VarChar, 128).Value = (object?)next.ExecutionId ?? DBNull.Value;
        command.Parameters.Add("@active", SqlDbType.Bit).Value = next.AttemptId is not null;
        command.Parameters.Add("@error", SqlDbType.VarChar, 1024).Value = (object?)next.Error ?? DBNull.Value;
        command.Parameters.Add("@started", SqlDbType.DateTime2).Value =
            (next.AttemptStartedAt ?? previous.AttemptStartedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static TargetState Decode(string json) =>
        JsonSerializer.Deserialize<TargetState>(json) ?? throw new InvalidDataException("Empty SQL target state");

    private static void AddId(SqlCommand command, TargetId id)
    {
        command.Parameters.Add("@c", SqlDbType.VarChar, 128).Value = id.Collection;
        command.Parameters.Add("@k", SqlDbType.VarChar, 80).Value = id.Kind;
        command.Parameters.Add("@t", SqlDbType.VarChar, 400).Value = id.Key;
    }
}
