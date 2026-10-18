using Creational.Dataflow.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ImportCoordinatorTests
{
    [Fact]
    public async Task DuplicateClicksReturnTheSameActiveRun()
    {
        var engine = new GateEngine();
        var coordinator = new ImportCoordinator(engine, NullLogger<ImportCoordinator>.Instance);
        var first = await coordinator.StartOrGetAsync("enwiki/2026-10-01", CancellationToken.None);
        var second = await coordinator.StartOrGetAsync("enwiki/2026-10-01", CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Equal(1, engine.Starts);
        engine.Release();
        await coordinator.WaitAsync(first, CancellationToken.None);
    }

    private sealed class GateEngine : ICollectionImportEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }
        public async Task RunAsync(string collection, string runId, CancellationToken ct)
        { Starts++; await _release.Task.WaitAsync(ct); }
        public Task ReconcileAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public void Release() => _release.TrySetResult();
    }
}
