using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Components;

public sealed class TargetSelection
{
    private readonly HashSet<TargetId> _selected = [];

    public int Count => _selected.Count;

    public bool Contains(TargetId id) => _selected.Contains(id);

    public void Set(TargetId id, bool selected)
    {
        if (selected) _selected.Add(id);
        else _selected.Remove(id);
    }

    public void MarkSucceeded(TargetId id) => _selected.Remove(id);

    public void Clear() => _selected.Clear();

    public IReadOnlyList<TargetId> ActionTargets(TargetId clicked) =>
        _selected.Count == 0 ? [clicked] : _selected.ToArray();
}
