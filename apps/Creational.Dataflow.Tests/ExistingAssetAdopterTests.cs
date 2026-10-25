using System.Security.Cryptography;
using Creational.Dataflow.Storage;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public class ExistingAssetAdopterTests
{
    [Fact]
    public async Task MissingMemberIsNotAdopted()
    {
        var definition = Definition();
        var repository = new MemoryRepository();
        var catalog = new FixedCatalog(Manifest(definition));
        var adopter = new ExistingAssetAdopter(repository, catalog, new TargetVerifier(new EmptyStore()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adopter.AdoptAsync([definition], CancellationToken.None));
        Assert.Empty(await repository.GetCollectionAsync(definition.Id.Collection, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteGroupAdoptsIdempotently()
    {
        var definition = Definition();
        var repository = new MemoryRepository();
        var catalog = new FixedCatalog(Manifest(definition));
        var blobs = new MemoryStore();
        blobs.Add("output/a", "alpha");
        var adopter = new ExistingAssetAdopter(repository, catalog, new TargetVerifier(blobs));
        var first = await adopter.AdoptAsync([definition], CancellationToken.None);
        var second = await adopter.AdoptAsync([definition], CancellationToken.None);
        Assert.Equal(1, first.Adopted);
        Assert.Equal(0, second.Adopted);
        Assert.Equal(1, second.AlreadyCurrent);
    }

    [Fact]
    public async Task MissingRequiredDescriptorIsNotAdopted()
    {
        var definition = Definition() with { OutputNames = ["a", "b"] };
        var repository = new MemoryRepository();
        var blobs = new MemoryStore();
        blobs.Add("output/a", "alpha");
        var adopter = new ExistingAssetAdopter(repository, new FixedCatalog(Manifest(definition)), new TargetVerifier(blobs));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adopter.AdoptAsync([definition], CancellationToken.None));
    }

    [Fact]
    public async Task VerifyOnlyDoesNotReadOrWriteTable()
    {
        var definition = Definition();
        var blobs = new MemoryStore();
        blobs.Add("output/a", "alpha");
        var adopter = new ExistingAssetAdopter(new DeniedRepository(), new FixedCatalog(Manifest(definition)), new TargetVerifier(blobs));
        var report = await adopter.VerifyOnlyAsync([definition], CancellationToken.None);
        Assert.Equal(1, report.Adopted);
    }

    [Fact]
    public async Task BootstrapCannotSatisfyAnOperatorsStaleMark()
    {
        var definition = Definition();
        var repository = new MemoryRepository();
        var blobs = new MemoryStore();
        blobs.Add("output/a", "alpha");
        var adopter = new ExistingAssetAdopter(repository, new FixedCatalog(Manifest(definition)), new TargetVerifier(blobs));
        await adopter.AdoptAsync([definition], CancellationToken.None);
        await repository.MarkStaleAsync(definition.Id, "refresh", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adopter.AdoptAsync([definition], CancellationToken.None));
        var state = (await repository.GetCollectionAsync(definition.Id.Collection, CancellationToken.None))[definition.Id];
        Assert.True(state.IsStale);
        Assert.Equal("marker", state.ManifestPath);
    }

    [Fact]
    public async Task BootstrapCannotRestoreAnOldPointerOverRegeneratedOutput()
    {
        var definition = Definition();
        var repository = new MemoryRepository();
        var blobs = new MemoryStore();
        blobs.Add("output/a", "alpha");
        var adopter = new ExistingAssetAdopter(repository, new FixedCatalog(Manifest(definition)), new TargetVerifier(blobs));
        await adopter.AdoptAsync([definition], CancellationToken.None);
        await repository.BeginAttemptAsync(definition.Id, "new", 0, CancellationToken.None);
        await repository.CompleteAttemptAsync(definition.Id, "new", 0, "new-marker", "new-fingerprint",
            "v1", new Dictionary<string, string>(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adopter.AdoptAsync([definition], CancellationToken.None));
        var state = (await repository.GetCollectionAsync(definition.Id.Collection, CancellationToken.None))[definition.Id];
        Assert.Equal("new-marker", state.ManifestPath);
    }

    private sealed class DeniedRepository : ITargetRepository
    {
        public Task<TargetState?> GetTargetAsync(TargetId id, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken ct) =>
            throw new UnauthorizedAccessException();
        public Task<TargetState> MarkStaleAsync(TargetId id, string reason, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> BeginAttemptAsync(TargetId id, string attemptId, long revision, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> QueueAttemptAsync(TargetId id, string attemptId, long revision, string input, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> NoteEnqueuedAsync(TargetId id, string attemptId, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> BeginLaunchAsync(TargetId id, string attemptId, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> MarkLaunchUncertainAsync(TargetId id, string attemptId, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> AttachExecutionAsync(TargetId id, string attemptId, string execution, CancellationToken ct) => throw new UnauthorizedAccessException();
        public Task<TargetState> CompleteAttemptAsync(TargetId id, string attemptId, long revision, string marker,
            string fingerprint, string recipe, IReadOnlyDictionary<string, string> inputs, CancellationToken ct,
            string? warning = null) => throw new UnauthorizedAccessException();
        public Task<TargetState> FailAttemptAsync(TargetId id, string attemptId, string error, CancellationToken ct) => throw new UnauthorizedAccessException();
    }

    private static TargetDefinition Definition() => new(new("collection", "ott-source", "all"), "v1", [], [], ["a"]);
    private static OutputManifest Manifest(TargetDefinition definition) => new(definition.Id, definition.RecipeVersion,
        [new("a", "output/a", 5, Convert.ToHexString(SHA256.HashData("alpha"u8.ToArray())).ToLowerInvariant(), "text")],
        new Dictionary<string, string>());

    private sealed class FixedCatalog(OutputManifest manifest) : IManifestCatalog
    {
        public Task<CatalogEntry?> LoadAsync(TargetDefinition definition, CancellationToken ct) =>
            Task.FromResult<CatalogEntry?>(new("marker", manifest));
        public Task PublishAsync(CatalogEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    private class EmptyStore : IBlobStore
    {
        public virtual Task<BlobInfo?> HeadAsync(string path, CancellationToken ct) => Task.FromResult<BlobInfo?>(null);
        public virtual Task<Stream> OpenReadAsync(string path, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class MemoryStore : EmptyStore
    {
        private readonly Dictionary<string, byte[]> _data = [];
        public void Add(string path, string value) => _data[path] = System.Text.Encoding.UTF8.GetBytes(value);
        public override Task<BlobInfo?> HeadAsync(string path, CancellationToken ct) =>
            Task.FromResult(_data.TryGetValue(path, out var bytes)
                ? new BlobInfo(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) : null);
    }

    private sealed class MemoryRepository : ITargetRepository
    {
        private readonly Dictionary<TargetId, TargetState> _states = [];
        public Task<TargetState?> GetTargetAsync(TargetId id, CancellationToken ct) =>
            Task.FromResult<TargetState?>(_states.GetValueOrDefault(id));
        public Task<IReadOnlyDictionary<TargetId, TargetState>> GetCollectionAsync(string collection, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<TargetId, TargetState>>(_states.Where(kv => kv.Key.Collection == collection)
                .ToDictionary(kv => kv.Key, kv => kv.Value));
        public Task<TargetState> MarkStaleAsync(TargetId id, string reason, CancellationToken ct) => Set(id, Get(id).MarkStale(reason));
        public Task<TargetState> BeginAttemptAsync(TargetId id, string attemptId, long revision, CancellationToken ct) =>
            Set(id, Get(id).BeginAttempt(attemptId, revision));
        public Task<TargetState> QueueAttemptAsync(TargetId id, string attemptId, long revision, string input, CancellationToken ct) =>
            Set(id, Get(id).QueueAttempt(attemptId, revision, input));
        public Task<TargetState> NoteEnqueuedAsync(TargetId id, string attemptId, CancellationToken ct) =>
            Set(id, Get(id).NoteEnqueued(attemptId));
        public Task<TargetState> BeginLaunchAsync(TargetId id, string attemptId, CancellationToken ct) =>
            Set(id, Get(id).BeginLaunch(attemptId));
        public Task<TargetState> MarkLaunchUncertainAsync(TargetId id, string attemptId, CancellationToken ct) =>
            Set(id, Get(id).MarkLaunchUncertain(attemptId));
        public Task<TargetState> AttachExecutionAsync(TargetId id, string attemptId, string executionId, CancellationToken ct) =>
            Set(id, Get(id).AttachExecution(attemptId, executionId));
        public Task<TargetState> CompleteAttemptAsync(TargetId id, string attemptId, long revision,
            string marker, string fingerprint, string recipe, IReadOnlyDictionary<string, string> inputs,
            CancellationToken ct, string? warning = null) =>
            Set(id, Get(id).CompleteAttempt(attemptId, revision, marker, fingerprint, recipe, inputs, warning));
        public Task<TargetState> FailAttemptAsync(TargetId id, string attemptId, string error, CancellationToken ct) =>
            Set(id, Get(id).FailAttempt(attemptId, error));
        private TargetState Get(TargetId id) => _states.TryGetValue(id, out var state) ? state : TargetState.Empty(id);
        private Task<TargetState> Set(TargetId id, TargetState state) { _states[id] = state; return Task.FromResult(state); }
    }
}
