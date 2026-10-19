using Creational.Dataflow.Components;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class CollectionPageTests
{
    [Fact]
    public void CheckedTargetsTakePrecedenceOverClickedRowAndSurviveRecreatedIds()
    {
        var collection = October2026Collection.Collection;
        var first = new TargetId(collection, "parse-shard", "p1");
        var second = new TargetId(collection, "parse-shard", "p2");
        var clicked = new TargetId(collection, "ott-names", "all");
        var selection = new TargetSelection();
        selection.Set(first, true);
        selection.Set(second, true);

        Assert.True(selection.Contains(new TargetId(collection, "parse-shard", "p1")));
        Assert.Equal([first, second], selection.ActionTargets(clicked));

        selection.MarkSucceeded(first);
        Assert.Equal([second], selection.ActionTargets(clicked));
        selection.MarkSucceeded(second);
        Assert.Equal([clicked], selection.ActionTargets(clicked));
    }

    [Fact]
    public void ClickingRowTogglesTheSameSelectionUsedByBulkStale()
    {
        var id = new TargetId(October2026Collection.Collection, "parse-shard", "p1");
        var other = new TargetId(October2026Collection.Collection, "ott-names", "all");
        var selection = new TargetSelection();

        selection.Toggle(id);
        Assert.True(selection.Contains(id));
        Assert.Equal([id], selection.ActionTargets(other));

        selection.Toggle(new TargetId(id.Collection, id.Kind, id.Key));
        Assert.False(selection.Contains(id));
        Assert.Equal([other], selection.ActionTargets(other));
    }

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
