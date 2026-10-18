namespace Creational.Dataflow.Targets;

public sealed class TargetPlanner
{
    public ImportPlan Plan(IReadOnlyList<TargetDefinition> definitions,
        IReadOnlyDictionary<TargetId, TargetState> states,
        IReadOnlyDictionary<TargetId, VerificationResult> verifications)
    {
        var byId = definitions.ToDictionary(d => d.Id);
        var order = new List<TargetDefinition>();
        var visiting = new HashSet<TargetId>();
        var visited = new HashSet<TargetId>();

        void Visit(TargetId id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException($"Target graph has a cycle at {id}");
            if (!byId.TryGetValue(id, out var definition)) throw new InvalidOperationException($"Unknown target {id}");
            foreach (var dependency in definition.ContentDependencies.Concat(definition.ReadinessGates)) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
            order.Add(definition);
        }

        foreach (var definition in definitions) Visit(definition.Id);
        var planned = new Dictionary<TargetId, PlannedTarget>();
        foreach (var definition in order)
        {
            var blocked = definition.ContentDependencies.Concat(definition.ReadinessGates)
                .FirstOrDefault(id => planned[id].Disposition != TargetDisposition.Current);
            if (blocked is not null)
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Blocked, $"Waiting for {blocked}");
                continue;
            }
            if (!states.TryGetValue(definition.Id, out var state) || state.ManifestPath is null)
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Run, "Missing output group");
                continue;
            }
            if (state.AttemptId is not null)
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Blocked, "Attempt running");
                continue;
            }
            if (state.Outcome == "failed")
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Run, $"Retry failed attempt: {state.Error}");
                continue;
            }
            if (state.IsStale)
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Run, $"Marked stale: {state.StaleReason}");
                continue;
            }
            if (state.RecipeVersion != definition.RecipeVersion)
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Run, "Recipe version changed");
                continue;
            }
            if (!verifications.TryGetValue(definition.Id, out var verification) || !verification.Valid ||
                verification.Fingerprint != state.Fingerprint)
            {
                planned[definition.Id] = new(definition.Id, TargetDisposition.Run, verification?.Error ?? "Output verification failed");
                continue;
            }
            var changed = definition.ContentDependencies.FirstOrDefault(id =>
                !state.InputFingerprints.TryGetValue(id.ToString(), out var recorded) ||
                recorded != states[id].Fingerprint);
            planned[definition.Id] = changed is null
                ? new(definition.Id, TargetDisposition.Current, "Verified current")
                : new(definition.Id, TargetDisposition.Run, $"Input changed: {changed}");
        }
        return new(order.Select(d => planned[d.Id]).ToArray());
    }
}
