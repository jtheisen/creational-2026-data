using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Components;

public sealed record TargetGroup(string Kind, IReadOnlyList<PlannedTarget> Targets,
    IReadOnlyDictionary<TargetDisposition, int> Counts);

public enum TargetListView { All, Working, Groups }

public static class CollectionPageModel
{
    public static IReadOnlyList<TargetGroup> Groups(ImportPlan plan) => plan.Targets
        .GroupBy(target => target.Id.Kind)
        .Select(group => new TargetGroup(group.Key, group.ToArray(),
            group.GroupBy(target => target.Disposition).ToDictionary(items => items.Key, items => items.Count())))
        .ToArray();

    public static bool IsActive(TargetId id, IReadOnlyDictionary<TargetId, TargetState> states) =>
        states.TryGetValue(id, out var state) && state.AttemptId is not null;

    public static bool HasActiveAttempt(TargetGroup group, IReadOnlyDictionary<TargetId, TargetState> states) =>
        group.Targets.Any(target => IsActive(target.Id, states));

    public static IReadOnlyList<PlannedTarget> VisibleTargets(TargetGroup group,
        IReadOnlyDictionary<TargetId, TargetState> states, TargetListView view) => view switch
    {
        TargetListView.All => group.Targets,
        TargetListView.Working => group.Targets.Where(target => IsActive(target.Id, states)).ToArray(),
        _ => []
    };
}
