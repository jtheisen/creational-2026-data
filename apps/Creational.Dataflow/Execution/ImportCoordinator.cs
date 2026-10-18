namespace Creational.Dataflow.Execution;

public interface ICollectionImportEngine
{
    Task RunAsync(string collection, string runId, CancellationToken cancellationToken);
    Task ReconcileAsync(string runId, CancellationToken cancellationToken);
}

public sealed record ImportRunSnapshot(string RunId, string Collection, string State,
    DateTimeOffset StartedAt, string? Error);

public sealed class ImportCoordinator(ICollectionImportEngine engine, ILogger<ImportCoordinator> logger)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Run> _active = [];
    private readonly Dictionary<string, Run> _runs = [];

    public Task<string> StartOrGetAsync(string collection, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(collection, out var existing)) return Task.FromResult(existing.Id);
            var run = new Run(Guid.NewGuid().ToString("N"), collection);
            _active[collection] = run;
            _runs[run.Id] = run;
            run.Task = ExecuteAsync(run, cancellationToken);
            return Task.FromResult(run.Id);
        }
    }

    public async Task ReconcileAsync(string runId, CancellationToken cancellationToken) =>
        await engine.ReconcileAsync(runId, cancellationToken);

    public async Task WaitAsync(string runId, CancellationToken cancellationToken)
    {
        Run run;
        lock (_sync) run = _runs[runId];
        if (run.Task is not null) await run.Task.WaitAsync(cancellationToken);
    }

    public IReadOnlyList<ImportRunSnapshot> Snapshots()
    {
        lock (_sync) return _runs.Values.Select(r => new ImportRunSnapshot(r.Id, r.Collection,
            r.State, r.StartedAt, r.Error)).OrderByDescending(r => r.StartedAt).ToArray();
    }

    private async Task ExecuteAsync(Run run, CancellationToken ct)
    {
        try
        {
            await engine.RunAsync(run.Collection, run.Id, ct);
            run.State = "complete";
        }
        catch (Exception error)
        {
            run.State = "failed";
            run.Error = error.Message;
            logger.LogError(error, "Collection import {RunId} failed", run.Id);
            throw;
        }
        finally
        {
            lock (_sync) _active.Remove(run.Collection);
        }
    }

    private sealed class Run(string id, string collection)
    {
        public string Id { get; } = id;
        public string Collection { get; } = collection;
        public string State { get; set; } = "running";
        public string? Error { get; set; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public Task? Task { get; set; }
    }
}
