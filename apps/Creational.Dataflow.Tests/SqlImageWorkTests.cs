using System.Data;
using Creational.Dataflow.Images;
using Creational.Dataflow.Sql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlImageWorkTests
{
    [Fact]
    public async Task ResizePartialResultKeepsSuccessfulShapesAndRetriesMissingOnes()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var factory = new SqlConnectionFactory(connectionString);
        var repository = new SqlImageWorkRepository(factory, "catalog");
        var title = "File:Sql resize test " + Guid.NewGuid().ToString("N") + ".jpg";
        int id;
        await using (var connection = await factory.OpenAsync(CancellationToken.None))
        {
            await using var insert = new SqlCommand("""
                INSERT INTO catalog.Image(SourceWiki,CanonicalFileTitle,Priority,MetadataStatus,ImageInfoState)
                    OUTPUT inserted.ImageId VALUES ('commonswiki',@title,2000,'commons',1);
                """, connection);
            insert.Parameters.Add("@title", SqlDbType.VarChar, 1536).Value = title;
            id = Convert.ToInt32(await insert.ExecuteScalarAsync());
            await using var renditions = new SqlCommand("""
                INSERT INTO catalog.Rendition(ImageId,ShapeId,Bytes,Complete)
                VALUES (@id,1,123,1),(@id,2,NULL,0),(@id,3,NULL,0),(@id,4,NULL,0),(@id,5,NULL,0);
                """, connection);
            renditions.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await renditions.ExecuteNonQueryAsync();
        }
        try
        {
            Assert.Equal(1, await repository.FillPendingQueueAsync(ImageWorkKind.Resize, 1, CancellationToken.None));
            Assert.Empty(await repository.ClaimAsync(ImageWorkKind.Resize, 1, TimeSpan.FromMinutes(5), CancellationToken.None));
            await SetSettingAsync(factory, 3, true, 1000);
            var first = Assert.Single(await repository.ClaimAsync(ImageWorkKind.Resize, 1,
                TimeSpan.FromMinutes(5), CancellationToken.None));
            Assert.True(await repository.CompleteResizeItemAsync(first,
                new Dictionary<byte, int> { [2] = 20, [3] = 40 }, "two shapes failed", CancellationToken.None));
            Assert.Equal(0, await repository.ActivateDueAsync(ImageWorkKind.Resize, CancellationToken.None));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var moveDue = new SqlCommand("""
                    UPDATE catalog.ImageWorkQueue SET DueAt=DATEADD(SECOND,-1,SYSUTCDATETIME())
                    WHERE ImageId=@id AND Kind=3;
                    """, connection);
                moveDue.Parameters.Add("@id", SqlDbType.Int).Value = id;
                await moveDue.ExecuteNonQueryAsync();
            }
            Assert.Equal(1, await repository.ActivateDueAsync(ImageWorkKind.Resize, CancellationToken.None));
            var retry = Assert.Single(await repository.ClaimAsync(ImageWorkKind.Resize, 1,
                TimeSpan.FromMinutes(5), CancellationToken.None));
            Assert.True(await repository.CompleteResizeItemAsync(retry,
                new Dictionary<byte, int> { [2] = 20, [3] = 40, [4] = 60, [5] = 120 }, null,
                CancellationToken.None));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var check = new SqlCommand("""
                    SELECT COUNT(*) FROM catalog.Rendition WHERE ImageId=@id AND Complete=1 AND Stale=0;
                    """, connection);
                check.Parameters.Add("@id", SqlDbType.Int).Value = id;
                Assert.Equal(5, Convert.ToInt32(await check.ExecuteScalarAsync()));
            }
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var stale = new SqlCommand("""
                    UPDATE catalog.Rendition SET Stale=1 WHERE ImageId=@id AND ShapeId=2;
                    """, connection);
                stale.Parameters.Add("@id", SqlDbType.Int).Value = id;
                await stale.ExecuteNonQueryAsync();
            }
            Assert.Equal(1, await repository.FillPendingQueueAsync(ImageWorkKind.Resize, 1, CancellationToken.None));
            var later = Assert.Single(await repository.ClaimAsync(ImageWorkKind.Resize, 1,
                TimeSpan.FromMinutes(5), CancellationToken.None));
            Assert.Equal(3, later.AttemptNumber);
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var advance = new SqlCommand("""
                    UPDATE catalog.Image SET SourceGeneration=SourceGeneration+1 WHERE ImageId=@id;
                    """, connection);
                advance.Parameters.Add("@id", SqlDbType.Int).Value = id;
                await advance.ExecuteNonQueryAsync();
            }
            Assert.False(await repository.CompleteResizeItemAsync(later,
                new Dictionary<byte, int> { [2] = 20, [3] = 40, [4] = 60, [5] = 120 }, null,
                CancellationToken.None));
        }
        finally
        {
            await SetSettingAsync(factory, 3, false, 0);
            await using var connection = await factory.OpenAsync(CancellationToken.None);
            await using var command = new SqlCommand("""
                DELETE FROM catalog.ImageWorkAttempt WHERE ImageId=@id;
                DELETE FROM catalog.ImageWorkQueue WHERE ImageId=@id;
                DELETE FROM catalog.Rendition WHERE ImageId=@id;
                DELETE FROM catalog.Image WHERE ImageId=@id;
                """, connection);
            command.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MetadataQueueClaimsExclusivelyAndHonorsDisabledMinimumAndRetry()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var factory = new SqlConnectionFactory(connectionString);
        var repository = new SqlImageWorkRepository(factory, "catalog");
        var title = "File:Sql work test " + Guid.NewGuid().ToString("N") + ".jpg";
        int id;
        await using (var connection = await factory.OpenAsync(CancellationToken.None))
        {
            await using var command = new SqlCommand("""
                INSERT INTO catalog.Image(SourceWiki,CanonicalFileTitle,Priority,MetadataStatus)
                    OUTPUT inserted.ImageId VALUES ('commonswiki',@title,2000,'commons');
                """, connection);
            command.Parameters.Add("@title", SqlDbType.VarChar, 1536).Value = title;
            id = Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        try
        {
            Assert.Empty(await repository.ClaimAsync(ImageWorkKind.Metadata, 1, TimeSpan.FromMinutes(5), CancellationToken.None));
            await SetSettingAsync(factory, 1, true, 3000);
            Assert.Equal(0, await repository.FillMetadataQueueAsync(1, CancellationToken.None));
            await SetSettingAsync(factory, 1, true, 1000);
            Assert.Equal(1, await repository.FillMetadataQueueAsync(1, CancellationToken.None));
            var groups = await Task.WhenAll(
                repository.ClaimAsync(ImageWorkKind.Metadata, 1, TimeSpan.FromMinutes(5), CancellationToken.None),
                repository.ClaimAsync(ImageWorkKind.Metadata, 1, TimeSpan.FromMinutes(5), CancellationToken.None));
            var claim = Assert.Single(groups.SelectMany(x => x));
            Assert.Equal(id, claim.ImageId);
            Assert.True(await repository.FailAsync(claim, "retry", TimeSpan.Zero, false, CancellationToken.None));
            Assert.False(await repository.FailAsync(claim, "late", TimeSpan.Zero, false, CancellationToken.None));
            Assert.Equal(1, await repository.ActivateDueAsync(ImageWorkKind.Metadata, CancellationToken.None));
            var retry = Assert.Single(await repository.ClaimAsync(ImageWorkKind.Metadata, 1,
                TimeSpan.FromMinutes(5), CancellationToken.None));
            Assert.Equal(2, retry.AttemptNumber);
            Assert.True(await repository.CompleteMetadataAsync(retry, 2, null, CancellationToken.None));
            Assert.Empty(await repository.ClaimAsync(ImageWorkKind.Metadata, 1, TimeSpan.FromMinutes(5), CancellationToken.None));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var stale = new SqlCommand("""
                    UPDATE catalog.Image SET MetadataStale=1 WHERE ImageId=@id;
                    INSERT INTO catalog.Rendition(ImageId,ShapeId,Complete) VALUES (@id,1,0);
                    INSERT INTO catalog.ImageWorkQueue(ImageId,Kind,Priority,State,LastError)
                        VALUES (@id,2,2000,3,'blocked copy');
                    """, connection);
                stale.Parameters.Add("@id", SqlDbType.Int).Value = id;
                await stale.ExecuteNonQueryAsync();
            }
            Assert.Equal(1, await repository.FillMetadataQueueAsync(1, CancellationToken.None));
            var refreshed = Assert.Single(await repository.ClaimAsync(ImageWorkKind.Metadata, 1,
                TimeSpan.FromMinutes(5), CancellationToken.None));
            Assert.Equal(3, refreshed.AttemptNumber);
            Assert.True(await repository.CompleteMetadataAsync(refreshed, 1, null, CancellationToken.None));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var check = new SqlCommand("""
                    SELECT State FROM catalog.ImageWorkQueue WHERE ImageId=@id AND Kind=2;
                    """, connection);
                check.Parameters.Add("@id", SqlDbType.Int).Value = id;
                Assert.Equal(0, Convert.ToInt32(await check.ExecuteScalarAsync()));
            }
        }
        finally
        {
            await SetSettingAsync(factory, 1, false, 0);
            await using var connection = await factory.OpenAsync(CancellationToken.None);
            await using var command = new SqlCommand("""
                DELETE FROM catalog.ImageWorkAttempt WHERE ImageId=@id;
                DELETE FROM catalog.ImageWorkQueue WHERE ImageId=@id;
                DELETE FROM catalog.Rendition WHERE ImageId=@id;
                DELETE FROM catalog.Image WHERE ImageId=@id;
                """, connection);
            command.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SetSettingAsync(SqlConnectionFactory factory, byte kind, bool enabled, float minimum)
    {
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = new SqlCommand("""
            UPDATE catalog.ImageWorkSetting SET Enabled=@enabled,MinimumPriority=@minimum,Concurrency=1 WHERE Kind=@kind;
            """, connection);
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = kind;
        command.Parameters.Add("@enabled", SqlDbType.Bit).Value = enabled;
        command.Parameters.Add("@minimum", SqlDbType.Real).Value = minimum;
        await command.ExecuteNonQueryAsync();
    }
}
