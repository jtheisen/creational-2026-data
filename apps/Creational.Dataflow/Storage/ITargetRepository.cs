using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Storage;

public interface ITargetRepository
{
    Task<TargetState?> GetTargetAsync(TargetId id, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken cancellationToken);
    Task<TargetState> MarkStaleAsync(TargetId id, string reason, CancellationToken cancellationToken);
    Task<TargetState> BeginAttemptAsync(TargetId id, string attemptId, long expectedStaleRevision, CancellationToken cancellationToken);
    Task<TargetState> QueueAttemptAsync(TargetId id, string attemptId, long expectedStaleRevision, string inputManifestUri, CancellationToken cancellationToken);
    Task<TargetState> NoteEnqueuedAsync(TargetId id, string attemptId, CancellationToken cancellationToken);
    Task<TargetState> BeginLaunchAsync(TargetId id, string attemptId, CancellationToken cancellationToken);
    Task<TargetState> MarkLaunchUncertainAsync(TargetId id, string attemptId, CancellationToken cancellationToken);
    Task<TargetState> AttachExecutionAsync(TargetId id, string attemptId, string executionId, CancellationToken cancellationToken);
    Task<TargetState> CompleteAttemptAsync(TargetId id, string attemptId, long capturedStaleRevision,
        string manifestPath, string fingerprint, string recipeVersion, IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken, string? warning = null);
    Task<TargetState> FailAttemptAsync(TargetId id, string attemptId, string error, CancellationToken cancellationToken);
}
