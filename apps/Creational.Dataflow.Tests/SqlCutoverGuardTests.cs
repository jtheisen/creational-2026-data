using Creational.Dataflow.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlCutoverGuardTests
{
    [Fact]
    public async Task LegacyImageCommandsAreRejectedDuringSqlCutover()
    {
        var images = new ThumbnailImportService(null!, null!, null!,
            NullLogger<ThumbnailImportService>.Instance, allowLegacyWrites: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            images.CreateJobAsync(["File:Example.jpg"], CancellationToken.None));
        var resizes = new ResizeSweepService(null!, null!, NullLogger<ResizeSweepService>.Instance,
            allowLegacyWrites: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resizes.RequestAsync(CancellationToken.None));
    }
}
