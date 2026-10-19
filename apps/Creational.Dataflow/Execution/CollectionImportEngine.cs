using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Creational.Dataflow.Transfers;

namespace Creational.Dataflow.Execution;

public sealed record CollectionPlanSnapshot(ImportPlan Plan,
    IReadOnlyDictionary<TargetId, TargetState> States);

public sealed class CollectionImportEngine(
    IReadOnlyList<ManifestShard> shards,
    ITargetRepository repository,
    ICollectionSettingsRepository settings,
    IOutputManifestReader manifestReader,
    TargetVerifier verifier,
    TargetPlanner planner,
    WorkInputBuilder inputs,
    QueueClient queue,
    CopyHostedService copies,
    IComputeJobLauncher launcher,
    AzureBlobStore blobs,
    IConfiguration configuration,
    ILogger<CollectionImportEngine> logger) : ICollectionImportEngine
{
    private readonly IReadOnlyList<TargetDefinition> _definitions = October2026Collection.Build(shards);
    private readonly ConcurrentDictionary<TargetId, (string Marker, DateTimeOffset Checked, VerificationResult Result)> _verificationCache = new();
    private readonly ConcurrentDictionary<TargetId, string> _activeCopyAttempts = new();

    public async Task<CollectionPlanSnapshot> GetRecordedPlanAsync(string collection, CancellationToken ct)
    {
        if (collection != October2026Collection.Collection) throw new ArgumentOutOfRangeException(nameof(collection));
        var states = await repository.GetCollectionAsync(collection, ct);
        var recorded = states.Where(kv => kv.Value.ManifestPath is not null && kv.Value.Fingerprint is not null)
            .ToDictionary(kv => kv.Key, kv => new VerificationResult(true, kv.Value.Fingerprint, null));
        var plan = planner.Plan(_definitions, states, recorded);
        return new(new ImportPlan(plan.Targets.Select(target =>
            target.Disposition == TargetDisposition.Current
                ? target with { Reason = "Last verified state; checked again on Import" }
                : target).ToArray()), states);
    }

    public async Task<CollectionPlanSnapshot> GetPlanAsync(string collection, CancellationToken ct)
    {
        if (collection != October2026Collection.Collection) throw new ArgumentOutOfRangeException(nameof(collection));
        var states = await repository.GetCollectionAsync(collection, ct);
        var checks = new ConcurrentDictionary<TargetId, VerificationResult>();
        await Parallel.ForEachAsync(_definitions, new ParallelOptions
        {
            MaxDegreeOfParallelism = 24,
            CancellationToken = ct
        }, async (definition, token) =>
        {
            if (!states.TryGetValue(definition.Id, out var state) || state.ManifestPath is null) return;
            if (_verificationCache.TryGetValue(definition.Id, out var cached) &&
                cached.Marker == state.ManifestPath && DateTimeOffset.UtcNow - cached.Checked < TimeSpan.FromMinutes(2))
            {
                checks[definition.Id] = cached.Result;
                return;
            }
            VerificationResult result;
            try
            {
                var manifest = await manifestReader.ReadAsync(state.ManifestPath, token);
                result = manifest.Target == definition.Id && definition.HasRequiredArtifacts(manifest)
                    ? await verifier.VerifyAsync(manifest, token)
                    : new(false, null, "Completion marker has wrong target or omits required artifacts");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                result = new(false, null, error.Message);
            }
            checks[definition.Id] = result;
            _verificationCache[definition.Id] = (state.ManifestPath, DateTimeOffset.UtcNow, result);
        });
        return new(planner.Plan(_definitions, states, checks), states);
    }

    public async Task RunAsync(string collection, string runId, CancellationToken ct)
    {
        if (collection != October2026Collection.Collection) throw new ArgumentOutOfRangeException(nameof(collection));
        var queued = new Dictionary<TargetId, string>();
        var copyTasks = new Dictionary<TargetId, Task>();
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileAsync(runId, ct);
            foreach (var (id, task) in copyTasks.Where(kv => kv.Value.IsCompleted).ToArray())
            {
                await task;
                copyTasks.Remove(id);
            }
            var snapshot = await GetPlanAsync(collection, ct);
            if (snapshot.Plan.Targets.All(t => t.Disposition == TargetDisposition.Current)) return;
            foreach (var (id, attempt) in queued.ToArray())
            {
                if (snapshot.States.TryGetValue(id, out var state) && state.LastAttemptId == attempt)
                {
                    if (state.Outcome == "failed") throw new InvalidOperationException($"{id} failed: {state.Error}");
                    queued.Remove(id);
                }
                else if (snapshot.States.TryGetValue(id, out state) && state.AttemptId is null &&
                    state.Outcome == "complete" && snapshot.Plan[id].Disposition == TargetDisposition.Current)
                    queued.Remove(id);
            }
            var parallelism = await settings.GetParallelismAsync(collection, ct);
            var capacity = CollectionParallelism.AvailableSlots(parallelism,
                queued.Keys, copyTasks.Keys,
                snapshot.States.Values.Where(state => state.AttemptId is not null).Select(state => state.Id));
            foreach (var planned in snapshot.Plan.Targets.Where(t => t.Disposition == TargetDisposition.Run).Take(capacity))
            {
                if (queued.ContainsKey(planned.Id) || copyTasks.ContainsKey(planned.Id)) continue;
                if (snapshot.States.TryGetValue(planned.Id, out var state) && state.AttemptId is not null) continue;
                var definition = _definitions.Single(d => d.Id == planned.Id);
                var attempt = Guid.NewGuid().ToString("N");
                if (IsCopyTarget(planned.Id.Kind))
                    copyTasks[planned.Id] = ProcessCopyAsync(definition, snapshot.States, attempt, ct);
                else
                {
                    var inputUri = await inputs.PublishAsync(definition, snapshot.States, attempt, ct);
                    var outputPrefix = AttemptOutputPrefix(planned.Id, attempt);
                    var revision = state?.StaleRevision ?? 0;
                    var item = new QueueWorkItem(planned.Id, attempt, revision, inputUri, outputPrefix, definition.RecipeVersion);
                    await queue.SendMessageAsync(JsonSerializer.Serialize(item), cancellationToken: ct);
                    queued[planned.Id] = attempt;
                }
            }
            if (queued.Count == 0 && copyTasks.Count == 0 &&
                !snapshot.States.Values.Any(s => s.AttemptId is not null) &&
                snapshot.Plan.Targets.All(t => t.Disposition is TargetDisposition.Blocked or TargetDisposition.Failed))
                throw new InvalidOperationException("Collection has blocked targets and no work is running");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    public async Task ReconcileAsync(string runId, CancellationToken ct)
    {
        var states = await repository.GetCollectionAsync(October2026Collection.Collection, ct);
        foreach (var state in states.Values.Where(s => s.AttemptId is not null))
        {
            if (state.ExecutionId is null)
            {
                if (IsCopyTarget(state.Id.Kind))
                {
                    if (!_activeCopyAttempts.TryGetValue(state.Id, out var active) || active != state.AttemptId)
                        await repository.FailAttemptAsync(state.Id, state.AttemptId!, "App Service copy was interrupted", ct);
                }
                else if (state.AttemptStartedAt is { } started && DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(2))
                    await repository.FailAttemptAsync(state.Id, state.AttemptId!, "Worker launch was interrupted", ct);
                continue;
            }
            var status = await launcher.GetStatusAsync(state.ExecutionId, ct);
            if (status.State == ComputeExecutionState.Running || status.State == ComputeExecutionState.Unknown) continue;
            if (status.State == ComputeExecutionState.Failed)
            {
                await repository.FailAttemptAsync(state.Id, state.AttemptId!, status.Message ?? "Container Apps Job failed", ct);
                continue;
            }
            var marker = AttemptOutputPrefix(state.Id, state.AttemptId!) + "_COMPLETE.json";
            try
            {
                var manifest = await manifestReader.ReadAsync(marker, ct);
                var check = await verifier.VerifyAsync(manifest, ct);
                var definition = _definitions.Single(d => d.Id == state.Id);
                if (!check.Valid || manifest.Target != state.Id || manifest.RecipeVersion != definition.RecipeVersion ||
                    !definition.HasRequiredArtifacts(manifest))
                    throw new InvalidDataException(check.Error ?? "Worker marker identity or recipe mismatch");
                await repository.CompleteAttemptAsync(state.Id, state.AttemptId!, state.AttemptRevision!.Value,
                    marker, check.Fingerprint!, definition.RecipeVersion, manifest.InputFingerprints, ct);
                _verificationCache.TryRemove(state.Id, out _);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await repository.FailAttemptAsync(state.Id, state.AttemptId!, error.Message, ct);
            }
        }
    }

    private async Task ProcessCopyAsync(TargetDefinition definition,
        IReadOnlyDictionary<TargetId, TargetState> initialStates, string attempt, CancellationToken ct)
    {
        var id = definition.Id;
        var current = initialStates.TryGetValue(id, out var state) ? state : TargetState.Empty(id);
        if (!_activeCopyAttempts.TryAdd(id, attempt)) throw new InvalidOperationException($"Copy already running for {id}");
        try
        {
            await repository.BeginAttemptAsync(id, attempt, current.StaleRevision, ct);
            var source = await CopySourceAsync(definition, current, ct);
            var copyResult = await copies.EnqueueAsync(source, ct);
            if (copyResult.CleanupError is { Length: > 0 } warning)
                logger.LogWarning("Copy target {Target} completed, but previous-version cleanup failed: {Warning}", id, warning);
            if (current.ManifestPath is null) throw new InvalidOperationException($"No completion manifest to update after copying {id}");
            var manifest = await manifestReader.ReadAsync(current.ManifestPath, ct);
            var check = await verifier.VerifyAsync(manifest, ct);
            if (!check.Valid) throw new InvalidDataException(check.Error);
            var inputs = definition.ContentDependencies.ToDictionary(dep => dep.ToString(),
                dep => initialStates[dep].Fingerprint ?? throw new InvalidDataException($"Missing dependency {dep}"));
            await repository.CompleteAttemptAsync(id, attempt, current.StaleRevision,
                current.ManifestPath, check.Fingerprint!, definition.RecipeVersion, inputs, ct,
                copyResult.CleanupError);
            _verificationCache.TryRemove(id, out _);
        }
        catch (Exception error)
        {
            var states = await repository.GetCollectionAsync(id.Collection, CancellationToken.None);
            if (states.TryGetValue(id, out var failed) && failed.AttemptId == attempt)
                await repository.FailAttemptAsync(id, attempt, error.Message, CancellationToken.None);
            logger.LogError(error, "Copy target {Target} failed", id);
            throw;
        }
        finally
        {
            _activeCopyAttempts.TryRemove(id, out _);
        }
    }

    private async Task<CopyTarget> CopySourceAsync(TargetDefinition definition, TargetState state, CancellationToken ct)
    {
        if (state.ManifestPath is null) throw new InvalidOperationException($"Copy target {definition.Id} has no adopted manifest");
        var manifest = await manifestReader.ReadAsync(state.ManifestPath, ct);
        var member = definition.Id.Kind switch
        {
            "dump-shard" => manifest.Artifacts.Single(),
            "ott-source" => manifest.Artifacts.Single(),
            "source-manifest" => manifest.Artifacts.Single(a => a.Name == "SHA256SUMS"),
            _ => throw new InvalidOperationException($"{definition.Id.Kind} is not a copy target")
        };
        var sourceUrl = definition.Id.Kind switch
        {
            "dump-shard" => "https://dumps.wikimedia.org/other/mediawiki_content_current/enwiki/2026-10-01/xml/bzip2/" + member.Name,
            "source-manifest" => "https://dumps.wikimedia.org/other/mediawiki_content_current/enwiki/2026-10-01/xml/bzip2/SHA256SUMS",
            "ott-source" => "https://files.opentreeoflife.org/ott/ott3.3/ott3.3/taxonomy.tsv",
            _ => throw new InvalidOperationException()
        };
        string? oldVersion = null;
        string? oldETag = null;
        try
        {
            var properties = await blobs.Client(member.Path).GetPropertiesAsync(cancellationToken: ct);
            oldVersion = properties.Value.VersionId;
            oldETag = properties.Value.ETag.ToString();
            if (oldVersion is null && !configuration.GetValue("Copies:VersioningEnabled", false))
                throw new InvalidOperationException("Confirm Blob versioning is enabled, then set Copies:VersioningEnabled=true before replacing a legacy asset");
        }
        catch (RequestFailedException error) when (error.Status == 404) { }
        return new(definition.Id, sourceUrl, member.Path, member.Length, member.Sha256, oldVersion, oldETag);
    }

    public static string AttemptOutputPrefix(TargetId id, string attempt) =>
        $"https://creational.blob.core.windows.net/wikimedia-extracts/{id.Collection}/attempts/{Uri.EscapeDataString(id.Kind)}/{Uri.EscapeDataString(id.Key)}/{Uri.EscapeDataString(attempt)}/";

    private static bool IsCopyTarget(string kind) => kind is "source-manifest" or "dump-shard" or "ott-source";
}
