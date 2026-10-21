using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using Creational.Dataflow.Targets;

namespace Creational.Dataflow.Transfers;

public sealed record CopyTarget(TargetId Id, string SourceUrl, string Destination,
    long ExpectedLength, string ExpectedSha256, string? OldVersionId, string? OldETag = null)
{
    public string? ExpectedSha1 { get; init; }
}

public sealed record CopyResult(bool Success, long Bytes, string Sha256, string? CleanupError);

public interface IBlockBlobWriter
{
    Task StageAsync(string destination, string blockId, byte[] bytes, int count, CancellationToken ct);
    Task CommitAsync(string destination, IReadOnlyList<string> blockIds, long length, string sha256,
        string? expectedCurrentETag, CancellationToken ct);
    Task<bool> VerifyAsync(string destination, long length, string sha256, CancellationToken ct);
    Task DeleteVersionAsync(string destination, string versionId, CancellationToken ct);
}

public interface IStreamingCopier
{
    Task<CopyResult> CopyAsync(CopyTarget target, CancellationToken cancellationToken);
}

public sealed class StreamingBlobCopier(HttpClient http, IBlockBlobWriter blobs, TransferLedger ledger,
    int blockSize = 4 * 1024 * 1024, TimeSpan? retryDelay = null) : IStreamingCopier
{
    public async Task<CopyResult> CopyAsync(CopyTarget target, CancellationToken cancellationToken)
    {
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        ledger.Start(target.Id, target.ExpectedLength);
        try
        {
            using var response = await GetWithRetryAsync(target.SourceUrl, cancellationToken);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1Digest = target.ExpectedSha1 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var blocks = new List<string>();
            var session = Guid.NewGuid().ToString("N");
            var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
            long bytes = 0;
            try
            {
                while (true)
                {
                    var count = 0;
                    while (count < blockSize)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(count, blockSize - count), cancellationToken);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count == 0) break;
                    digest.AppendData(buffer, 0, count);
                    sha1Digest?.AppendData(buffer, 0, count);
                    var blockId = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{session}-{blocks.Count:D8}"));
                    await blobs.StageAsync(target.Destination, blockId, buffer, count, cancellationToken);
                    blocks.Add(blockId);
                    bytes += count;
                    ledger.Progress(target.Id, bytes);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            var sha256 = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var sha1 = sha1Digest is null ? null : Convert.ToHexString(sha1Digest.GetHashAndReset()).ToLowerInvariant();
            if (bytes != target.ExpectedLength ||
                (target.ExpectedSha256.Length > 0 && !string.Equals(sha256, target.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) ||
                (target.ExpectedSha1 is not null && !string.Equals(sha1, target.ExpectedSha1, StringComparison.OrdinalIgnoreCase)) ||
                (target.ExpectedSha256.Length == 0 && target.ExpectedSha1 is null))
                throw new InvalidDataException($"Source length or checksum mismatch for {target.Id}");
            await blobs.CommitAsync(target.Destination, blocks, bytes, sha256, target.OldETag, cancellationToken);
            if (!await blobs.VerifyAsync(target.Destination, bytes, sha256, cancellationToken))
                throw new InvalidDataException($"Committed Blob verification failed for {target.Id}");
            string? cleanupError = target.OldETag is not null && target.OldVersionId is null
                ? "Previous Blob predates versioning and has no version ID; old version was retained"
                : null;
            if (target.OldVersionId is { Length: > 0 })
            {
                try { await blobs.DeleteVersionAsync(target.Destination, target.OldVersionId, cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                { cleanupError = error.Message; }
            }
            ledger.Finish(target.Id, "complete", cleanupError);
            return new(true, bytes, sha256, cleanupError);
        }
        catch (Exception error)
        {
            ledger.Finish(target.Id, "failed", error.Message);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;
            if (response.StatusCode is not (HttpStatusCode)429 and not HttpStatusCode.ServiceUnavailable || attempt == 3)
            {
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException($"Source HTTP {(int)response.StatusCode}");
            }
            response.Dispose();
            await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
        throw new InvalidOperationException("Unreachable retry state");
    }
}
