using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class TargetRepositoryTests
{
    private static readonly TargetId Id = new("enwiki/2026-10-01", "parse-shard", "p1");

    [Fact]
    public void RepeatedStaleMarksIncreaseRevision()
    {
        var state = TargetState.Empty(Id);
        state = state.MarkStale("check").MarkStale("again");
        Assert.Equal(2, state.StaleRevision);
        Assert.Equal("again", state.StaleReason);
    }

    [Fact]
    public void StaleMarkDuringAttemptRemainsUnsatisfied()
    {
        var state = TargetState.Empty(Id).MarkStale("refresh");
        state = state.BeginAttempt("attempt-1", 1);
        state = state.MarkStale("refresh again");
        state = state.CompleteAttempt("attempt-1", 1, "manifest.json", "fingerprint", "v1", new Dictionary<string, string>());
        Assert.Equal(2, state.StaleRevision);
        Assert.Equal(1, state.SatisfiedStaleRevision);
        Assert.True(state.IsStale);
    }

    [Fact]
    public void DuplicateOrWrongAttemptCannotComplete()
    {
        var state = TargetState.Empty(Id).QueueAttempt("attempt-1", 0, "inputs").BeginLaunch("attempt-1");
        Assert.Throws<InvalidOperationException>(() => state.CompleteAttempt("attempt-2", 0, "m", "f", "v", new Dictionary<string, string>()));
        state = state.CompleteAttempt("attempt-1", 0, "m", "f", "v", new Dictionary<string, string>());
        Assert.Throws<InvalidOperationException>(() => state.CompleteAttempt("attempt-1", 0, "m", "f", "v", new Dictionary<string, string>()));
    }

    [Fact]
    public void ConflictingAttemptRevisionIsRejected()
    {
        var state = TargetState.Empty(Id).MarkStale("one");
        Assert.Throws<InvalidOperationException>(() => state.BeginAttempt("attempt", 0));
    }

    [Fact]
    public void ExecutionCanOnlyAttachToClaimedAttempt()
    {
        var state = TargetState.Empty(Id).QueueAttempt("attempt-1", 0, "inputs").BeginLaunch("attempt-1");
        Assert.Throws<InvalidOperationException>(() => state.AttachExecution("attempt-2", "execution-1"));
        state = state.AttachExecution("attempt-1", "execution-1");
        Assert.Equal("execution-1", state.ExecutionId);
    }

    [Fact]
    public void QueuedAndUncertainLaunchRemainDurableUntilResolved()
    {
        var state = TargetState.Empty(Id).QueueAttempt("attempt-1", 0, "inputs");
        Assert.Equal("queued", state.Outcome);
        Assert.Equal("inputs", state.InputManifestUri);
        Assert.Throws<InvalidOperationException>(() => state.QueueAttempt("attempt-2", 0, "other"));

        state = state.NoteEnqueued("attempt-1").BeginLaunch("attempt-1").MarkLaunchUncertain("attempt-1");
        Assert.Equal("launch-uncertain", state.Outcome);
        Assert.NotNull(state.AttemptId);
        Assert.NotNull(state.LastEnqueuedAt);

        state = state.AttachExecution("attempt-1", "execution-1");
        Assert.Equal("running", state.Outcome);
        Assert.Null(state.Error);
    }

    [Fact]
    public void CleanupWarningRemainsVisibleOnCompletedTarget()
    {
        var state = TargetState.Empty(Id).BeginAttempt("attempt", 0);
        state = state.CompleteAttempt("attempt", 0, "marker", "fingerprint", "v1",
            new Dictionary<string, string>(), "old version delete denied");
        Assert.Equal("complete-with-warning", state.Outcome);
        Assert.Contains("denied", state.Error);
        Assert.False(state.IsStale);
    }
}
