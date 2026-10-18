using Creational.Dataflow.Execution;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class QueueWorkDispatcherTests
{
    [Fact]
    public async Task StaleMarkBeforeClaimIsIncludedInTheClaimedAttempt()
    {
        var id = new TargetId("enwiki/2026-10-01", "parse-shard", "p1");
        var repository = new FakeRepository(TargetState.Empty(id).MarkStale("first").MarkStale("second"));
        var launcher = new FakeLauncher();
        var item = new QueueWorkItem(id, "attempt-1", 1, "inputs", "output", "v1");
        var started = await QueueWorkDispatcher.ProcessAsync(item, repository, launcher, CancellationToken.None);
        Assert.True(started);
        Assert.Equal(2, repository.State.AttemptRevision);
        Assert.Equal("execution-1", repository.State.ExecutionId);
        Assert.Equal(1, launcher.Starts);
    }

    private sealed class FakeRepository(TargetState state) : ITargetRepository
    {
        public TargetState State { get; private set; } = state;
        public Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<TargetId, TargetState>>(new Dictionary<TargetId, TargetState> { [State.Id] = State });
        public Task<TargetState> MarkStaleAsync(TargetId id, string reason, CancellationToken ct) => throw new NotImplementedException();
        public Task<TargetState> BeginAttemptAsync(TargetId id, string attempt, long revision, CancellationToken ct)
        { State = State.BeginAttempt(attempt, revision); return Task.FromResult(State); }
        public Task<TargetState> AttachExecutionAsync(TargetId id, string attempt, string execution, CancellationToken ct)
        { State = State.AttachExecution(attempt, execution); return Task.FromResult(State); }
        public Task<TargetState> CompleteAttemptAsync(TargetId id, string attempt, long revision, string manifest,
            string fingerprint, string recipe, IReadOnlyDictionary<string, string> inputs, CancellationToken ct,
            string? warning = null) => throw new NotImplementedException();
        public Task<TargetState> FailAttemptAsync(TargetId id, string attempt, string error, CancellationToken ct)
        { State = State.FailAttempt(attempt, error); return Task.FromResult(State); }
    }

    private sealed class FakeLauncher : IComputeJobLauncher
    {
        public int Starts { get; private set; }
        public Task<string> StartAsync(TargetId target, string input, string output, string recipe, CancellationToken ct)
        { Starts++; return Task.FromResult("execution-1"); }
        public Task<ComputeExecutionStatus> GetStatusAsync(string execution, CancellationToken ct) => throw new NotImplementedException();
    }
}
