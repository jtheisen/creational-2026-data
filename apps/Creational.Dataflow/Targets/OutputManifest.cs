namespace Creational.Dataflow.Targets;

public sealed record ArtifactDescriptor(string Name, string Path, long Length, string Sha256, string Format);

public sealed record OutputManifest(
    TargetId Target,
    string RecipeVersion,
    IReadOnlyList<ArtifactDescriptor> Artifacts,
    IReadOnlyDictionary<string, string> InputFingerprints);

public sealed record VerificationResult(bool Valid, string? Fingerprint, string? Error);
