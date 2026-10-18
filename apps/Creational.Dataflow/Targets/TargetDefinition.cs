namespace Creational.Dataflow.Targets;

public sealed record TargetDefinition(
    TargetId Id,
    string RecipeVersion,
    IReadOnlyList<TargetId> ContentDependencies,
    IReadOnlyList<TargetId> ReadinessGates,
    IReadOnlyList<string> OutputNames)
{
    public bool HasRequiredArtifacts(OutputManifest manifest) =>
        OutputNames.All(name => manifest.Artifacts.Any(artifact => artifact.Name == name));
}
