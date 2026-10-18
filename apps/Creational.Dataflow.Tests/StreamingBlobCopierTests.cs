using System.Net;
using System.Security.Cryptography;
using System.Text;
using Creational.Dataflow.Targets;
using Creational.Dataflow.Transfers;
using Xunit;

namespace Creational.Dataflow.Tests;

public class StreamingBlobCopierTests
{
    [Fact]
    public async Task HashMismatchLeavesCurrentBlobUncommitted()
    {
        var writer = new FakeWriter();
        var copier = NewCopier(writer, _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("wrong") });
        var target = CopyTarget("expected", 5, "old-version");
        await Assert.ThrowsAsync<InvalidDataException>(() => copier.CopyAsync(target, CancellationToken.None));
        Assert.False(writer.Committed);
        Assert.Null(writer.DeletedVersion);
    }

    [Fact]
    public async Task VerifiedReplacementDeletesOnlyCapturedOldVersion()
    {
        var writer = new FakeWriter();
        var payload = "hello";
        var copier = NewCopier(writer, _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(payload) });
        var result = await copier.CopyAsync(CopyTarget(Hash(payload), 5, "old-version"), CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(writer.Committed);
        Assert.Equal("old-version", writer.DeletedVersion);
        Assert.Equal("\"etag-old\"", writer.CommitETag);
        Assert.Equal(5, result.Bytes);
    }

    [Fact]
    public async Task ThrottledSourceRetriesBeforeStaging()
    {
        var requests = 0;
        var writer = new FakeWriter();
        var copier = NewCopier(writer, _ =>
        {
            requests++;
            return requests == 1 ? new HttpResponseMessage((HttpStatusCode)429)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello") };
        });
        await copier.CopyAsync(CopyTarget(Hash("hello"), 5, null), CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.True(writer.Committed);
    }

    [Fact]
    public async Task LegacyCurrentWithoutVersionIdCopiesButRecordsCleanupWarning()
    {
        var writer = new FakeWriter();
        var copier = NewCopier(writer, _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("hello") });
        var target = CopyTarget(Hash("hello"), 5, null) with { OldETag = "\"legacy-etag\"" };
        var result = await copier.CopyAsync(target, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("\"legacy-etag\"", writer.CommitETag);
        Assert.Contains("no version ID", result.CleanupError);
        Assert.Null(writer.DeletedVersion);
    }

    [Fact]
    public async Task ShortNetworkReadsArePackedIntoFullBlocks()
    {
        var writer = new FakeWriter();
        var payload = Encoding.UTF8.GetBytes("abcdefghij");
        var copier = NewCopier(writer, _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ShortReadStream(payload)) });
        var target = CopyTarget(Hash("abcdefghij"), payload.Length, null);
        await copier.CopyAsync(target, CancellationToken.None);
        Assert.Equal(5, writer.BlockCount);
    }

    private static CopyTarget CopyTarget(string hash, long length, string? oldVersion) => new(
        new TargetId("collection", "dump-shard", "p1"), "https://example.test/file", "destination",
        length, hash, oldVersion, oldVersion is null ? null : "\"etag-old\"");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static StreamingBlobCopier NewCopier(FakeWriter writer, Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new HttpClient(new Handler(reply)), writer, new TransferLedger(), blockSize: 2,
            retryDelay: TimeSpan.Zero);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    private sealed class FakeWriter : IBlockBlobWriter
    {
        private readonly Dictionary<string, byte[]> _blocks = [];
        public bool Committed { get; private set; }
        public string? DeletedVersion { get; private set; }
        public string? CommitETag { get; private set; }
        public int BlockCount => _blocks.Count;
        public Task StageAsync(string destination, string blockId, byte[] bytes, int count, CancellationToken ct)
        { _blocks[blockId] = bytes[..count]; return Task.CompletedTask; }
        public Task CommitAsync(string destination, IReadOnlyList<string> blockIds, long length, string sha256,
            string? expectedCurrentETag, CancellationToken ct)
        { Committed = true; CommitETag = expectedCurrentETag; return Task.CompletedTask; }
        public Task<bool> VerifyAsync(string destination, long length, string sha256, CancellationToken ct) => Task.FromResult(Committed);
        public Task DeleteVersionAsync(string destination, string versionId, CancellationToken ct)
        { DeletedVersion = versionId; return Task.CompletedTask; }
    }

    private sealed class ShortReadStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
