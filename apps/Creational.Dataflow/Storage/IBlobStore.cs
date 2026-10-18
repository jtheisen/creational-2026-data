namespace Creational.Dataflow.Storage;

public sealed record BlobInfo(long Length, string? Sha256);

public interface IBlobStore
{
    Task<BlobInfo?> HeadAsync(string path, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
}
