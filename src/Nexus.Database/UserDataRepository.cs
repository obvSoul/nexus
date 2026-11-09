namespace Nexus.Database;

/// <summary>Search history and favorites.</summary>
public sealed class UserDataRepository(NexusDatabase db)
{
    public void AddHistory(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        using var scope = db.ExternalWrite();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO search_history(query,last_used,uses) VALUES($q,$t,1)
            ON CONFLICT(query) DO UPDATE SET last_used=$t, uses=uses+1
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Most recent queries starting with <paramref name="prefix"/> (all recent ones if empty).</summary>
    public List<string> GetHistory(string prefix, int limit)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT query FROM search_history WHERE query LIKE $p ESCAPE '^' ORDER BY last_used DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$p", EscapeLike(prefix.Trim()) + "%");
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void ClearHistory()
    {
        using var scope = db.ExternalWrite();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM search_history";
        cmd.ExecuteNonQuery();
    }

    public bool IsFavorite(string path)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM favorites WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Toggles the favorite flag; returns the new state.</summary>
    public bool ToggleFavorite(string path)
    {
        using var scope = db.ExternalWrite();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.Parameters.AddWithValue("$p", path);
        cmd.CommandText = "DELETE FROM favorites WHERE path=$p";
        if (cmd.ExecuteNonQuery() > 0) return false;
        cmd.CommandText = "INSERT INTO favorites(path,added) VALUES($p,$t)";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
        return true;
    }

    private static string EscapeLike(string s) => s.Replace("^", "^^").Replace("%", "^%").Replace("_", "^_");
}
