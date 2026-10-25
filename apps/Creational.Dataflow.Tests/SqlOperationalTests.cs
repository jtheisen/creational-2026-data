using Creational.Dataflow.Sql;
using Creational.Dataflow.Targets;
using Xunit;

namespace Creational.Dataflow.Tests;

public sealed class SqlOperationalTests
{
    [Fact]
    public async Task ConcurrentAttemptClaimHasOneWinnerAndPointReadMatchesCollectionRead()
    {
        var connectionString = Environment.GetEnvironmentVariable("CREATIONAL_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var factory = new SqlConnectionFactory(connectionString);
        var repository = new SqlTargetRepository(factory, "catalog");
        var id = new TargetId("test/" + Guid.NewGuid().ToString("N"), "parse", "shard/1");
        try
        {
            var stale = await repository.MarkStaleAsync(id, "test", CancellationToken.None);
            var wins = 0;
            await Task.WhenAll(Claim("a"), Claim("b"));
            Assert.Equal(1, wins);
            var point = await repository.GetTargetAsync(id, CancellationToken.None);
            Assert.NotNull(point);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(point), System.Text.Json.JsonSerializer.Serialize(
                (await repository.GetCollectionAsync(id.Collection, CancellationToken.None))[id]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.BeginAttemptAsync(id, "c",
                stale.StaleRevision + 1, CancellationToken.None));

            async Task Claim(string attempt)
            {
                try
                {
                    await repository.BeginAttemptAsync(id, attempt, stale.StaleRevision, CancellationToken.None);
                    Interlocked.Increment(ref wins);
                }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            await using var connection = await factory.OpenAsync(CancellationToken.None);
            await using var command = new Microsoft.Data.SqlClient.SqlCommand("""
                DELETE FROM catalog.CollectionTargetAttempt WHERE CollectionName=@collection;
                DELETE FROM catalog.CollectionTargetState WHERE CollectionName=@collection;
                """, connection);
            command.Parameters.Add("@collection", System.Data.SqlDbType.VarChar, 128).Value = id.Collection;
            await command.ExecuteNonQueryAsync();
        }
    }
}
