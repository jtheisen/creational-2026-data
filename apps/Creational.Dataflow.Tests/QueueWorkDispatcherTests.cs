using Creational.Dataflow.Execution;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class QueueWorkDispatcherTests
{
    [Fact]
    public async Task PersistedQueueAttemptLaunchesOnlyOnceEvenAfterRedelivery()
    {
        var id = new TargetId("enwiki/2026-10-01", "parse-shard", "p1");
        var repository = new FakeRepository(TargetState.Empty(id).MarkStale("first")
            .QueueAttempt("attempt-1", 1, "inputs").MarkStale("second"));
        var launcher = new FakeLauncher();
        var item = new QueueWorkItem(id, "attempt-1", 1, "inputs",
            CollectionImportEngine.AttemptOutputPrefix(id, "attempt-1"), "v1");
        var started = await QueueWorkDispatcher.ProcessAsync(item, repository, launcher, CancellationToken.None);
        Assert.True(started);
        Assert.Equal(1, repository.State.AttemptRevision);
        Assert.Equal(2, repository.State.StaleRevision);
        Assert.Equal("execution-1", repository.State.ExecutionId);
        Assert.False(await QueueWorkDispatcher.ProcessAsync(item, repository, launcher, CancellationToken.None));
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task AmbiguousStartNeverLaunchesAgainOnRedelivery()
    {
        var id = new TargetId("enwiki/2026-10-01", "parse-shard", "p1");
        var repository = new FakeRepository(TargetState.Empty(id).QueueAttempt("attempt-1", 0, "inputs"));
        var launcher = new FakeLauncher { FailStart = true };
        var item = new QueueWorkItem(id, "attempt-1", 0, "inputs",
            CollectionImportEngine.AttemptOutputPrefix(id, "attempt-1"), "v1");
        await Assert.ThrowsAsync<IOException>(() => QueueWorkDispatcher.ProcessAsync(item, repository, launcher, CancellationToken.None));
        Assert.Equal("launching", repository.State.Outcome);
        Assert.False(await QueueWorkDispatcher.ProcessAsync(item, repository, launcher, CancellationToken.None));
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
        public Task<TargetState> QueueAttemptAsync(TargetId id, string attempt, long revision, string input, CancellationToken ct)
        { State = State.QueueAttempt(attempt, revision, input); return Task.FromResult(State); }
        public Task<TargetState> NoteEnqueuedAsync(TargetId id, string attempt, CancellationToken ct)
        { State = State.NoteEnqueued(attempt); return Task.FromResult(State); }
        public Task<TargetState> BeginLaunchAsync(TargetId id, string attempt, CancellationToken ct)
        { State = State.BeginLaunch(attempt); return Task.FromResult(State); }
        public Task<TargetState> MarkLaunchUncertainAsync(TargetId id, string attempt, CancellationToken ct)
        { State = State.MarkLaunchUncertain(attempt); return Task.FromResult(State); }
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
        public bool FailStart { get; init; }
        public Task<string> StartAsync(TargetId target, string input, string output, string recipe, CancellationToken ct)
        { Starts++; return FailStart ? throw new IOException("response lost") : Task.FromResult("execution-1"); }
        public Task<ComputeExecutionStatus> GetStatusAsync(string execution, CancellationToken ct) => throw new NotImplementedException();
    }
}
