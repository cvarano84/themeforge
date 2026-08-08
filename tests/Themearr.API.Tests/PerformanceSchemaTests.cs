using Microsoft.Data.Sqlite;
using Themearr.API.Data;

namespace Themearr.API.Tests;

public sealed class PerformanceSchemaTests
{
    [Fact]
    public void Read_indexes_and_wal_mode_are_installed()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "schema.db");
        new Database(path).Init();

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var journal = conn.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode";
            Assert.Equal("wal", ((string)journal.ExecuteScalar()!).ToLowerInvariant());
        }

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        using (var command = conn.CreateCommand())
        {
            command.CommandText = "PRAGMA index_list(movies)";
            using var reader = command.ExecuteReader();
            while (reader.Read()) indexes.Add(reader.GetString(1));
        }
        Assert.Contains("ix_movies_group_key", indexes);
        Assert.Contains("ix_movies_status_title", indexes);
        Assert.Contains("ix_movies_instance_status", indexes);
        Assert.Contains("ix_movies_source_ref", indexes);

        using var plan = conn.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT id FROM movies WHERE status = 'pending' AND ignored = 0 ORDER BY title COLLATE NOCASE LIMIT 50";
        var details = new List<string>();
        using (var reader = plan.ExecuteReader())
            while (reader.Read()) details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains("ix_movies_status_title", StringComparison.Ordinal));
    }
}
