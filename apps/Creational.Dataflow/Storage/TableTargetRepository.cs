using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Storage;

public sealed class TableTargetRepository(TableClient table) : ITargetRepository
{
    private static string Partition(string collection) => Uri.EscapeDataString(collection);
    private static string Row(TargetId id) => Uri.EscapeDataString(id.Kind) + "|" + Uri.EscapeDataString(id.Key);

    public async Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        var states = new Dictionary<TargetId, TargetState>();
        try
        {
            await foreach (var entity in table.QueryAsync<TableEntity>(e => e.PartitionKey == Partition(collection), cancellationToken: cancellationToken))
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
            LastAttemptId = Optional("LastAttemptId")
        };
    }
}
