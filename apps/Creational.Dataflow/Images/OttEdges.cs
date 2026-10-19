using DuckDB.NET.Data;

namespace Creational.Dataflow.Images;

public static class OttEdges
{
    public static async Task BuildAsync(string source, string destination, CancellationToken ct)
    {
        var csv = destination + "." + Guid.NewGuid().ToString("N") + ".tsv";
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var input = File.OpenRead(source))
            using (var reader = new StreamReader(input))
            await using (var output = File.Create(csv))
            await using (var writer = new StreamWriter(output))
            {
                await writer.WriteLineAsync("ott_id\tparent_ott_id");
                await reader.ReadLineAsync(ct); // OTT header
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    var first = line.IndexOf("\t|\t", StringComparison.Ordinal);
                    var second = first < 0 ? -1 : line.IndexOf("\t|\t", first + 3, StringComparison.Ordinal);
                    if (first < 0 || second < 0 ||
                        !long.TryParse(line.AsSpan(0, first), out var id) || id <= 0)
                        continue;
                    var parentText = line.AsSpan(first + 3, second - first - 3);
                    if (parentText.Length > 0 && !long.TryParse(parentText, out _)) continue;
                    await writer.WriteLineAsync($"{id}\t{parentText.ToString()}");
                }
            }
            using var db = new DuckDBConnection("Data Source=:memory:");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $$"""
                COPY (
                    SELECT ott_id,parent_ott_id FROM read_csv('{{Quote(csv)}}',
                        delim='\t',header=true,
                        columns={'ott_id':'BIGINT','parent_ott_id':'BIGINT'},nullstr='')
                ) TO '{{Quote(partial)}}' (FORMAT PARQUET, COMPRESSION ZSTD)
                """;
            command.ExecuteNonQuery();
            File.Move(partial, destination, true);
        }
        finally
        {
            if (File.Exists(csv)) File.Delete(csv);
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static string Quote(string path) => path.Replace("'", "''");
}
