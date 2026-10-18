using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

namespace Creational.Dataflow.Storage;

public sealed class AzureBlobStore(BlobServiceClient service) : IBlobStore
{
    public BlobClient Client(string path)
    {
        var uri = new Uri(path, UriKind.Absolute);
        if (!string.Equals(uri.Host, service.Uri.Host, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Blob path is outside the configured storage account", nameof(path));
        var pieces = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        if (pieces.Length != 2 || pieces[0].Length == 0 || pieces[1].Length == 0)
            throw new ArgumentException("Blob path must contain a container and name", nameof(path));
        return service.GetBlobContainerClient(pieces[0]).GetBlobClient(Uri.UnescapeDataString(pieces[1]));
    }

    public BlockBlobClient BlockClient(string path)
    {
        var client = Client(path);
        var pieces = client.Uri.AbsolutePath.TrimStart('/').Split('/', 2);
        return service.GetBlobContainerClient(pieces[0]).GetBlockBlobClient(Uri.UnescapeDataString(pieces[1]));
    }

    public async Task<BlobInfo?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Client(path).GetPropertiesAsync(cancellationToken: cancellationToken);
            result.Value.Metadata.TryGetValue("sha256", out var sha256);
            return new(result.Value.ContentLength, sha256);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        await Client(path).OpenReadAsync(cancellationToken: cancellationToken);
}
