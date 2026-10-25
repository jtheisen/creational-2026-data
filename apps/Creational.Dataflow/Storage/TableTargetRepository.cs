using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Storage;

public sealed class TableTargetRepository(TableClient table) : ITargetRepository
{
    private static string Partition(string collection) => Uri.EscapeDataString(collection);
    private static string Row(TargetId id) => Uri.EscapeDataString(id.Kind) + "|" + Uri.EscapeDataString(id.Key);

    public async Task<TargetState?> GetTargetAsync(TargetId id, CancellationToken cancellationToken)
    {
        var response = await table.GetEntityIfExistsAsync<TableEntity>(Partition(id.Collection), Row(id),
            cancellationToken: cancellationToken);
        return response.HasValue ? Decode(response.Value!, id.Collection) : null;
    }

    public async Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        var states = new Dictionary<TargetId, TargetState>();
        try
        {
            var partition = Partition(collection).Replace("'", "''", StringComparison.Ordinal);
            // The SDK's expression binder fails under the App Service runtime's compilation settings.
            await foreach (var entity in table.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partition}'",
                cancellationToken: cancellationToken))
            {
                var state = Decode(entity, collection);
                states[state.Id] = state;
            }
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { }
        return states;
    }

    public Task<TargetState> MarkStaleAsync(TargetId id, string reason, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.MarkStale(reason), cancellationToken);

    public Task<TargetState> BeginAttemptAsync(TargetId id, string attemptId, long expectedStaleRevision, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.BeginAttempt(attemptId, expectedStaleRevision), cancellationToken);

    public Task<TargetState> QueueAttemptAsync(TargetId id, string attemptId, long expectedStaleRevision, string inputManifestUri, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.QueueAttempt(attemptId, expectedStaleRevision, inputManifestUri), cancellationToken);

    public Task<TargetState> NoteEnqueuedAsync(TargetId id, string attemptId, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.NoteEnqueued(attemptId), cancellationToken);

    public Task<TargetState> BeginLaunchAsync(TargetId id, string attemptId, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.BeginLaunch(attemptId), cancellationToken);

    public Task<TargetState> MarkLaunchUncertainAsync(TargetId id, string attemptId, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.MarkLaunchUncertain(attemptId), cancellationToken);

    public Task<TargetState> AttachExecutionAsync(TargetId id, string attemptId, string executionId, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.AttachExecution(attemptId, executionId), cancellationToken);

    public Task<TargetState> CompleteAttemptAsync(TargetId id, string attemptId, long capturedStaleRevision,
        string manifestPath, string fingerprint, string recipeVersion, IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken, string? warning = null) =>
        MutateAsync(id, state => state.CompleteAttempt(attemptId, capturedStaleRevision,
            manifestPath, fingerprint, recipeVersion, inputs, warning), cancellationToken);

    public Task<TargetState> FailAttemptAsync(TargetId id, string attemptId, string error, CancellationToken cancellationToken) =>
        MutateAsync(id, state => state.FailAttempt(attemptId, error), cancellationToken);

    private async Task<TargetState> MutateAsync(TargetId id, Func<TargetState, TargetState> change, CancellationToken ct)
    {
        for (var retry = 0; retry < 8; retry++)
        {
            var response = await table.GetEntityIfExistsAsync<TableEntity>(Partition(id.Collection), Row(id), cancellationToken: ct);
            var current = response.HasValue ? Decode(response.Value!, id.Collection) : TargetState.Empty(id);
            var updated = change(current);
            var entity = Encode(updated);
            try
            {
                if (response.HasValue)
                    await table.UpdateEntityAsync(entity, response.Value!.ETag, TableUpdateMode.Replace, ct);
                else
                    await table.AddEntityAsync(entity, ct);
                return updated;
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                // Another updater won the ETag race; re-read before retrying.
            }
        }
        throw new InvalidOperationException($"Could not update {id} after eight ETag conflicts");
    }

    private static TableEntity Encode(TargetState state) => new(Partition(state.Id.Collection), Row(state.Id))
    {
        ["Kind"] = state.Id.Kind,
        ["Key"] = state.Id.Key,
        ["StaleRevision"] = state.StaleRevision,
        ["SatisfiedStaleRevision"] = state.SatisfiedStaleRevision,
        ["StaleReason"] = state.StaleReason ?? "",
        ["AttemptId"] = state.AttemptId ?? "",
        ["AttemptRevision"] = state.AttemptRevision ?? -1,
        ["AttemptStartedAt"] = state.AttemptStartedAt?.ToString("O") ?? "",
        ["ExecutionId"] = state.ExecutionId ?? "",
        ["LastAttemptId"] = state.LastAttemptId ?? "",
        ["InputManifestUri"] = state.InputManifestUri ?? "",
        ["LastEnqueuedAt"] = state.LastEnqueuedAt?.ToString("O") ?? "",
        ["ManifestPath"] = state.ManifestPath ?? "",
        ["Fingerprint"] = state.Fingerprint ?? "",
        ["RecipeVersion"] = state.RecipeVersion ?? "",
        ["InputFingerprints"] = JsonSerializer.Serialize(state.InputFingerprints),
        ["Outcome"] = state.Outcome,
        ["Error"] = state.Error ?? ""
    };

    private static TargetState Decode(TableEntity entity, string collection)
    {
        string? Optional(string name) => entity.GetString(name) is { Length: > 0 } value ? value : null;
        var attemptRevision = entity.GetInt64("AttemptRevision");
        var started = Optional("AttemptStartedAt");
        return new TargetState(new(collection, entity.GetString("Kind")!, entity.GetString("Key")!),
            entity.GetInt64("StaleRevision") ?? 0, entity.GetInt64("SatisfiedStaleRevision") ?? 0,
            Optional("StaleReason"), Optional("AttemptId"), attemptRevision < 0 ? null : attemptRevision,
            Optional("ManifestPath"), Optional("Fingerprint"), Optional("RecipeVersion"),
            JsonSerializer.Deserialize<Dictionary<string, string>>(entity.GetString("InputFingerprints") ?? "{}")!,
            entity.GetString("Outcome") ?? "missing", Optional("Error"))
        {
            AttemptStartedAt = started is null ? null : DateTimeOffset.Parse(started),
            ExecutionId = Optional("ExecutionId"),
            LastAttemptId = Optional("LastAttemptId"),
            InputManifestUri = Optional("InputManifestUri"),
            LastEnqueuedAt = Optional("LastEnqueuedAt") is { } enqueued ? DateTimeOffset.Parse(enqueued) : null
        };
    }
}
