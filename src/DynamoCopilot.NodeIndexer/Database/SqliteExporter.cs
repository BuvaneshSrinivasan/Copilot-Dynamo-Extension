using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DynamoCopilot.NodeIndexer.Models;

namespace DynamoCopilot.NodeIndexer.Database;

// =============================================================================
// SqliteExporter — writes all-MiniLM-L6-v2 embeddings to a portable SQLite file
// =============================================================================
// The resulting nodes.db is bundled with the installer so end users never need
// to run Ollama or any external service for node suggestion search.
//
// Schema (single table):
//   Nodes(Id INTEGER PK, Name, Category, PackageName, Description,
//         InputPortsJson, OutputPortsJson, NodeType, EmbeddingJson)
//
// EmbeddingJson stores a JSON array of float32 values (384 elements for
// all-MiniLM-L6-v2).  The Extension reads this column and does cosine
// similarity entirely in managed C# code.
// =============================================================================

public sealed class SqliteExporter : IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteExporter(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Nodes (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                Name           TEXT    NOT NULL,
                Category       TEXT,
                PackageName    TEXT    NOT NULL,
                Description    TEXT,
                InputPortsJson TEXT,
                OutputPortsJson TEXT,
                NodeType       TEXT    NOT NULL,
                EmbeddingJson  TEXT,
                UNIQUE(PackageName, Name)
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_package ON Nodes(PackageName);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the set of (PackageName, Name) pairs already in the DB.</summary>
    public HashSet<(string, string)> LoadIndexedKeys()
    {
        var keys = new HashSet<(string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT PackageName, Name FROM Nodes";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add((reader.GetString(0), reader.GetString(1)));
        return keys;
    }

    /// <summary>Bulk-inserts a batch of (NodeRecord, embedding) pairs.</summary>
    public async Task UpsertAsync(
        IReadOnlyList<(NodeRecord Record, float[] Embedding)> batch,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO Nodes
                    (Name, Category, PackageName, Description,
                     InputPortsJson, OutputPortsJson, NodeType, EmbeddingJson)
                VALUES
                    ($name, $cat, $pkg, $desc, $inp, $out, $nt, $emb)
                """;

            var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
            var pCat  = cmd.CreateParameter(); pCat.ParameterName  = "$cat";  cmd.Parameters.Add(pCat);
            var pPkg  = cmd.CreateParameter(); pPkg.ParameterName  = "$pkg";  cmd.Parameters.Add(pPkg);
            var pDesc = cmd.CreateParameter(); pDesc.ParameterName = "$desc"; cmd.Parameters.Add(pDesc);
            var pInp  = cmd.CreateParameter(); pInp.ParameterName  = "$inp";  cmd.Parameters.Add(pInp);
            var pOut  = cmd.CreateParameter(); pOut.ParameterName  = "$out";  cmd.Parameters.Add(pOut);
            var pNt   = cmd.CreateParameter(); pNt.ParameterName   = "$nt";   cmd.Parameters.Add(pNt);
            var pEmb  = cmd.CreateParameter(); pEmb.ParameterName  = "$emb";  cmd.Parameters.Add(pEmb);

            foreach (var (r, emb) in batch)
            {
                ct.ThrowIfCancellationRequested();

                pName.Value = r.Name;
                pCat.Value  = r.Category  ?? (object)DBNull.Value;
                pPkg.Value  = r.PackageName;
                pDesc.Value = r.Description ?? (object)DBNull.Value;
                pInp.Value  = r.InputPorts  is { Length: > 0 }
                    ? JsonSerializer.Serialize(r.InputPorts)
                    : (object)DBNull.Value;
                pOut.Value  = r.OutputPorts is { Length: > 0 }
                    ? JsonSerializer.Serialize(r.OutputPorts)
                    : (object)DBNull.Value;
                pNt.Value   = r.NodeType;
                pEmb.Value  = JsonSerializer.Serialize(emb);

                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }, ct);
    }

    public void Dispose() => _conn.Dispose();
}
