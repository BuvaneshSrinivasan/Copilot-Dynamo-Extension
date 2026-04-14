using Npgsql;
using NpgsqlTypes;
using Pgvector;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Database;

// =============================================================================
// NodeRepository — Reads and writes DynamoNodes rows via raw Npgsql
// =============================================================================
// Uses raw Npgsql (not EF Core) for maximum insert throughput during indexing.
//
// Key design choices:
//   - LoadIndexedKeys()    → load (package_name, name) pairs already in the DB
//                            so the orchestrator can skip already-embedded nodes
//   - UpsertAsync()        → ON CONFLICT DO NOTHING — safe to re-run anytime
//   - Uses NpgsqlDataSource (connection pool) rather than per-insert connections
// =============================================================================

public class NodeRepository : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NodeRepository(string connectionString)
    {
        // Register the pgvector type so Npgsql knows how to read/write Vector columns.
        // This must be done before the first connection is opened.
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
    }

    // Returns all (PackageName, NodeName) pairs already present in the DB.
    // The orchestrator uses this set to skip nodes that don't need re-indexing.
    public async Task<HashSet<(string PackageName, string Name)>> LoadIndexedKeysAsync(
        CancellationToken ct = default)
    {
        var keys = new HashSet<(string, string)>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """SELECT "PackageName", "Name" FROM "DynamoNodes" WHERE "Embedding" IS NOT NULL""", conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add((reader.GetString(0), reader.GetString(1)));

        return keys;
    }

    // Upserts a batch of (NodeRecord, embedding) pairs.
    // ON CONFLICT DO NOTHING means re-running the indexer is always safe.
    public async Task UpsertAsync(
        IReadOnlyList<(NodeRecord Record, float[] Embedding)> batch,
        CancellationToken ct = default)
    {
        if (batch.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Use a multi-row INSERT for efficiency — one round trip per batch.
        // The @pN placeholders are numbered per row to avoid conflicts.
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            INSERT INTO "DynamoNodes"
              ("Name", "PackageName", "Description", "Category", "PackageDescription",
               "Keywords", "InputPorts", "OutputPorts", "NodeType", "Embedding", "IndexedAt")
            VALUES
            """);

        var parameters = new List<NpgsqlParameter>();

        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"""
                (@n{i}, @pkg{i}, @desc{i}, @cat{i}, @pkgDesc{i},
                 @kw{i}, @inp{i}, @out{i}, @type{i}, @emb{i}, NOW())
                """);

            var (rec, emb) = batch[i];
            parameters.Add(new NpgsqlParameter($"n{i}",      Clip(rec.Name, 512)));
            parameters.Add(new NpgsqlParameter($"pkg{i}",    Clip(rec.PackageName, 256)));
            parameters.Add(new NpgsqlParameter($"desc{i}",   (object?)Clip(rec.Description, 4000)  ?? DBNull.Value));
            parameters.Add(new NpgsqlParameter($"cat{i}",    (object?)Clip(rec.Category, 512)      ?? DBNull.Value));
            parameters.Add(new NpgsqlParameter($"pkgDesc{i}",(object?)Clip(rec.PackageDescription, 4000) ?? DBNull.Value));
            parameters.Add(new NpgsqlParameter($"kw{i}",     rec.Keywords)  { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            parameters.Add(new NpgsqlParameter($"inp{i}",    rec.InputPorts){ NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            parameters.Add(new NpgsqlParameter($"out{i}",    rec.OutputPorts){ NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            parameters.Add(new NpgsqlParameter($"type{i}",   rec.NodeType));
            parameters.Add(new NpgsqlParameter($"emb{i}",    new Vector(emb)));
        }

        sb.Append("""
            ON CONFLICT ("PackageName", "Name") DO NOTHING
            """);

        await using var cmd = new NpgsqlCommand(sb.ToString(), conn);
        cmd.Parameters.AddRange(parameters.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Clip(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose() => _dataSource.Dispose();
}
