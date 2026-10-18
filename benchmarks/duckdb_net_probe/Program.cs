using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using DuckDB.NET.Data;

using var connection = new DuckDBConnection("Data Source=:memory:");
connection.Open();

void Run(string label, string sql)
{
    var timer = Stopwatch.StartNew();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    var result = command.ExecuteScalar();
    timer.Stop();
    Console.WriteLine($"{label}: {result ?? "ok"}; {timer.Elapsed.TotalSeconds:F3}s; " +
                      $"working_set={Process.GetCurrentProcess().WorkingSet64 / 1048576} MiB");
}

Run("duckdb_version", "SELECT version()");

if (Environment.GetEnvironmentVariable("PROBE_SKIP_AZURE") != "1")
{
    Run("load_azure", "INSTALL azure; LOAD azure; SELECT 'loaded'");
    Run("azure_transport", "SET azure_transport_option_type='curl'; SELECT 'curl'");
    if (Environment.GetEnvironmentVariable("PROBE_AUTH_MODE") == "access_token")
    {
        var timer = Stopwatch.StartNew();
        var credential = new ManagedIdentityCredential();
        var token = credential.GetToken(new TokenRequestContext(["https://storage.azure.com/.default"]));
        timer.Stop();
        Console.WriteLine($"get_managed_identity_token: ok; {timer.Elapsed.TotalSeconds:F3}s; " +
                          $"working_set={Process.GetCurrentProcess().WorkingSet64 / 1048576} MiB");
        Run("create_access_token_secret", $"""
            CREATE SECRET azure_probe (
                TYPE azure,
                PROVIDER access_token,
                ACCESS_TOKEN '{token.Token.Replace("'", "''")}',
                ACCOUNT_NAME 'creational'
            );
            SELECT 'created'
            """);
    }
    else
    {
        Run("create_managed_identity_secret", """
            CREATE SECRET azure_probe (
                TYPE azure,
                PROVIDER managed_identity,
                ACCOUNT_NAME 'creational'
            );
            SELECT 'created'
            """);
    }
    Run("azure_parquet_count", """
        SELECT count(*) FROM read_parquet(
          'az://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/enwiki-2026-10-01-p10p1113635/pool.parquet')
        """);
    Run("azure_parquet_filter", """
        SELECT count(*) FROM read_parquet(
          'az://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/enwiki-2026-10-01-p10p1113635/pool.parquet')
        WHERE title LIKE 'H%'
        """);
    Run("duckdb_threads", "SET threads=1; SELECT 1");
    Run("azure_parquet_glob", """
        SELECT count(*) FROM read_parquet(
          'az://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/*/pool.parquet')
        """);
    Run("azure_parquet_glob_filter", """
        SELECT count(*) FROM read_parquet(
          'az://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/*/pool.parquet')
        WHERE title LIKE 'Gadus%'
        """);
}

var dataRoot = Environment.GetEnvironmentVariable("PROBE_DATA_DIR");
if (dataRoot is not null)
{
    var wiki = Path.Combine(dataRoot, "wiki_names.parquet").Replace("'", "''");
    var ott = Path.Combine(dataRoot, "taxonomy_names.parquet").Replace("'", "''");
    Run("local_full_name_join", $"""
        SELECT count(*) FROM read_parquet('{wiki}') w
        JOIN read_parquet('{ott}') o USING (name_key)
        """);
    Run("selected_pages_join", $"""
        SELECT count(DISTINCT w.page_id) FROM read_parquet('{wiki}') w
        JOIN read_parquet('{ott}') o USING (name_key)
        WHERE w.page_id IN (680, 4925, 682482)
        """);
    Run("gadus_branch_join", $"""
        WITH RECURSIVE descendants(ott_id) AS (
            SELECT ott_id FROM read_parquet('{ott}')
            WHERE ott_id = 114168
            UNION ALL
            SELECT o.ott_id FROM read_parquet('{ott}') o
            JOIN descendants d ON o.parent_ott_id = d.ott_id
        )
        SELECT count(DISTINCT w.page_id)
        FROM descendants d
        JOIN read_parquet('{ott}') o USING (ott_id)
        JOIN read_parquet('{wiki}') w USING (name_key)
        """);
}
