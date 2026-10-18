namespace Creational.Dataflow.Targets;

public enum TargetDisposition { Current, Run, Blocked, Failed }

public sealed record PlannedTarget(TargetId Id, TargetDisposition Disposition, string Reason);

public sealed record ImportPlan(IReadOnlyList<PlannedTarget> Targets)
{
    public PlannedTarget this[TargetId id] => Targets.Single(t => t.Id == id);
}
