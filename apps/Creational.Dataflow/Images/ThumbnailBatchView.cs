namespace Creational.Dataflow.Images;

public enum ThumbnailBatchFilter { All, InProgress, Skipped, Pending }

public enum ThumbnailBatchWorkStage { Metadata, Copy }

public sealed record ThumbnailBatchActivity(string BatchId, ThumbnailBatchWorkStage Stage,
    int StartOffset, int Count);

public sealed record ThumbnailBatchViewItem(int Offset, string Title, string CopyStatus,
    long? Bytes = null, string? Error = null, ThumbnailBatchWorkStage? WorkStage = null);

public static class ThumbnailBatchView
{
    public static IReadOnlyList<ThumbnailBatchViewItem> Build(ThumbnailBatchManifest manifest,
        int startOffset, IReadOnlyList<ThumbnailBatchItem> terminalItems, ThumbnailBatchActivity? activity)
    {
        var done = terminalItems.Where(x => x.BatchId == manifest.BatchId)
            .ToDictionary(x => x.Offset);
        var rows = new List<ThumbnailBatchViewItem>(manifest.Images.Length);
        for (var index = 0; index < manifest.Images.Length; index++)
        {
            var offset = checked(startOffset + index);
            var candidate = manifest.Images[index];
            if (done.TryGetValue(offset, out var item) &&
                item.Title == candidate.FileTitle && item.Status is "copied" or "skipped" or "failed")
            {
                rows.Add(new(offset, candidate.FileTitle, item.Status, item.Bytes, item.Error));
                continue;
            }
            var working = activity is not null && activity.BatchId == manifest.BatchId &&
                offset >= activity.StartOffset && offset < activity.StartOffset + activity.Count;
            rows.Add(new(offset, candidate.FileTitle, working ? "in-progress" : "pending",
                WorkStage: working ? activity!.Stage : null));
        }
        return rows;
    }

    public static IReadOnlyList<ThumbnailBatchViewItem> Filter(
        IReadOnlyList<ThumbnailBatchViewItem> rows, ThumbnailBatchFilter filter) => filter switch
    {
        ThumbnailBatchFilter.All => rows,
        ThumbnailBatchFilter.InProgress => rows.Where(x => x.CopyStatus == "in-progress").ToArray(),
        ThumbnailBatchFilter.Skipped => rows.Where(x => x.CopyStatus == "skipped").ToArray(),
        ThumbnailBatchFilter.Pending => rows.Where(x => x.CopyStatus == "pending").ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };
}
