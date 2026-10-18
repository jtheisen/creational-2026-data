using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Execution;

public enum ComputeExecutionState { Running, Succeeded, Failed, Unknown }
public sealed record ComputeExecutionStatus(ComputeExecutionState State, string? Message);

public interface IComputeJobLauncher
{
    Task<string> StartAsync(TargetId target, string inputManifestUri, string outputPrefix,
        string recipeVersion, CancellationToken cancellationToken);
    Task<ComputeExecutionStatus> GetStatusAsync(string executionId, CancellationToken cancellationToken);
}
