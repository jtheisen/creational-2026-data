using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class TargetPlannerTests
{
    [Fact]
    public void AllCurrentGraphSchedulesNothing()
    {
        var (definitions, states, checks) = Fixture();
        var plan = new TargetPlanner().Plan(definitions, states, checks);
        Assert.All(plan.Targets, t => Assert.Equal(TargetDisposition.Current, t.Disposition));
    }

    [Fact]
    public void StaleParseRunsAndBlocksCorrelationUntilItFinishes()
    {
        var (definitions, states, checks) = Fixture();
        var parse = definitions.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p1").Id;
        states[parse] = states[parse].MarkStale("operator");
        var plan = new TargetPlanner().Plan(definitions, states, checks);
        Assert.Equal(TargetDisposition.Run, plan[parse].Disposition);
        Assert.Equal(TargetDisposition.Blocked, plan[definitions.Single(t => t.Id.Kind == "parse-finalize").Id].Disposition);
        Assert.Equal(TargetDisposition.Current, plan[definitions.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p2").Id].Disposition);
    }

    [Fact]
    public void ChangedOnePoolShardInvalidatesOnlyItsParser()
    {
        var (definitions, states, checks) = Fixture();
        var pool = definitions.Single(t => t.Id.Kind == "candidate-pool-shard" && t.Id.Key == "p1").Id;
        var finalizer = definitions.Single(t => t.Id.Kind == "candidate-pool-finalize").Id;
        states[pool] = states[pool] with { Fingerprint = "changed" };
        states[finalizer] = states[finalizer] with
        {
            Fingerprint = "changed",
            InputFingerprints = new Dictionary<string, string>(states[finalizer].InputFingerprints)
            {
                [pool.ToString()] = "changed"
            }
        };
        checks[pool] = new(true, "changed", null);
        checks[finalizer] = new(true, "changed", null);
        var plan = new TargetPlanner().Plan(definitions, states, checks);
        Assert.Equal(TargetDisposition.Run, plan[definitions.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p1").Id].Disposition);
        Assert.Equal(TargetDisposition.Current, plan[definitions.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p2").Id].Disposition);
    }

    [Fact]
    public void StaleDumpBlocksDescendantsUntilResultIsKnown()
    {
        var (definitions, states, checks) = Fixture();
        var dump = definitions.Single(t => t.Id.Kind == "dump-shard" && t.Id.Key == "p1").Id;
        states[dump] = states[dump].MarkStale("recopy");
        var plan = new TargetPlanner().Plan(definitions, states, checks);
        Assert.Equal(TargetDisposition.Run, plan[dump].Disposition);
        Assert.Equal(TargetDisposition.Blocked, plan[definitions.Single(t => t.Id.Kind == "alias-index" && t.Id.Key == "p1").Id].Disposition);
    }

    [Fact]
    public void FailedAttemptIsScheduledForManualRetryEvenWithOldOutput()
    {
        var (definitions, states, checks) = Fixture();
        var parse = definitions.Single(t => t.Id.Kind == "parse-shard" && t.Id.Key == "p1").Id;
        states[parse] = states[parse] with { Outcome = "failed", Error = "worker crashed" };
        var plan = new TargetPlanner().Plan(definitions, states, checks);
        Assert.Equal(TargetDisposition.Run, plan[parse].Disposition);
    }

    [Fact]
    public void AddingImageTargetsKeepsRecordedLegacyWorkCurrent()
    {
        var (definitions, states, checks) = Fixture();
        var newKinds = new HashSet<string> { "source-set", "enwiki-image-dump", "commons-image-dump",
            "enwiki-image-metadata", "commons-image-metadata", "resolved-image-metadata" };
        foreach (var target in definitions.Where(t => newKinds.Contains(t.Id.Kind)))
        {
            states.Remove(target.Id);
            checks.Remove(target.Id);
        }

        var plan = new TargetPlanner().Plan(definitions, states, checks);

        Assert.All(definitions.Where(t => !newKinds.Contains(t.Id.Kind)),
            target => Assert.Equal(TargetDisposition.Current, plan[target.Id].Disposition));
        Assert.Equal(TargetDisposition.Run, plan[definitions.Single(t => t.Id.Kind == "source-set").Id].Disposition);
        Assert.All(definitions.Where(t => t.Id.Kind.EndsWith("-image-dump")),
            target => Assert.Equal(TargetDisposition.Blocked, plan[target.Id].Disposition));
    }

    private static (IReadOnlyList<TargetDefinition>, Dictionary<TargetId, TargetState>, Dictionary<TargetId, VerificationResult>) Fixture()
    {
        var graph = October2026Collection.Build([
            new("p1", "a.xml.bz2", 1, "a"), new("p2", "b.xml.bz2", 1, "b")]);
        var states = new Dictionary<TargetId, TargetState>();
        var checks = new Dictionary<TargetId, VerificationResult>();
        foreach (var target in graph)
        {
            var inputs = target.ContentDependencies.ToDictionary(d => d.ToString(), _ => "same");
            states[target.Id] = TargetState.Empty(target.Id) with
            {
                ManifestPath = "manifest", Fingerprint = "same", RecipeVersion = target.RecipeVersion,
                InputFingerprints = inputs, Outcome = "complete"
            };
            checks[target.Id] = new(true, "same", null);
        }
        return (graph, states, checks);
    }
}
