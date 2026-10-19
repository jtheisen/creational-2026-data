using Creational.Dataflow.Execution;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class CollectionParallelismTests
{
    private static readonly TargetId First = new("enwiki/2026-10-01", "parse-shard", "p1");
    private static readonly TargetId Second = new("enwiki/2026-10-01", "parse-shard", "p2");

    [Fact]
    public void DefaultsToOneOutstandingTarget()
    {
        Assert.Equal(1, CollectionParallelism.Default);
        Assert.Equal(1, CollectionParallelism.AvailableSlots(CollectionParallelism.Default,
            [], [], []));
    }

    [Fact]
    public void CountsPersistedAttemptsAfterRestart()
    {
        Assert.Equal(0, CollectionParallelism.AvailableSlots(1, [], [], [First]));
    }

    [Fact]
    public void CountsTheSameQueuedAndPersistedAttemptOnce()
    {
        Assert.Equal(1, CollectionParallelism.AvailableSlots(2, [First], [], [First]));
    }

    [Fact]
    public void CountsCopiesAndWorkersTogether()
    {
        Assert.Equal(0, CollectionParallelism.AvailableSlots(2, [First], [Second], []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void RejectsLimitsOutsideExistingSchedulerRange(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectionParallelism.Validate(value));
    }
}
