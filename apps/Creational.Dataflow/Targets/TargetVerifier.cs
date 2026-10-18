using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Creational.Dataflow.Storage;

namespace Creational.Dataflow.Targets;

public sealed class TargetVerifier(IBlobStore blobs)
{
    public async Task<VerificationResult> VerifyAsync(OutputManifest manifest, CancellationToken cancellationToken)
    {
        if ((manifest.Artifacts.Count == 0 && manifest.Target.Kind != "parse-finalize") ||
            manifest.Artifacts.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Artifacts.Count)
            return new(false, null, "Manifest has no artifacts or duplicate names");

        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.Length < 0 || artifact.Sha256.Length != 64 || artifact.Path.Length == 0)
                return new(false, null, $"Invalid descriptor for {artifact.Name}");
            var info = await blobs.HeadAsync(artifact.Path, cancellationToken);
            if (info is null) return new(false, null, $"Missing artifact {artifact.Name}");
            if (info.Length != artifact.Length) return new(false, null, $"Length mismatch for {artifact.Name}");
            var hash = info.Sha256;
            if (hash is null)
            {
                await using var stream = await blobs.OpenReadAsync(artifact.Path, cancellationToken);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            }
            if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(false, null, $"SHA-256 mismatch for {artifact.Name}");
        }

        var canonical = new StringBuilder().Append(manifest.RecipeVersion).Append('\n');
        if (manifest.Target.Kind == "parse-finalize")
            foreach (var input in manifest.InputFingerprints.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                canonical.Append(input.Key).Append('\0').Append(input.Value).Append('\n');
        foreach (var artifact in manifest.Artifacts.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (artifact.Name == "report.json" ||
                (manifest.Target.Kind == "source-manifest" && artifact.Name == "_COMPLETE.json") ||
                (manifest.Target.Kind == "candidate-pool-finalize" && artifact.Name == "_COMPLETE.json"))
                continue;
            canonical.Append(artifact.Name).Append('\0').Append(artifact.Format).Append('\0');
            if ((manifest.Target.Kind == "alias-index" && artifact.Name == "index.json") ||
                (manifest.Target.Kind == "alias-seed" && artifact.Name == "aliases.json") ||
                (manifest.Target.Kind == "candidate-pool-finalize" && artifact.Name == "aliases-discovered.json"))
            {
                var property = manifest.Target.Kind == "alias-index" ? "redirects" : "aliases";
                await using var stream = await blobs.OpenReadAsync(artifact.Path, cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var pair in json.RootElement.GetProperty(property).EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    canonical.Append(pair.Name).Append('=').Append(pair.Value.GetString()).Append('\0');
            }
            else
                canonical.Append(artifact.Length).Append('\0').Append(artifact.Sha256.ToLowerInvariant());
            canonical.Append('\n');
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        return new(true, fingerprint, null);
    }
}
