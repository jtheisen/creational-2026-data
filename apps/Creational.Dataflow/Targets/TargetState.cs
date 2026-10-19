namespace Creational.Dataflow.Targets;

public sealed record TargetState(
    TargetId Id,
    long StaleRevision,
    long SatisfiedStaleRevision,
    string? StaleReason,
    string? AttemptId,
    long? AttemptRevision,
    string? ManifestPath,
    string? Fingerprint,
    string? RecipeVersion,
    IReadOnlyDictionary<string, string> InputFingerprints,
    string Outcome,
    string? Error)
{
    public string? ExecutionId { get; init; }
    public DateTimeOffset? AttemptStartedAt { get; init; }
    public string? LastAttemptId { get; init; }
    public string? InputManifestUri { get; init; }
    public DateTimeOffset? LastEnqueuedAt { get; init; }
    public bool IsStale => StaleRevision > SatisfiedStaleRevision;
    public static TargetState Empty(TargetId id) => new(id, 0, 0, null, null, null, null, null, null,
        new Dictionary<string, string>(), "missing", null);

    public TargetState MarkStale(string reason) => this with
    {
        StaleRevision = checked(StaleRevision + 1), StaleReason = reason
    };

    public TargetState BeginAttempt(string attemptId, long expectedStaleRevision)
    {
        if (AttemptId is not null || StaleRevision != expectedStaleRevision)
            throw new InvalidOperationException("Attempt already active or stale revision changed");
        return this with { AttemptId = attemptId, AttemptRevision = expectedStaleRevision,
            AttemptStartedAt = DateTimeOffset.UtcNow, ExecutionId = null, Outcome = "running", Error = null };
    }

    public TargetState QueueAttempt(string attemptId, long expectedStaleRevision, string inputManifestUri)
    {
        if (AttemptId is not null || StaleRevision != expectedStaleRevision)
            throw new InvalidOperationException("Attempt already active or stale revision changed");
        return this with { AttemptId = attemptId, AttemptRevision = expectedStaleRevision,
            AttemptStartedAt = DateTimeOffset.UtcNow, InputManifestUri = inputManifestUri,
            LastEnqueuedAt = null, ExecutionId = null, Outcome = "queued", Error = null };
    }

    public TargetState NoteEnqueued(string attemptId)
    {
        if (AttemptId != attemptId || Outcome != "queued")
            throw new InvalidOperationException("Only the queued attempt can record an enqueue");
        return this with { LastEnqueuedAt = DateTimeOffset.UtcNow };
    }

    public TargetState BeginLaunch(string attemptId)
    {
        if (AttemptId != attemptId || Outcome != "queued")
            throw new InvalidOperationException("Only the queued attempt can start a worker");
        return this with { Outcome = "launching", AttemptStartedAt = DateTimeOffset.UtcNow };
    }

    public TargetState MarkLaunchUncertain(string attemptId)
    {
        if (AttemptId != attemptId || Outcome is not ("launching" or "running") || ExecutionId is not null)
            throw new InvalidOperationException("Only an unattached launch can become uncertain");
        return this with { Outcome = "launch-uncertain",
            Error = "Azure start may have succeeded; inspect executions before retrying" };
    }

    public TargetState AttachExecution(string attemptId, string executionId)
    {
        if (AttemptId != attemptId || Outcome is not ("launching" or "launch-uncertain") || ExecutionId is not null)
            throw new InvalidOperationException("Execution cannot attach to a different or already launched attempt");
        return this with { ExecutionId = executionId, Outcome = "running", Error = null };
    }

    public TargetState CompleteAttempt(string attemptId, long capturedStaleRevision, string manifestPath,
        string fingerprint, string recipeVersion, IReadOnlyDictionary<string, string> inputs,
        string? warning = null)
    {
        if (AttemptId != attemptId || AttemptRevision != capturedStaleRevision)
            throw new InvalidOperationException("Attempt identity or captured stale revision differs");
        return this with
        {
            AttemptId = null, AttemptRevision = null, AttemptStartedAt = null, ExecutionId = null,
            InputManifestUri = null, LastEnqueuedAt = null,
            LastAttemptId = attemptId,
            ManifestPath = manifestPath,
            Fingerprint = fingerprint, RecipeVersion = recipeVersion,
            InputFingerprints = inputs, SatisfiedStaleRevision = capturedStaleRevision,
            Outcome = warning is null ? "complete" : "complete-with-warning", Error = warning
        };
    }

    public TargetState FailAttempt(string attemptId, string error)
    {
        if (AttemptId != attemptId) throw new InvalidOperationException("Attempt identity differs");
        return this with { AttemptId = null, AttemptRevision = null, AttemptStartedAt = null,
            ExecutionId = null, InputManifestUri = null, LastEnqueuedAt = null,
            LastAttemptId = attemptId, Outcome = "failed",
            Error = error[..Math.Min(error.Length, 1024)] };
    }
}
