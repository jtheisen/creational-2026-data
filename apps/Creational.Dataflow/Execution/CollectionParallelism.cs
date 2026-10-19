using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Execution;

public static class CollectionParallelism
{
    public const int Default = 1;
    public const int Maximum = 5;

    public static int Validate(int value)
    {
        if (value is < 1 or > Maximum)
            throw new ArgumentOutOfRangeException(nameof(value), $"Parallelism must be between 1 and {Maximum}");
        return value;
    }

    public static int AvailableSlots(int limit, IEnumerable<TargetId> queued,
        IEnumerable<TargetId> copying, IEnumerable<TargetId> persistedAttempts)
    {
        Validate(limit);
        var outstanding = queued.Concat(copying).Concat(persistedAttempts).ToHashSet();
        return Math.Max(0, limit - outstanding.Count);
    }
}
