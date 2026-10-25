using System.Data;
using System.Text;
using Azure.Data.Tables;
using Azure;
using Creational.Dataflow.Sql;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Images;

public sealed record ImageAdoptionTotals(int AssetsSeen, int AssetsMatched, int MetadataCopied,
    int RenditionsSeen, int RenditionsCopied, int Quarantined);

public sealed partial class SqlImageAdopter(TableClient table, SqlConnectionFactory factory, string schema,
    SqlImageBlobStore blobs)
{
    private readonly string _schema = SqlCatalogSchema.ValidateName(schema);
    private string Table(string name) => $"[{_schema}].[{name}]";

    private async Task<ImageAdoptionTotals> RunSerialAsync(int limitPerPartition, bool apply, CancellationToken ct)
    {
        if (limitPerPartition < 1) throw new ArgumentOutOfRangeException(nameof(limitPerPartition));
        if (apply) await blobs.EnsureContainerAsync(ct);
        await using var connection = await factory.OpenAsync(ct);
        var assetRows = new List<TableEntity>();
        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_assets'",
            cancellationToken: ct)) assetRows.Add(row);
        var matches = await BuildMatchesAsync(connection, assetRows, apply, ct);
        var assetsCheckpoint = apply ? await GetCheckpointAsync(connection, "__thumbnail_assets", ct) : "";
        var renditionsCheckpoint = apply ? await GetCheckpointAsync(connection, "__thumbnail_renditions", ct) : "";
        var seenAssets = 0;
        var matchedAssets = 0;
        var metadataCopied = 0;
        var seenRenditions = 0;
        var renditionCopied = 0;
        var quarantined = 0;
        var processedAssets = 0;
        var processedRenditions = 0;
        string? lastAsset = null, lastRendition = null;
        foreach (var row in assetRows)
        {
            seenAssets++;
            var title = row.GetString("Title");
            int? imageId = title is not null && matches.TryGetValue(title, out var id) ? id : null;
            if (imageId is not null) matchedAssets++;
            if (string.CompareOrdinal(row.RowKey, assetsCheckpoint) <= 0 || processedAssets >= limitPerPartition)
                continue;
            processedAssets++;
            lastAsset = row.RowKey;
            if (imageId is null)
            {
                quarantined++;
                if (apply) await IssueAsync(connection, row.PartitionKey, row.RowKey, "unmatched-title", title, ct);
                if (apply && processedAssets % 100 == 0)
                    await SaveCheckpointAsync(connection, row.PartitionKey, row.RowKey, 100, ct);
                continue;
            }
            var status = row.GetString("MetadataStatus");
            var rawPath = row.GetString("MetadataRawPath");
            if (string.IsNullOrWhiteSpace(status) || string.IsNullOrWhiteSpace(rawPath))
            {
                if (apply && processedAssets % 100 == 0)
                    await SaveCheckpointAsync(connection, row.PartitionKey, row.RowKey, 100, ct);
                continue;
            }
            if (!apply) continue;
            try
            {
                var legacy = blobs.LegacyClient(rawPath);
                var properties = (await legacy.GetPropertiesAsync(cancellationToken: ct)).Value;
                if (!await blobs.CopyLegacyAsync(rawPath, SqlImageBlobStore.MetadataPath(imageId.Value),
                        properties.ContentLength, null, ct)) metadataCopied++;
                await SaveMetadataAsync(connection, imageId.Value, status, row, ct);
                await ClearIssueAsync(connection, row.PartitionKey, row.RowKey, ct);
            }
            catch (RequestFailedException error) when (error.Status == 404)
            {
                quarantined++;
                await IssueAsync(connection, row.PartitionKey, row.RowKey, "metadata-blob-missing", title, ct);
            }
            catch (InvalidDataException)
            {
                quarantined++;
                await IssueAsync(connection, row.PartitionKey, row.RowKey, "metadata-invalid", title, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                quarantined++;
                await IssueAsync(connection, row.PartitionKey, row.RowKey, "metadata-copy-failed", title, ct);
                throw new InvalidOperationException($"Metadata adoption stopped at {row.RowKey}: {error.Message}", error);
            }
            if (processedAssets % 100 == 0)
            {
                await SaveCheckpointAsync(connection, row.PartitionKey, row.RowKey, 100, ct);
                Console.Error.WriteLine($"Image metadata adoption: {processedAssets} processed, {metadataCopied} copied, {quarantined} issues");
            }
        }
        if (apply && lastAsset is not null)
            await SaveCheckpointAsync(connection, "__thumbnail_assets", lastAsset, processedAssets % 100, ct);

        await foreach (var row in table.QueryAsync<TableEntity>(filter: "PartitionKey eq '__thumbnail_renditions'",
            cancellationToken: ct))
        {
            if (string.CompareOrdinal(row.RowKey, renditionsCheckpoint) <= 0) continue;
            if (processedRenditions >= limitPerPartition) break;
            processedRenditions++;
            seenRenditions++;
            lastRendition = row.RowKey;
            if (row.GetString("Status") != "complete")
            {
                if (apply && processedRenditions % 100 == 0)
                    await SaveCheckpointAsync(connection, row.PartitionKey, row.RowKey, 100, ct);
                continue;
            }
            var title = row.GetString("Title");
            var width = row.GetInt32("Width");
            var bytes = row.GetInt64("Bytes");
            var hash = row.GetString("Sha256");
            var path = row.GetString("BlobName");
            if (title is null || !matches.TryGetValue(title, out var imageId) ||
                width is null || Shape(width.Value) is null || bytes is null or < 1 or > int.MaxValue ||
                string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(path))
            {
                quarantined++;
                if (apply) await IssueAsync(connection, row.PartitionKey, row.RowKey, "invalid-rendition", title, ct);
                if (apply && processedRenditions % 100 == 0)
                    await SaveCheckpointAsync(connection, row.PartitionKey, row.RowKey, 100, ct);
                continue;
            }
            if (!apply) continue;
            try
            {
                var destination = SqlImageBlobStore.RenditionPath(imageId, width.Value);
                if (!await blobs.CopyLegacyAsync(path, destination, bytes.Value, hash, ct)) renditionCopied++;
                await SaveRenditionAsync(connection, imageId, Shape(width.Value)!.Value,
                    (int)bytes.Value, row.GetBoolean("Stale") ?? false, ct);
                await ClearIssueAsync(connection, row.PartitionKey, row.RowKey, ct);
            }
            catch (RequestFailedException error) when (error.Status == 404)
            {
                quarantined++;
                await IssueAsync(connection, row.PartitionKey, row.RowKey, "rendition-blob-missing", title, ct);
            }
            catch (InvalidDataException)
            {
                quarantined++;
                await IssueAsync(connection, row.PartitionKey, row.RowKey, "rendition-invalid", title, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                quarantined++;
                await IssueAsync(connection, row.PartitionKey, row.RowKey, "rendition-copy-failed", title, ct);
                throw new InvalidOperationException($"Rendition adoption stopped at {row.RowKey}: {error.Message}", error);
            }
            if (processedRenditions % 100 == 0)
            {
                await SaveCheckpointAsync(connection, row.PartitionKey, row.RowKey, 100, ct);
                Console.Error.WriteLine($"Image rendition adoption: {processedRenditions} processed, {renditionCopied} copied, {quarantined} issues");
            }
        }
        if (apply && lastRendition is not null)
            await SaveCheckpointAsync(connection, "__thumbnail_renditions", lastRendition,
                processedRenditions % 100, ct);
        return new(seenAssets, matchedAssets, metadataCopied, seenRenditions, renditionCopied, quarantined);
    }

    private async Task<Dictionary<string, int>> BuildMatchesAsync(SqlConnection connection,
        IReadOnlyList<TableEntity> rows, bool apply, CancellationToken ct)
    {
        await using (var create = new SqlCommand("""
            CREATE TABLE #LegacyImageKeys (
                LegacyTitle VARCHAR(1536) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
                SourceWiki VARCHAR(16) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
                ResolvedTitle VARCHAR(1536) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL
            );
            """, connection)) await create.ExecuteNonQueryAsync(ct);
        var data = new DataTable();
        data.Columns.Add("LegacyTitle", typeof(string));
        data.Columns.Add("SourceWiki", typeof(string));
        data.Columns.Add("ResolvedTitle", typeof(string));
        foreach (var row in rows)
        {
            var title = row.GetString("Title");
            var resolved = row.GetString("ResolvedTitle") ?? title;
            var wiki = row.GetString("SourceWiki");
            if (title is not null && resolved is not null && wiki is not null)
                data.Rows.Add(title, wiki, resolved.Replace('_', ' ').Normalize(NormalizationForm.FormC));
        }
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#LegacyImageKeys",
                   BatchSize = 1000, BulkCopyTimeout = 300 })
        {
            bulk.ColumnMappings.Add("LegacyTitle", "LegacyTitle");
            bulk.ColumnMappings.Add("SourceWiki", "SourceWiki");
            bulk.ColumnMappings.Add("ResolvedTitle", "ResolvedTitle");
            await bulk.WriteToServerAsync(data, ct);
        }
        await using var command = new SqlCommand($"""
            SELECT l.LegacyTitle,i.ImageId FROM #LegacyImageKeys l
            JOIN {Table("Image")} i ON i.SourceWiki=l.SourceWiki AND i.CanonicalFileTitle=l.ResolvedTitle;
            """, connection) { CommandTimeout = 300 };
        var matches = new Dictionary<string, int>(StringComparer.Ordinal);
        var ownerByImage = new Dictionary<int, string>();
        var ambiguousImages = new HashSet<int>();
        var ambiguousTitles = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var title = reader.GetString(0);
            var id = reader.GetInt32(1);
            if (ownerByImage.TryGetValue(id, out var owner) && owner != title)
            {
                ambiguousImages.Add(id);
                ambiguousTitles.Add(owner);
                ambiguousTitles.Add(title);
            }
            else ownerByImage.TryAdd(id, title);
            if (!matches.TryAdd(title, id) && matches[title] != id)
                ambiguousTitles.Add(title);
        }
        await reader.DisposeAsync();
        foreach (var (title, id) in matches)
            if (ambiguousImages.Contains(id)) ambiguousTitles.Add(title);
        foreach (var title in ambiguousTitles) matches.Remove(title);
        if (ambiguousImages.Count > 0)
            Console.Error.WriteLine($"Quarantining {ambiguousTitles.Count} legacy titles resolving ambiguously to {ambiguousImages.Count} images.");
        if (apply && ambiguousImages.Count > 0)
        {
            await using var quarantine = new SqlCommand($"""
                UPDATE {Table("Image")} SET ImageInfoState=0,MetadataStale=1
                WHERE ImageId=@id;
                UPDATE {Table("Rendition")} SET Complete=0,Stale=1,Bytes=NULL
                WHERE ImageId=@id;
                """, connection);
            quarantine.Parameters.Add("@id", SqlDbType.Int);
            foreach (var id in ambiguousImages)
            {
                quarantine.Parameters["@id"].Value = id;
                await quarantine.ExecuteNonQueryAsync(ct);
            }
        }
        return matches;
    }

    private async Task SaveMetadataAsync(SqlConnection connection, int id, string status, TableEntity row, CancellationToken ct)
    {
        await using var command = new SqlCommand($"""
            UPDATE {Table("Image")}
            SET ImageInfoState=@status,MetadataStale=@stale,LicenseCategoryId=@license
            WHERE ImageId=@id;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@status", SqlDbType.TinyInt).Value = status == "complete" ? 1 : 2;
        command.Parameters.Add("@stale", SqlDbType.Bit).Value = row.GetBoolean("MetadataStale") ?? false;
        command.Parameters.Add("@license", SqlDbType.TinyInt).Value = License(row.GetString("License"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task SaveRenditionAsync(SqlConnection connection, int id, byte shape, int bytes,
        bool stale, CancellationToken ct)
    {
        await using var command = new SqlCommand($"""
            UPDATE {Table("Rendition")} SET Bytes=@bytes,Complete=1,Stale=@stale,Failed=0
            WHERE ImageId=@id AND ShapeId=@shape;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@shape", SqlDbType.TinyInt).Value = shape;
        command.Parameters.Add("@bytes", SqlDbType.Int).Value = bytes;
        command.Parameters.Add("@stale", SqlDbType.Bit).Value = stale;
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidDataException($"Rendition row missing for ImageId {id}, ShapeId {shape}");
    }

    private async Task<string> GetCheckpointAsync(SqlConnection connection, string partition, CancellationToken ct)
    {
        await using var command = new SqlCommand($"SELECT LastRowKey FROM {Table("ImageAdoptionCheckpoint")} WHERE PartitionName=@partition", connection);
        command.Parameters.Add("@partition", SqlDbType.VarChar, 64).Value = partition;
        return await command.ExecuteScalarAsync(ct) as string ?? "";
    }

    private async Task SaveCheckpointAsync(SqlConnection connection, string partition, string rowKey,
        int processed, CancellationToken ct)
    {
        await using var command = new SqlCommand($"""
            UPDATE {Table("ImageAdoptionCheckpoint")}
                SET LastRowKey=@key,Processed=Processed+@processed,UpdatedAt=SYSUTCDATETIME()
                WHERE PartitionName=@partition;
            IF @@ROWCOUNT=0
                INSERT INTO {Table("ImageAdoptionCheckpoint")}
                    (PartitionName,LastRowKey,Processed,Matched,Copied,Quarantined)
                VALUES (@partition,@key,@processed,0,0,0);
            """, connection);
        command.Parameters.Add("@partition", SqlDbType.VarChar, 64).Value = partition;
        command.Parameters.Add("@key", SqlDbType.VarChar, 256).Value = rowKey;
        command.Parameters.Add("@processed", SqlDbType.Int).Value = processed;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task IssueAsync(SqlConnection connection, string partition, string rowKey,
        string reason, string? title, CancellationToken ct)
    {
        await using var command = new SqlCommand($"""
            UPDATE {Table("ImageAdoptionIssue")} SET Reason=@reason,Title=@title,NotedAt=SYSUTCDATETIME()
            WHERE PartitionName=@partition AND LegacyRowKey=@key;
            IF @@ROWCOUNT=0 INSERT INTO {Table("ImageAdoptionIssue")}
                (PartitionName,LegacyRowKey,Reason,Title) VALUES (@partition,@key,@reason,@title);
            """, connection);
        command.Parameters.Add("@partition", SqlDbType.VarChar, 64).Value = partition;
        command.Parameters.Add("@key", SqlDbType.VarChar, 256).Value = rowKey;
        command.Parameters.Add("@reason", SqlDbType.VarChar, 64).Value = reason;
        command.Parameters.Add("@title", SqlDbType.VarChar, 1536).Value = (object?)title ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task ClearIssueAsync(SqlConnection connection, string partition, string rowKey, CancellationToken ct)
    {
        await using var command = new SqlCommand($"""
            DELETE FROM {Table("ImageAdoptionIssue")} WHERE PartitionName=@partition AND LegacyRowKey=@key
            """, connection);
        command.Parameters.Add("@partition", SqlDbType.VarChar, 64).Value = partition;
        command.Parameters.Add("@key", SqlDbType.VarChar, 256).Value = rowKey;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static byte? Shape(int width) => width switch
    {
        250 => 1, 20 => 2, 40 => 3, 60 => 4, 120 => 5, _ => null
    };

    public static byte License(string? license)
    {
        if (string.IsNullOrWhiteSpace(license)) return 6;
        var name = license.ToLowerInvariant();
        if (name.Contains("cc-by-sa", StringComparison.Ordinal) || name.Contains("cc by-sa", StringComparison.Ordinal)) return 2;
        if (name.Contains("cc-by", StringComparison.Ordinal) || name.Contains("cc by", StringComparison.Ordinal)) return 1;
        if (name.Contains("cc0", StringComparison.Ordinal)) return 3;
        if (name.Contains("public domain", StringComparison.Ordinal) || name == "pd") return 4;
        return 5;
    }
}
