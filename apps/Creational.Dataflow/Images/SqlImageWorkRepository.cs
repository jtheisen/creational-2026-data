using System.Data;
using Creational.Dataflow.Sql;
using Microsoft.Data.SqlClient;

namespace Creational.Dataflow.Images;

public enum ImageWorkKind : byte { Metadata = 1, Copy = 2, Resize = 3 }

public sealed record ImageWorkClaim(int ImageId, ImageWorkKind Kind, float Priority, Guid LeaseId,
    int AttemptNumber, string SourceWiki, string FileTitle)
{
    public int SourceGeneration { get; init; }
}

public sealed record ImageResizeBatch(Guid BatchId, string ManifestPath, string ResultPath,
    string? ExecutionId, byte Status, DateTimeOffset CreatedAt);
public sealed record ImageWorkSetting(ImageWorkKind Kind, bool Enabled, float MinimumPriority,
    byte Concurrency, int PacingMilliseconds);

public sealed class SqlImageWorkRepository(SqlConnectionFactory factory, string schema)
{
    private readonly string _schema = SqlCatalogSchema.ValidateName(schema);
    private string Table(string name) => $"[{_schema}].[{name}]";

    public async Task<ImageWorkSetting> GetSettingAsync(ImageWorkKind kind, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SELECT Enabled,MinimumPriority,Concurrency,PacingMilliseconds
            FROM {Table("ImageWorkSetting")} WHERE Kind=@kind
            """, connection);
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)kind;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException($"Work setting {kind} missing");
        return new(kind, reader.GetBoolean(0), reader.GetFloat(1), reader.GetByte(2), reader.GetInt32(3));
    }

    public async Task<IReadOnlyList<(ImageWorkKind Kind, byte State, int Count)>> QueueCountsAsync(CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SELECT Kind,State,COUNT(*) FROM {Table("ImageWorkQueue")} GROUP BY Kind,State ORDER BY Kind,State
            """, connection);
        var rows = new List<(ImageWorkKind, byte, int)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(((ImageWorkKind)reader.GetByte(0), reader.GetByte(1),
            reader.GetInt32(2)));
        return rows;
    }

    public async Task SetSettingAsync(ImageWorkSetting setting, CancellationToken ct)
    {
        if (!float.IsFinite(setting.MinimumPriority) || setting.Concurrency is < 1 or > 16 ||
            setting.PacingMilliseconds is < 0 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(setting));
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            UPDATE {Table("ImageWorkSetting")}
            SET Enabled=@enabled,MinimumPriority=@minimum,Concurrency=@concurrency,
                PacingMilliseconds=@pacing WHERE Kind=@kind
            """, connection);
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)setting.Kind;
        command.Parameters.Add("@enabled", SqlDbType.Bit).Value = setting.Enabled;
        command.Parameters.Add("@minimum", SqlDbType.Real).Value = setting.MinimumPriority;
        command.Parameters.Add("@concurrency", SqlDbType.TinyInt).Value = setting.Concurrency;
        command.Parameters.Add("@pacing", SqlDbType.Int).Value = setting.PacingMilliseconds;
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidDataException($"Work setting {setting.Kind} missing");
    }

    public async Task<int> FillMetadataQueueAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            DECLARE @minimum REAL,@enabled BIT,@capacity INT,@outstanding INT;
            SELECT @minimum=MinimumPriority,@enabled=Enabled,
                @capacity=CASE WHEN Concurrency*4>20 THEN Concurrency*4 ELSE 20 END
            FROM {Table("ImageWorkSetting")} WHERE Kind=1;
            IF @enabled=0 BEGIN SELECT 0; RETURN; END;
            SELECT @outstanding=COUNT(*) FROM {Table("ImageWorkQueue")}
                WHERE Kind=1 AND State IN (0,1,2) AND Priority>=@minimum;
            SET @limit=CASE WHEN @outstanding>=@capacity THEN 0
                WHEN @limit>@capacity-@outstanding THEN @capacity-@outstanding ELSE @limit END;
            IF @limit=0 BEGIN SELECT 0; RETURN; END;
            ;WITH candidates AS (
                SELECT fresh.ImageId,fresh.Priority FROM (
                    SELECT TOP (@limit) i.ImageId,i.Priority
                    FROM {Table("Image")} i WITH (UPDLOCK,READPAST,READCOMMITTEDLOCK,INDEX(IX_Image_MetadataPriority))
                    WHERE i.ImageInfoState=0 AND i.MetadataStale=0 AND i.Priority>=@minimum
                      AND i.MetadataStatus IN ('commons','enwiki-local')
                      AND NOT EXISTS (SELECT 1 FROM {Table("ImageWorkQueue")} q
                                      WHERE q.ImageId=i.ImageId AND q.Kind=1)
                    ORDER BY i.Priority DESC,i.ImageId
                ) fresh
                UNION ALL
                SELECT stale.ImageId,stale.Priority FROM (
                    SELECT TOP (@limit) i.ImageId,i.Priority
                    FROM {Table("Image")} i WITH (UPDLOCK,READPAST,READCOMMITTEDLOCK,INDEX(IX_Image_MetadataStale))
                    WHERE i.MetadataStale=1 AND i.Priority>=@minimum
                      AND i.MetadataStatus IN ('commons','enwiki-local')
                      AND NOT EXISTS (SELECT 1 FROM {Table("ImageWorkQueue")} q
                                      WHERE q.ImageId=i.ImageId AND q.Kind=1)
                    ORDER BY i.Priority DESC,i.ImageId
                ) stale
            )
            INSERT INTO {Table("ImageWorkQueue")}(ImageId,Kind,Priority,AttemptNumber)
            SELECT TOP (@limit) c.ImageId,1,c.Priority,
                COALESCE((SELECT MAX(a.AttemptNumber) FROM {Table("ImageWorkAttempt")} a
                          WHERE a.ImageId=c.ImageId AND a.Kind=1),0)
            FROM candidates c ORDER BY c.Priority DESC,c.ImageId;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@limit", SqlDbType.Int).Value = Math.Min(limit, 100);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<int> FillPendingQueueAsync(ImageWorkKind kind, int limit, CancellationToken ct)
    {
        if (kind is not (ImageWorkKind.Copy or ImageWorkKind.Resize))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var candidates = kind == ImageWorkKind.Copy ? $"""
            FROM {Table("Rendition")} source WITH (INDEX(IX_Rendition_Work))
            JOIN {Table("Image")} i ON i.ImageId=source.ImageId
            WHERE source.ShapeId=1 AND i.ImageInfoState=1
              AND (source.Complete=0 OR source.Stale=1)
            """ : $"""
            FROM (
                SELECT ImageId FROM {Table("Rendition")} WITH (INDEX(IX_Rendition_Incomplete))
                    WHERE ShapeId IN (2,3,4,5) AND Complete=0
                UNION
                SELECT ImageId FROM {Table("Rendition")} WITH (INDEX(IX_Rendition_Stale))
                    WHERE ShapeId IN (2,3,4,5) AND Stale=1
            ) pending
            JOIN {Table("Image")} i ON i.ImageId=pending.ImageId
            JOIN {Table("Rendition")} source ON source.ImageId=i.ImageId AND source.ShapeId=1
            WHERE source.Complete=1 AND source.Stale=0
            """;
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            DECLARE @minimum REAL,@capacity INT,@outstanding INT;
            SELECT @minimum=MinimumPriority,
                @capacity=CASE WHEN Concurrency*4>20 THEN Concurrency*4 ELSE 20 END
            FROM {Table("ImageWorkSetting")} WHERE Kind=@kind;
            SELECT @outstanding=COUNT(*) FROM {Table("ImageWorkQueue")}
                WHERE Kind=@kind AND State IN (0,1,2) AND Priority>=@minimum;
            SET @limit=CASE WHEN @outstanding>=@capacity THEN 0
                WHEN @limit>@capacity-@outstanding THEN @capacity-@outstanding ELSE @limit END;
            IF @limit=0 BEGIN SELECT 0; RETURN; END;
            INSERT INTO {Table("ImageWorkQueue")}(ImageId,Kind,Priority,AttemptNumber)
            SELECT TOP (@limit) i.ImageId,@kind,i.Priority,
                COALESCE((SELECT MAX(a.AttemptNumber) FROM {Table("ImageWorkAttempt")} a
                          WHERE a.ImageId=i.ImageId AND a.Kind=@kind),0)
            {candidates} AND i.Priority>=@minimum
              AND NOT EXISTS (SELECT 1 FROM {Table("ImageWorkQueue")} q WITH (UPDLOCK,HOLDLOCK)
                              WHERE q.ImageId=i.ImageId AND q.Kind=@kind)
            ORDER BY i.Priority DESC,i.ImageId;
            SELECT @@ROWCOUNT;
            """, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)kind;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<bool> IsSourceStaleAsync(int imageId, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SELECT Stale FROM {Table("Rendition")} WHERE ImageId=@image AND ShapeId=1
            """, connection);
        command.Parameters.Add("@image", SqlDbType.Int).Value = imageId;
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> IsLeaseCurrentAsync(ImageWorkClaim claim, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {Table("ImageWorkQueue")}
                WHERE ImageId=@image AND Kind=@kind AND State=2 AND LeaseId=@lease
                  AND AttemptNumber=@attempt AND LeaseUntil>SYSUTCDATETIME()
            ) THEN 1 ELSE 0 END
            """, connection);
        AddClaim(command, claim);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task<int> SetSubtreePriorityAsync(int ottId, float priority, CancellationToken ct)
    {
        if (!float.IsFinite(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            DECLARE @start INT,@end INT;
            SELECT @start=DfsStart,@end=DfsEndExclusive FROM {Table("Taxon")}
                WHERE TreeId=1 AND OttId=@ott;
            IF @start IS NULL THROW 51009, 'OTT ID was not found in tree 1', 1;
            CREATE TABLE #Focus(ImageId INT NOT NULL PRIMARY KEY);
            INSERT INTO #Focus(ImageId)
            SELECT DISTINCT pi.ImageId
            FROM {Table("TaxonPage")} tp WITH (INDEX(IX_TaxonPage_DfsStart))
            JOIN {Table("PageImage")} pi ON pi.CollectionId=tp.CollectionId AND pi.PageId=tp.PageId
            WHERE tp.TreeId=1 AND tp.CollectionId=1 AND tp.DfsStart>=@start AND tp.DfsStart<@end
              AND pi.ImageId IS NOT NULL;
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE i SET Priority=@priority FROM {Table("Image")} i JOIN #Focus f ON f.ImageId=i.ImageId;
            UPDATE q SET Priority=@priority FROM {Table("ImageWorkQueue")} q
                JOIN #Focus f ON f.ImageId=q.ImageId;
            COMMIT TRANSACTION;
            SELECT COUNT(*) FROM #Focus;
            """, connection) { CommandTimeout = 1800 };
        command.Parameters.Add("@ott", SqlDbType.Int).Value = ottId;
        command.Parameters.Add("@priority", SqlDbType.Real).Value = priority;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task SetImagePriorityAsync(int imageId, float priority, CancellationToken ct)
    {
        if (!float.IsFinite(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE {Table("Image")} SET Priority=@priority WHERE ImageId=@image;
            IF @@ROWCOUNT<>1 THROW 51010, 'Image ID was not found', 1;
            UPDATE {Table("ImageWorkQueue")} SET Priority=@priority WHERE ImageId=@image;
            UPDATE {Table("ImageWorkQueue")} SET State=0,DueAt=SYSUTCDATETIME(),LastError=NULL
            WHERE ImageId=@image AND State=3;
            COMMIT TRANSACTION;
            """, connection);
        command.Parameters.Add("@image", SqlDbType.Int).Value = imageId;
        command.Parameters.Add("@priority", SqlDbType.Real).Value = priority;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkMetadataStaleAsync(int imageId, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE {Table("Image")} SET MetadataStale=1 WHERE ImageId=@image;
            IF @@ROWCOUNT<>1 THROW 51010, 'Image ID was not found', 1;
            UPDATE {Table("ImageWorkQueue")} SET State=0,DueAt=SYSUTCDATETIME(),LastError=NULL
            WHERE ImageId=@image AND Kind=1 AND State=3;
            COMMIT TRANSACTION;
            """, connection);
        command.Parameters.Add("@image", SqlDbType.Int).Value = imageId;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ImageWorkClaim>> ClaimAsync(ImageWorkKind kind, int limit,
        TimeSpan lease, CancellationToken ct)
    {
        if (kind is < ImageWorkKind.Metadata or > ImageWorkKind.Resize) throw new ArgumentOutOfRangeException(nameof(kind));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (lease < TimeSpan.FromMinutes(1) || lease > TimeSpan.FromHours(4)) throw new ArgumentOutOfRangeException(nameof(lease));
        var leaseId = Guid.NewGuid();
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @enabled BIT,@minimum REAL,@slots INT;
            SELECT @enabled=Enabled,@minimum=MinimumPriority,@slots=Concurrency
            FROM {Table("ImageWorkSetting")} WITH (UPDLOCK,HOLDLOCK) WHERE Kind=@kind;
            IF @enabled=1
            BEGIN
                SELECT @slots=@slots-COUNT(*) FROM {Table("ImageWorkQueue")}
                WHERE Kind=@kind AND State=2 AND LeaseUntil>SYSUTCDATETIME();
                SET @slots=CASE WHEN @slots<0 THEN 0 WHEN @slots>@limit THEN @limit ELSE @slots END;
                DECLARE @claimed TABLE(ImageId INT,Kind TINYINT,Priority REAL,AttemptNumber INT);
                ;WITH ready AS (
                    SELECT TOP (@slots) ImageId,Kind,Priority,State,LeaseId,LeaseUntil,AttemptNumber
                    FROM {Table("ImageWorkQueue")} WITH (UPDLOCK,READPAST,READCOMMITTEDLOCK,INDEX(IX_ImageWorkQueue_Claim))
                    WHERE Kind=@kind AND State=0 AND Priority>=@minimum
                    ORDER BY Priority DESC,ImageId
                )
                UPDATE ready SET State=2,LeaseId=@lease,LeaseUntil=DATEADD(SECOND,@seconds,SYSUTCDATETIME()),
                    AttemptNumber=AttemptNumber+1
                OUTPUT inserted.ImageId,inserted.Kind,inserted.Priority,inserted.AttemptNumber INTO @claimed;
                INSERT INTO {Table("ImageWorkAttempt")}(ImageId,Kind,AttemptNumber,LeaseId,SourceGeneration)
                SELECT c.ImageId,c.Kind,c.AttemptNumber,@lease,i.SourceGeneration
                FROM @claimed c JOIN {Table("Image")} i ON i.ImageId=c.ImageId;
                SELECT c.ImageId,c.Kind,c.Priority,c.AttemptNumber,i.SourceWiki,i.CanonicalFileTitle,
                    i.SourceGeneration
                FROM @claimed c JOIN {Table("Image")} i ON i.ImageId=c.ImageId
                ORDER BY c.Priority DESC,c.ImageId;
            END;
            COMMIT TRANSACTION;
            """, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)kind;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = leaseId;
        command.Parameters.Add("@seconds", SqlDbType.Int).Value = (int)lease.TotalSeconds;
        var result = new List<ImageWorkClaim>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        do
        {
            if (reader.FieldCount != 7) continue;
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetInt32(0), (ImageWorkKind)reader.GetByte(1), reader.GetFloat(2),
                    leaseId, reader.GetInt32(3), reader.GetString(4), reader.GetString(5))
                { SourceGeneration = reader.GetInt32(6) });
        } while (await reader.NextResultAsync(ct));
        return result;
    }

    public async Task<int> ActivateDueAsync(ImageWorkKind kind, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            UPDATE TOP (100) {Table("ImageWorkQueue")} SET State=0,DueAt=SYSUTCDATETIME()
            WHERE Kind=@kind AND State=1 AND DueAt<=SYSUTCDATETIME();
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)kind;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<int> RecoverExpiredAsync(ImageWorkKind kind, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @expired TABLE(ImageId INT,AttemptNumber INT,LeaseId UNIQUEIDENTIFIER);
            UPDATE TOP (100) {Table("ImageWorkQueue")}
            SET State=1,DueAt=DATEADD(SECOND,30,SYSUTCDATETIME()),LeaseId=NULL,LeaseUntil=NULL,
                LastError='Lease expired'
            OUTPUT inserted.ImageId,inserted.AttemptNumber,deleted.LeaseId INTO @expired
            WHERE Kind=@kind AND State=2 AND LeaseUntil<SYSUTCDATETIME();
            UPDATE a SET FinishedAt=SYSUTCDATETIME(),Outcome=2,Error='Lease expired'
            FROM {Table("ImageWorkAttempt")} a JOIN @expired e
              ON a.ImageId=e.ImageId AND a.AttemptNumber=e.AttemptNumber AND a.LeaseId=e.LeaseId
            WHERE a.Kind=@kind AND a.FinishedAt IS NULL;
            SELECT COUNT(*) FROM @expired;
            COMMIT TRANSACTION;
            """, connection);
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)kind;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<int> RecoverOrphanedResizeLeasesAsync(CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @expired TABLE(ImageId INT,AttemptNumber INT,LeaseId UNIQUEIDENTIFIER);
            ;WITH orphan AS (
                SELECT TOP (100) q.ImageId,q.Kind,q.State,q.LeaseId,q.LeaseUntil,q.DueAt,
                    q.LastError,q.AttemptNumber
                FROM {Table("ImageWorkQueue")} q WITH (UPDLOCK,READPAST)
                WHERE q.Kind=3 AND q.State=2 AND q.LeaseUntil<SYSUTCDATETIME()
                  AND NOT EXISTS (SELECT 1 FROM {Table("ImageResizeBatch")} b
                                  WHERE b.BatchId=q.LeaseId)
                ORDER BY q.LeaseUntil,q.ImageId
            )
            UPDATE orphan SET State=1,DueAt=DATEADD(SECOND,30,SYSUTCDATETIME()),
                LeaseId=NULL,LeaseUntil=NULL,LastError='Resize lease had no batch record'
            OUTPUT inserted.ImageId,inserted.AttemptNumber,deleted.LeaseId INTO @expired;
            UPDATE a SET FinishedAt=SYSUTCDATETIME(),Outcome=2,Error='Resize lease had no batch record'
            FROM {Table("ImageWorkAttempt")} a JOIN @expired e
              ON a.ImageId=e.ImageId AND a.AttemptNumber=e.AttemptNumber AND a.LeaseId=e.LeaseId
            WHERE a.Kind=3 AND a.FinishedAt IS NULL;
            SELECT COUNT(*) FROM @expired;
            COMMIT TRANSACTION;
            """, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<bool> FailAsync(ImageWorkClaim claim, string error, TimeSpan retryAfter, bool blocked,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE {Table("ImageWorkQueue")}
            SET State=@state,DueAt=DATEADD(SECOND,@seconds,SYSUTCDATETIME()),LeaseId=NULL,
                LeaseUntil=NULL,LastError=@error
            WHERE ImageId=@image AND Kind=@kind AND State=2 AND LeaseId=@lease AND AttemptNumber=@attempt;
            DECLARE @changed INT=@@ROWCOUNT;
            IF @changed=1 UPDATE {Table("ImageWorkAttempt")}
                SET FinishedAt=SYSUTCDATETIME(),Outcome=2,Error=@error
                WHERE ImageId=@image AND Kind=@kind AND AttemptNumber=@attempt AND LeaseId=@lease;
            SELECT @changed;
            COMMIT TRANSACTION;
            """, connection);
        AddClaim(command, claim);
        command.Parameters.Add("@state", SqlDbType.TinyInt).Value = blocked ? 3 : 1;
        command.Parameters.Add("@seconds", SqlDbType.Int).Value = (int)Math.Clamp(retryAfter.TotalSeconds, 0, 86_400);
        command.Parameters.Add("@error", SqlDbType.VarChar, 1024).Value = error[..Math.Min(error.Length, 1024)];
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task<bool> CompleteMetadataAsync(ImageWorkClaim claim, byte imageInfoState,
        byte? licenseCategory, CancellationToken ct)
    {
        if (claim.Kind != ImageWorkKind.Metadata) throw new ArgumentException("Expected metadata claim", nameof(claim));
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM {Table("ImageWorkQueue")}
            WHERE ImageId=@image AND Kind=1 AND State=2 AND LeaseId=@lease AND AttemptNumber=@attempt;
            DECLARE @changed INT=@@ROWCOUNT;
            IF @changed=1
            BEGIN
                UPDATE {Table("Image")} SET ImageInfoState=@status,MetadataStale=0,
                    LicenseCategoryId=@license WHERE ImageId=@image;
                UPDATE {Table("ImageWorkAttempt")} SET FinishedAt=SYSUTCDATETIME(),Outcome=1
                    WHERE ImageId=@image AND Kind=1 AND AttemptNumber=@attempt AND LeaseId=@lease;
                IF @status=1
                    UPDATE q SET State=0,DueAt=SYSUTCDATETIME(),LastError=NULL
                    FROM {Table("ImageWorkQueue")} q
                    JOIN {Table("Rendition")} r ON r.ImageId=q.ImageId AND r.ShapeId=1
                    WHERE q.ImageId=@image AND q.Kind=2 AND q.State=3
                      AND (r.Complete=0 OR r.Stale=1);
                IF @status=1 AND NOT EXISTS (SELECT 1 FROM {Table("ImageWorkQueue")}
                                             WHERE ImageId=@image AND Kind=2)
                    INSERT INTO {Table("ImageWorkQueue")}(ImageId,Kind,Priority,AttemptNumber)
                    SELECT i.ImageId,2,i.Priority,
                        COALESCE((SELECT MAX(a.AttemptNumber) FROM {Table("ImageWorkAttempt")} a
                                  WHERE a.ImageId=i.ImageId AND a.Kind=2),0)
                    FROM {Table("Image")} i
                    JOIN {Table("Rendition")} r ON r.ImageId=i.ImageId AND r.ShapeId=1
                    WHERE i.ImageId=@image AND (r.Complete=0 OR r.Stale=1);
            END;
            SELECT @changed;
            COMMIT TRANSACTION;
            """, connection);
        AddClaim(command, claim);
        command.Parameters.Add("@status", SqlDbType.TinyInt).Value = imageInfoState;
        command.Parameters.Add("@license", SqlDbType.TinyInt).Value = (object?)licenseCategory ?? DBNull.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task<bool> CompleteRenditionAsync(ImageWorkClaim claim, byte shapeId, int bytes,
        CancellationToken ct)
    {
        if (claim.Kind != ImageWorkKind.Copy || shapeId != 1)
            throw new ArgumentException("Only source-copy claims can complete the source rendition.", nameof(claim));
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM {Table("ImageWorkQueue")}
            WHERE ImageId=@image AND Kind=@kind AND State=2 AND LeaseId=@lease AND AttemptNumber=@attempt;
            DECLARE @changed INT=@@ROWCOUNT;
            IF @changed=1
            BEGIN
                UPDATE {Table("Rendition")} SET Bytes=@bytes,Complete=1,Stale=0,Failed=0
                    WHERE ImageId=@image AND ShapeId=@shape;
                UPDATE {Table("Image")} SET SourceGeneration=SourceGeneration+1
                    WHERE ImageId=@image;
                UPDATE {Table("Rendition")} SET Stale=1
                    WHERE ImageId=@image AND ShapeId BETWEEN 2 AND 5 AND Complete=1;
                UPDATE {Table("ImageWorkAttempt")} SET FinishedAt=SYSUTCDATETIME(),Outcome=1
                    WHERE ImageId=@image AND Kind=@kind AND AttemptNumber=@attempt AND LeaseId=@lease;
                IF @kind=2 AND NOT EXISTS (SELECT 1 FROM {Table("ImageWorkQueue")}
                                          WHERE ImageId=@image AND Kind=3)
                    INSERT INTO {Table("ImageWorkQueue")}(ImageId,Kind,Priority,AttemptNumber)
                    SELECT @image,3,i.Priority,
                        COALESCE((SELECT MAX(a.AttemptNumber) FROM {Table("ImageWorkAttempt")} a
                                  WHERE a.ImageId=@image AND a.Kind=3),0)
                    FROM {Table("Image")} i WHERE i.ImageId=@image;
            END;
            SELECT @changed;
            COMMIT TRANSACTION;
            """, connection);
        AddClaim(command, claim);
        command.Parameters.Add("@shape", SqlDbType.TinyInt).Value = shapeId;
        command.Parameters.Add("@bytes", SqlDbType.Int).Value = bytes;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task CreateResizeBatchAsync(Guid batchId, string manifestPath, string resultPath,
        CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            INSERT INTO {Table("ImageResizeBatch")}(BatchId,ManifestPath,ResultPath,Status)
            VALUES (@id,@manifest,@result,0)
            """, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = batchId;
        command.Parameters.Add("@manifest", SqlDbType.VarChar, 256).Value = manifestPath;
        command.Parameters.Add("@result", SqlDbType.VarChar, 256).Value = resultPath;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetResizeBatchStatusAsync(Guid batchId, byte status, string? executionId,
        string? error, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            UPDATE {Table("ImageResizeBatch")} SET Status=@status,ExecutionId=COALESCE(@execution,ExecutionId),
                Error=@error,UpdatedAt=SYSUTCDATETIME() WHERE BatchId=@id
            """, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = batchId;
        command.Parameters.Add("@status", SqlDbType.TinyInt).Value = status;
        command.Parameters.Add("@execution", SqlDbType.VarChar, 128).Value = (object?)executionId ?? DBNull.Value;
        command.Parameters.Add("@error", SqlDbType.VarChar, 1024).Value = (object?)error ?? DBNull.Value;
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidDataException($"Resize batch {batchId} disappeared");
    }

    public async Task<IReadOnlyList<ImageResizeBatch>> ListOpenResizeBatchesAsync(CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SELECT BatchId,ManifestPath,ResultPath,ExecutionId,Status,CreatedAt
            FROM {Table("ImageResizeBatch")} WHERE Status<3 ORDER BY CreatedAt
            """, connection);
        var batches = new List<ImageResizeBatch>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) batches.Add(new(reader.GetGuid(0), reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetByte(4),
            new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero)));
        return batches;
    }

    public async Task<IReadOnlyList<ImageWorkClaim>> GetResizeBatchClaimsAsync(Guid batchId, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = new SqlCommand($"""
            SELECT q.ImageId,q.Priority,q.AttemptNumber,i.SourceWiki,i.CanonicalFileTitle,
                COALESCE(a.SourceGeneration,0)
            FROM {Table("ImageWorkQueue")} q JOIN {Table("Image")} i ON i.ImageId=q.ImageId
            JOIN {Table("ImageWorkAttempt")} a ON a.ImageId=q.ImageId AND a.Kind=3
                AND a.AttemptNumber=q.AttemptNumber AND a.LeaseId=q.LeaseId
            WHERE q.Kind=3 AND q.State=2 AND q.LeaseId=@batch ORDER BY q.ImageId
            """, connection);
        command.Parameters.Add("@batch", SqlDbType.UniqueIdentifier).Value = batchId;
        var claims = new List<ImageWorkClaim>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) claims.Add(new(reader.GetInt32(0), ImageWorkKind.Resize,
            reader.GetFloat(1), batchId, reader.GetInt32(2), reader.GetString(3), reader.GetString(4))
        { SourceGeneration = reader.GetInt32(5) });
        return claims;
    }

    public async Task<bool> CompleteResizeItemAsync(ImageWorkClaim claim,
        IReadOnlyDictionary<byte, int> verifiedShapes, string? error, CancellationToken ct)
    {
        if (claim.Kind != ImageWorkKind.Resize) throw new ArgumentException("Expected resize claim", nameof(claim));
        await using var connection = await factory.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var guard = new SqlCommand($"""
            SELECT COUNT(*) FROM {Table("ImageWorkQueue")} q WITH (UPDLOCK,HOLDLOCK)
            JOIN {Table("Image")} i WITH (UPDLOCK,HOLDLOCK) ON i.ImageId=q.ImageId
            JOIN {Table("Rendition")} source ON source.ImageId=q.ImageId AND source.ShapeId=1
            WHERE q.ImageId=@image AND q.Kind=3 AND q.State=2 AND q.LeaseId=@lease
              AND q.AttemptNumber=@attempt AND i.SourceGeneration=@generation
              AND source.Complete=1 AND source.Stale=0
            """, connection, transaction))
        {
            AddClaim(guard, claim);
            guard.Parameters.Add("@generation", SqlDbType.Int).Value = claim.SourceGeneration;
            if (Convert.ToInt32(await guard.ExecuteScalarAsync(ct)) != 1)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
        }
        foreach (var (shape, bytes) in verifiedShapes)
        {
            if (shape is < 2 or > 5 || bytes < 0) throw new ArgumentOutOfRangeException(nameof(verifiedShapes));
            await using var update = new SqlCommand($"""
                UPDATE {Table("Rendition")} SET Bytes=@bytes,Complete=1,Stale=0,Failed=0
                WHERE ImageId=@image AND ShapeId=@shape
                """, connection, transaction);
            update.Parameters.Add("@image", SqlDbType.Int).Value = claim.ImageId;
            update.Parameters.Add("@shape", SqlDbType.TinyInt).Value = shape;
            update.Parameters.Add("@bytes", SqlDbType.Int).Value = bytes;
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidDataException("Derived rendition row missing");
        }
        var complete = verifiedShapes.Count == 4 && error is null;
        await using var finish = new SqlCommand(complete ? $"""
            DELETE FROM {Table("ImageWorkQueue")} WHERE ImageId=@image AND Kind=3 AND LeaseId=@lease;
            """ : $"""
            UPDATE {Table("ImageWorkQueue")} SET State=1,DueAt=DATEADD(MINUTE,5,SYSUTCDATETIME()),
                LeaseId=NULL,LeaseUntil=NULL,LastError=@error
            WHERE ImageId=@image AND Kind=3 AND LeaseId=@lease;
            """, connection, transaction);
        AddClaim(finish, claim);
        if (!complete) finish.Parameters.Add("@error", SqlDbType.VarChar, 1024).Value =
            (error ?? "One or more derived shapes failed")[..Math.Min((error ?? "One or more derived shapes failed").Length, 1024)];
        await finish.ExecuteNonQueryAsync(ct);
        await using var attempt = new SqlCommand($"""
            UPDATE {Table("ImageWorkAttempt")} SET FinishedAt=SYSUTCDATETIME(),Outcome=@outcome,Error=@error
            WHERE ImageId=@image AND Kind=3 AND AttemptNumber=@attempt AND LeaseId=@lease
            """, connection, transaction);
        AddClaim(attempt, claim);
        attempt.Parameters.Add("@outcome", SqlDbType.TinyInt).Value = complete ? 1 : 2;
        attempt.Parameters.Add("@error", SqlDbType.VarChar, 1024).Value = complete ? DBNull.Value :
            (object?)(error ?? "One or more derived shapes failed");
        await attempt.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static void AddClaim(SqlCommand command, ImageWorkClaim claim)
    {
        command.Parameters.Add("@image", SqlDbType.Int).Value = claim.ImageId;
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)claim.Kind;
        command.Parameters.Add("@lease", SqlDbType.UniqueIdentifier).Value = claim.LeaseId;
        command.Parameters.Add("@attempt", SqlDbType.Int).Value = claim.AttemptNumber;
    }
}
