using Creational.Dataflow.Storage;

namespace Creational.Dataflow.Targets;

public sealed record CatalogEntry(string MarkerPath, OutputManifest Manifest, bool NeedsPublication = false);

public interface IManifestCatalog
{
    Task<CatalogEntry?> LoadAsync(TargetDefinition definition, CancellationToken cancellationToken);
    Task PublishAsync(CatalogEntry entry, CancellationToken cancellationToken);
}

public sealed record AdoptionReport(int Adopted, int AlreadyCurrent, int Failed);

public sealed class ExistingAssetAdopter(ITargetRepository repository, IManifestCatalog catalog, TargetVerifier verifier)
{
    public async Task<AdoptionReport> AdoptAsync(IReadOnlyList<TargetDefinition> definitions, CancellationToken cancellationToken)
        => await RunAsync(definitions, true, cancellationToken);

    public async Task<AdoptionReport> VerifyOnlyAsync(IReadOnlyList<TargetDefinition> definitions, CancellationToken cancellationToken)
        => await RunAsync(definitions, false, cancellationToken);

    private async Task<AdoptionReport> RunAsync(IReadOnlyList<TargetDefinition> definitions, bool apply, CancellationToken cancellationToken)
    {
        if (definitions.Count == 0) return new(0, 0, 0);
        var states = apply
            ? new Dictionary<TargetId, TargetState>(await repository.GetCollectionAsync(definitions[0].Id.Collection, cancellationToken))
            : new Dictionary<TargetId, TargetState>();
        var adopted = 0;
        var already = 0;
        foreach (var definition in definitions)
        {
            var entry = await catalog.LoadAsync(definition, cancellationToken)
                ?? throw new InvalidOperationException($"No existing output group for {definition.Id}");
            if (entry.Manifest.Target != definition.Id || entry.Manifest.RecipeVersion != definition.RecipeVersion)
                throw new InvalidOperationException($"Manifest identity or recipe differs for {definition.Id}");
            if (!definition.HasRequiredArtifacts(entry.Manifest))
                throw new InvalidOperationException($"Manifest omits required artifacts for {definition.Id}");
            var inputs = new Dictionary<string, string>();
            foreach (var dependency in definition.ContentDependencies)
            {
                if (!states.TryGetValue(dependency, out var source) || source.Fingerprint is null)
                    throw new InvalidOperationException($"Cannot adopt {definition.Id} before {dependency}");
                inputs[dependency.ToString()] = source.Fingerprint;
            }
            if (entry.Manifest.InputFingerprints.Count > 0 &&
                (entry.Manifest.InputFingerprints.Count != inputs.Count ||
                 inputs.Any(kv => !entry.Manifest.InputFingerprints.TryGetValue(kv.Key, out var value) || value != kv.Value)))
                throw new InvalidOperationException($"Recorded input fingerprints differ for {definition.Id}");
            entry = entry with { Manifest = entry.Manifest with { InputFingerprints = inputs } };
            var check = await verifier.VerifyAsync(entry.Manifest, cancellationToken);
            if (!check.Valid)
                throw new InvalidOperationException($"Cannot adopt {definition.Id}: {check.Error}");
            if (states.TryGetValue(definition.Id, out var old) && old.ManifestPath == entry.MarkerPath &&
                old.Fingerprint == check.Fingerprint && old.RecipeVersion == definition.RecipeVersion &&
                !old.IsStale && inputs.All(kv => old.InputFingerprints.TryGetValue(kv.Key, out var value) && value == kv.Value))
            {
                already++;
                continue;
            }
            if (old?.ManifestPath is not null)
                throw new InvalidOperationException($"{definition.Id} is already tracked with a different output or stale revision; bootstrap cannot replace it");
            if (!apply)
            {
                adopted++;
                states[definition.Id] = TargetState.Empty(definition.Id) with
                {
                    ManifestPath = entry.MarkerPath, Fingerprint = check.Fingerprint,
                    RecipeVersion = definition.RecipeVersion, InputFingerprints = inputs, Outcome = "complete"
                };
                continue;
            }
            await catalog.PublishAsync(entry, cancellationToken);
            var current = old ?? TargetState.Empty(definition.Id);
            var attempt = "adopt-" + Guid.NewGuid().ToString("N");
            await repository.BeginAttemptAsync(definition.Id, attempt, current.StaleRevision, cancellationToken);
            var finished = await repository.CompleteAttemptAsync(definition.Id, attempt, current.StaleRevision,
                entry.MarkerPath, check.Fingerprint!, definition.RecipeVersion, inputs, cancellationToken);
            states[definition.Id] = finished;
            adopted++;
        }
        return new(adopted, already, 0);
    }
}
