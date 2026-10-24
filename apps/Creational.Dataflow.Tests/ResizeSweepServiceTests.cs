using Creational.Dataflow.Execution;
using Creational.Dataflow.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ResizeSweepServiceTests
{
    [Fact]
    public async Task OneSweepContinuesAcrossWorkerExecutions()
    {
        var repository = new MemoryRepository();
        var launcher = new FakeLauncher();
        var service = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);
        var id = await service.RequestAsync(CancellationToken.None);

        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal("execution-1", (await repository.GetAsync(id, CancellationToken.None))!.ExecutionId);
        await repository.PatchAsync(id, status: "continuing", cursor: "abc-250", processed: 10);
        launcher.Status = new(ComputeExecutionState.Succeeded, null);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal("pending", (await repository.GetAsync(id, CancellationToken.None))!.Status);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal("execution-2", (await repository.GetAsync(id, CancellationToken.None))!.ExecutionId);

        await repository.PatchAsync(id, status: "complete", processed: 11);
        await service.ReconcileAsync(CancellationToken.None);
        var complete = await repository.GetAsync(id, CancellationToken.None);
        Assert.Equal("complete", complete!.Status);
        Assert.Equal(11, complete.Processed);
        Assert.Null(complete.ExecutionId);
        Assert.Equal(2, launcher.Starts);
    }

    [Fact]
    public async Task ImportDuringActiveSweepRequestsAnotherPass()
    {
        var repository = new MemoryRepository();
        var launcher = new FakeLauncher();
        var service = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);
        var first = await service.RequestAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(first, await service.RequestAsync(CancellationToken.None));
        Assert.True((await repository.GetAsync(first, CancellationToken.None))!.ResweepRequested);
        await repository.PatchAsync(first, status: "complete");
        launcher.Status = new(ComputeExecutionState.Succeeded, null);
        await service.ReconcileAsync(CancellationToken.None);
        Assert.Equal(2, (await repository.ListAsync(10, CancellationToken.None)).Count);
        Assert.Contains(await repository.ListAsync(10, CancellationToken.None),
            x => x.Id != first && x.Status == "pending");
    }

    [Fact]
    public async Task FailedExecutionRemainsVisibleAndCanBeRetriedByNewRequest()
    {
        var repository = new MemoryRepository();
        var launcher = new FakeLauncher();
        var service = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);
        var id = await service.RequestAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        launcher.Status = new(ComputeExecutionState.Failed, "out of memory");
        await service.ReconcileAsync(CancellationToken.None);
        var failed = await repository.GetAsync(id, CancellationToken.None);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("out of memory", failed.Error);
        Assert.NotEqual(id, await service.RequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkerReportedFailureCanBeRetriedBeforeExecutionReconciliation()
    {
        var repository = new MemoryRepository();
        var service = new ResizeSweepService(repository, new FakeLauncher(),
            NullLogger<ResizeSweepService>.Instance);
        var id = await service.RequestAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        await repository.PatchAsync(id, status: "failed", error: "decode infrastructure failed");

        Assert.NotEqual(id, await service.RequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FastWorkerCompletionIsNotOverwrittenByLaunchBookkeeping()
    {
        var repository = new MemoryRepository();
        var launcher = new FakeLauncher();
        var service = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);
        var id = await service.RequestAsync(CancellationToken.None);
        launcher.OnStart = () => repository.PatchAsync(id, status: "complete", processed: 1);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal("complete", (await repository.GetAsync(id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task TwoControlPlaneInstancesJoinOneActiveSweep()
    {
        var repository = new MemoryRepository();
        var launcher = new FakeLauncher();
        var first = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);
        var second = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);

        var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            (index % 2 == 0 ? first : second).RequestAsync(CancellationToken.None)));

        Assert.Single(ids.Distinct());
        Assert.Single(await repository.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task ImportRequestDuringCompletionCannotBeClearedWithoutANewRun()
    {
        var repository = new MemoryRepository();
        var launcher = new FakeLauncher();
        var service = new ResizeSweepService(repository, launcher,
            NullLogger<ResizeSweepService>.Instance);
        var id = await service.RequestAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        await repository.PatchAsync(id, status: "complete");
        launcher.Status = new(ComputeExecutionState.Succeeded, null);
        var injected = false;
        repository.BeforePatch = async (patchId, executionId, resweepRequested) =>
        {
            if (!injected && patchId == id && executionId == "")
            {
                injected = true;
                await service.RequestAsync(CancellationToken.None);
            }
        };

        await service.ReconcileAsync(CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, (await repository.ListAsync(10, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task LaunchWithoutRecordedExecutionDoesNotRemainRunningForever()
    {
        var repository = new MemoryRepository();
        var service = new ResizeSweepService(repository, new FakeLauncher(),
            NullLogger<ResizeSweepService>.Instance);
        var id = await service.RequestAsync(CancellationToken.None);
        Assert.True(await repository.TryClaimLaunchAsync(id, CancellationToken.None));
        repository.ForceUpdatedAt(id, DateTimeOffset.UtcNow.AddMinutes(-10));

        await service.ReconcileAsync(CancellationToken.None);

        var run = await repository.GetAsync(id, CancellationToken.None);
        Assert.Equal("failed", run!.Status);
        Assert.Contains("not recorded", run.Error);
    }

    private sealed class FakeLauncher : IResizeJobLauncher
    {
        public int Starts { get; private set; }
        public ComputeExecutionStatus Status { get; set; } = new(ComputeExecutionState.Running, null);
        public Func<Task>? OnStart { get; set; }
        public async Task<string> StartResizeAsync(string runId, CancellationToken ct)
        {
            Starts++;
            if (OnStart is not null) await OnStart();
            return $"execution-{Starts}";
        }
        public Task<ComputeExecutionStatus> GetStatusAsync(string executionId, CancellationToken ct) =>
            Task.FromResult(Status);
    }

    private sealed class MemoryRepository : IResizeSweepRepository
    {
        private readonly Dictionary<string, ResizeSweepRun> _rows = [];
        private readonly object _lock = new();
        private string? _activeId;
        public void ForceUpdatedAt(string id, DateTimeOffset time) =>
            _rows[id] = _rows[id] with { UpdatedAt = time };
        public Func<string, string?, bool?, Task>? BeforePatch { get; set; }
        public Task<ResizeSweepRun> RequestOrJoinAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_activeId is not null && _rows.TryGetValue(_activeId, out var active) &&
                    (active.Status is "pending" or "running" or "continuing" ||
                     (active.Status is "complete" or "complete-with-errors" && active.ExecutionId is not null)))
                {
                    if (active.ExecutionId is not null || active.Status is "running" or "continuing")
                        _rows[active.Id] = active with { ResweepRequested = true };
                    return Task.FromResult(active);
                }
                var created = new ResizeSweepRun(Guid.NewGuid().ToString("N"), "pending", DateTimeOffset.UtcNow);
                if (_activeId is not null && _rows.TryGetValue(_activeId, out var old))
                    _rows[_activeId] = old with { ResweepRequested = false };
                _rows.Add(created.Id, created);
                _activeId = created.Id;
                return Task.FromResult(created);
            }
        }
        public Task<bool> TryClaimLaunchAsync(string id, CancellationToken ct)
        {
            lock (_lock)
            {
                var row = _rows[id];
                if (row.Status is not ("pending" or "continuing") || row.ExecutionId is not null)
                    return Task.FromResult(false);
                _rows[id] = row with { Status = "running", UpdatedAt = DateTimeOffset.UtcNow };
                return Task.FromResult(true);
            }
        }
        public Task AddAsync(ResizeSweepRun run, CancellationToken ct)
        {
            _rows.Add(run.Id, run);
            return Task.CompletedTask;
        }
        public Task<ResizeSweepRun?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_rows.GetValueOrDefault(id));
        public async Task<IReadOnlyList<ResizeSweepRun>> ListAsync(int limit, CancellationToken ct)
        {
            var snapshot = _rows.Values.Take(limit).ToArray();
            await Task.Yield();
            return snapshot;
        }
        public Task<IReadOnlyList<ResizeFailure>> ListFailuresAsync(string id, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResizeFailure>>([]);
        public async Task PatchAsync(string id, string? status = null, string? cursor = null,
            int? processed = null, string? executionId = null, bool? resweepRequested = null,
            string? error = null, CancellationToken ct = default)
        {
            if (BeforePatch is not null) await BeforePatch(id, executionId, resweepRequested);
            var row = _rows[id];
            _rows[id] = row with
            {
                Status = status ?? row.Status, Cursor = cursor ?? row.Cursor,
                Processed = processed ?? row.Processed,
                ExecutionId = executionId == "" ? null : executionId ?? row.ExecutionId,
                ResweepRequested = resweepRequested ?? row.ResweepRequested,
                Error = error ?? row.Error, UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }
}
