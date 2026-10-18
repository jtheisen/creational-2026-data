using Creational.Dataflow.Components;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class CollectionPageTests
{
    [Fact]
    public void GroupsTargetsAndCountsTheirPlanStates()
    {
        var collection = October2026Collection.Collection;
        var plan = new ImportPlan([
            new(new(collection, "parse-shard", "p1"), TargetDisposition.Run, "Marked stale"),
            new(new(collection, "parse-shard", "p2"), TargetDisposition.Current, "Verified current"),
            new(new(collection, "ott-names", "all"), TargetDisposition.Blocked, "Waiting")
        ]);
        var groups = CollectionPageModel.Groups(plan);
        Assert.Equal(2, groups.Count);
        Assert.Equal(1, groups.Single(g => g.Kind == "parse-shard").Counts[TargetDisposition.Run]);
        Assert.Equal(1, groups.Single(g => g.Kind == "parse-shard").Counts[TargetDisposition.Current]);
    }
}
