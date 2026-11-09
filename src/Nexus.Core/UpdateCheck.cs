namespace Nexus.Core;

/// <summary>Decides whether a file is a newer NEXUS installer, from the version information it carries.</summary>
public static class UpdateCheck
{
    public static (Version? Version, string? Error) Evaluate(string? productName, string? productVersion, Version current)
    {
        // Installers pad these fields with spaces or NULs.
        if (!string.Equals(productName?.Trim(' ', '\0'), "NEXUS", StringComparison.Ordinal))
            return (null, "Ce fichier n'est pas un installateur de NEXUS.");
        if (!Version.TryParse(productVersion?.Trim(' ', '\0').Split('+')[0], out var version))
            return (null, "Version de l'installateur illisible.");
        var candidate = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        var installed = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
        return candidate > installed
            ? (candidate, null)
            : (null, $"Cet installateur contient la version {candidate.ToString(3)}, qui n'est pas plus récente que la version installée ({installed.ToString(3)}).");
    }
}
