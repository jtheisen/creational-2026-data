using Creational.Dataflow.Images;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class ThumbnailBatchViewTests
{
    [Fact]
    public void MetadataWindowMarksUnfinishedImagesInProgressAtAbsoluteOffsets()
    {
        var manifest = new ThumbnailBatchManifest("batch", [
            new ImageCandidate("File:First.jpg", "First", "taxobox"),
            new ImageCandidate("File:Second.jpg", "Second", "taxobox"),
            new ImageCandidate("File:Third.jpg", "Third", "taxobox")]);
        var items = new[] { new ThumbnailBatchItem("batch", 100, "File:First.jpg", "skipped") };

        var rows = ThumbnailBatchView.Build(manifest, 100, items,
            new ThumbnailBatchActivity("batch", ThumbnailBatchWorkStage.Metadata, 100, 2));

        Assert.Equal(["skipped", "in-progress", "pending"], rows.Select(x => x.CopyStatus));
        Assert.Equal(ThumbnailBatchWorkStage.Metadata, rows[1].WorkStage);
        Assert.Equal([100, 101, 102], rows.Select(x => x.Offset));
        Assert.Equal([101], ThumbnailBatchView.Filter(rows, ThumbnailBatchFilter.InProgress).Select(x => x.Offset));
        Assert.Equal([100], ThumbnailBatchView.Filter(rows, ThumbnailBatchFilter.Skipped).Select(x => x.Offset));
        Assert.Equal([102], ThumbnailBatchView.Filter(rows, ThumbnailBatchFilter.Pending).Select(x => x.Offset));
    }

    [Fact]
    public void AllIncludesCopiedAndFailedRowsWhileCopyingOnlyMarksCurrentImage()
    {
        var manifest = new ThumbnailBatchManifest("batch", [
            new ImageCandidate("File:First.jpg", "First", "taxobox"),
            new ImageCandidate("File:Second.jpg", "Second", "taxobox"),
            new ImageCandidate("File:Third.jpg", "Third", "taxobox"),
            new ImageCandidate("File:Fourth.jpg", "Fourth", "taxobox")]);
        var items = new[] {
            new ThumbnailBatchItem("batch", 0, "File:First.jpg", "copied", 100),
            new ThumbnailBatchItem("batch", 1, "File:Second.jpg", "failed", Error: "missing") };

        var rows = ThumbnailBatchView.Build(manifest, 0, items,
            new ThumbnailBatchActivity("batch", ThumbnailBatchWorkStage.Copy, 2, 1));

        Assert.Equal(["copied", "failed", "in-progress", "pending"],
            ThumbnailBatchView.Filter(rows, ThumbnailBatchFilter.All).Select(x => x.CopyStatus));
        Assert.Equal(ThumbnailBatchWorkStage.Copy, rows[2].WorkStage);
        Assert.Equal(100, rows[0].Bytes);
        Assert.Equal("missing", rows[1].Error);
    }
}
