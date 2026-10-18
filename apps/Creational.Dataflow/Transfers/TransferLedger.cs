using System.Collections.Concurrent;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Transfers;

public sealed record TransferSnapshot(TargetId Target, string State, long Bytes, long? ExpectedBytes,
    DateTimeOffset StartedAt, string? Error);

public sealed class TransferLedger
{
    private readonly ConcurrentDictionary<TargetId, TransferSnapshot> _items = new();
    public TransferSnapshot? Snapshot(TargetId target) => _items.GetValueOrDefault(target);
    public IReadOnlyList<TransferSnapshot> All() => _items.Values.OrderBy(x => x.StartedAt).ToArray();

    public void Start(TargetId target, long? expected) =>
        _items[target] = new(target, "running", 0, expected, DateTimeOffset.UtcNow, null);

    public void Progress(TargetId target, long bytes) =>
        _items.AddOrUpdate(target,
            _ => new(target, "running", bytes, null, DateTimeOffset.UtcNow, null),
            (_, old) => old with { Bytes = bytes });

    public void Finish(TargetId target, string state, string? error = null) =>
        _items.AddOrUpdate(target,
            _ => new(target, state, 0, null, DateTimeOffset.UtcNow, error),
            (_, old) => old with { State = state, Error = error });
}
