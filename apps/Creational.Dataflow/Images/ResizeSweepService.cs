using Creational.Dataflow.Execution;

namespace Creational.Dataflow.Images;

public interface IResizeSweepScheduler
{
    Task<string> RequestAsync(CancellationToken ct);
}

public sealed class ResizeSweepService(IResizeSweepRepository repository, IResizeJobLauncher launcher,
    ILogger<ResizeSweepService> logger, bool allowLegacyWrites = true) : BackgroundService, IResizeSweepScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public Task<IReadOnlyList<ResizeSweepRun>> ListAsync(int limit, CancellationToken ct) =>
        repository.ListAsync(limit, ct);

    public Task<ResizeSweepRun?> GetAsync(string id, CancellationToken ct) => repository.GetAsync(id, ct);

    public Task<IReadOnlyList<ResizeFailure>> ListFailuresAsync(string id, int limit, CancellationToken ct) =>
        repository.ListFailuresAsync(id, limit, ct);

    public async Task<string> RequestAsync(CancellationToken ct)
    {
        if (!allowLegacyWrites)
            throw new InvalidOperationException("Legacy resize sweeps are read-only during the SQL cutover.");
        await _gate.WaitAsync(ct);
        try
        {
            var run = await repository.RequestOrJoinAsync(ct);
            if (_wake.CurrentCount == 0) _wake.Release();
            return run.Id;
        }
        finally { _gate.Release(); }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        foreach (var run in await repository.ListAsync(int.MaxValue, ct))
        {
            if (run.ExecutionId is null)
            {
                if (run.Status == "running" && DateTimeOffset.UtcNow -
                    (run.UpdatedAt ?? run.CreatedAt) > TimeSpan.FromMinutes(5))
                {
                    await repository.PatchAsync(run.Id, status: "failed",
                        error: "Worker launch was not recorded; inspect Container Apps executions before retrying.", ct: ct);
                    continue;
                }
                if (run.Status is "complete" or "complete-with-errors" && run.ResweepRequested)
                {
                    await RequestAsync(ct);
                    await repository.PatchAsync(run.Id, resweepRequested: false, ct: ct);
                    continue;
                }
                if (run.Status is "pending" or "continuing")
                {
                    try
                    {
                        if (!await repository.TryClaimLaunchAsync(run.Id, ct)) continue;
                        var execution = await launcher.StartResizeAsync(run.Id, ct);
                        await repository.PatchAsync(run.Id, executionId: execution, ct: ct);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        await repository.PatchAsync(run.Id, status: "failed", error: error.Message, ct: ct);
                        logger.LogError(error, "Could not start resize sweep {RunId}", run.Id);
                    }
                }
                continue;
            }
            var executionStatus = await launcher.GetStatusAsync(run.ExecutionId, ct);
            if (executionStatus.State is ComputeExecutionState.Running or ComputeExecutionState.Unknown) continue;
            if (executionStatus.State == ComputeExecutionState.Failed)
            {
                await repository.PatchAsync(run.Id, status: "failed", executionId: "",
                    error: executionStatus.Message ?? "Resize worker failed", ct: ct);
                continue;
            }
            var current = await repository.GetAsync(run.Id, ct) ??
                throw new InvalidDataException("Resize run disappeared during reconciliation");
            if (current.Status == "continuing")
                await repository.PatchAsync(run.Id, status: "pending", executionId: "", ct: ct);
            else if (current.Status is "complete" or "complete-with-errors")
            {
                await repository.PatchAsync(run.Id, executionId: "", ct: ct);
                if (current.ResweepRequested)
                {
                    await RequestAsync(ct);
                    await repository.PatchAsync(run.Id, resweepRequested: false, ct: ct);
                }
            }
            else if (current.Status == "failed")
                await repository.PatchAsync(run.Id, executionId: "", ct: ct);
            else
                await repository.PatchAsync(run.Id, status: "failed", executionId: "",
                    error: "Worker succeeded without a completion state", ct: ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
                await _wake.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Resize sweep reconciliation failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
