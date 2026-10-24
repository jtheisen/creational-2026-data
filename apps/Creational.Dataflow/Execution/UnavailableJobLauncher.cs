using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Execution;

public sealed class UnavailableJobLauncher : IComputeJobLauncher, IResizeJobLauncher
{
    public Task<string> StartAsync(TargetId target, string inputManifestUri, string outputPrefix,
        string recipeVersion, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Configure ContainerJob:ResourceId and ContainerJob:Image before running compute targets");

    public Task<string> StartResizeAsync(string runId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Configure ContainerJob:ResourceId and ContainerJob:Image before resizing thumbnails");

    public Task<ComputeExecutionStatus> GetStatusAsync(string executionId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Container Apps Job launcher is not configured");
}
