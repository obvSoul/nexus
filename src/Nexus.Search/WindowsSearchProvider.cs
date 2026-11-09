using System.Data.OleDb;
using Nexus.Core.Indexing;
using Nexus.Core.Search;

namespace Nexus.Search;

/// <summary>
/// Reads the index maintained by Windows itself (PDF, Office and everything Windows knows how to read, in the folders
/// Windows indexes). If the service is off or disabled the result is simply empty.
/// </summary>
public sealed class WindowsSearchProvider(INexusLog? log = null) : IContentHitProvider
{
    private const int MaxHits = 5000;
    private readonly INexusLog _log = log ?? NullLog.Instance;

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<(string Directory, string Name)> FindContent(IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (!Enabled || terms.Count == 0) return [];
        // Words become quoted literals of the Windows Search language: quotes in the input cannot change the query.
        string words = string.Join(" AND ", terms.Select(t => "\"" + t.Replace("\"", " ").Replace("'", " ") + "\""));
        var hits = new List<(string, string)>();
        try
        {
            using var c = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT TOP {MaxHits} System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:' AND CONTAINS(*, '{words}')";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (r.IsDBNull(0)) continue;
                string path = r.GetString(0);
                int i = path.LastIndexOf('\\');
                if (i > 0) hits.Add((path[..i], path[(i + 1)..]));
            }
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.Warn("Windows Search unavailable: " + ex.Message);
        }
        return hits;
    }
}
