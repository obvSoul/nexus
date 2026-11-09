using System.Text.Json;
using Nexus.Core;

namespace Nexus.Database;

public sealed class SettingsStore(NexusDatabase db)
{
    private const string Key = "settings";

    public NexusSettings Load()
    {
        string? json = db.GetMeta(Key);
        if (string.IsNullOrEmpty(json)) return new NexusSettings();
        try { return JsonSerializer.Deserialize<NexusSettings>(json) ?? new NexusSettings(); }
        catch (JsonException) { return new NexusSettings(); } // damaged preferences must not stop the application
    }

    public void Save(NexusSettings settings) => db.SetMeta(Key, JsonSerializer.Serialize(settings));
}
