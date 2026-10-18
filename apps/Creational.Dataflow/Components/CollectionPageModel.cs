using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Components;

public sealed record TargetGroup(string Kind, IReadOnlyList<PlannedTarget> Targets,
    IReadOnlyDictionary<TargetDisposition, int> Counts);

public static class CollectionPageModel
{
    public static IReadOnlyList<TargetGroup> Groups(ImportPlan plan) => plan.Targets
        .GroupBy(target => target.Id.Kind)
        .Select(group => new TargetGroup(group.Key, group.ToArray(),
            group.GroupBy(target => target.Disposition).ToDictionary(items => items.Key, items => items.Count())))
        .ToArray();
}
