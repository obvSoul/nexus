using System.Buffers;
using System.Runtime.InteropServices;

namespace Nexus.Infrastructure;

/// <summary>
/// Windows may report a change under the 8.3 spelling of a path (C:\Users\NAMEWI~1\...) when the program making it used
/// that spelling, as anything going through %TEMP% often does. The index only knows long names.
/// </summary>
public static class ShortPaths
{
    private const int BufferSize = 32768;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, char[] longPath, uint size);

    /// <summary>The path up to its first segment shaped like a generated 8.3 name (PROGRA~1, FILE~2.TXT), or null.</summary>
    public static string? FirstShortPrefix(string path)
    {
        if (!path.Contains('~')) return null;
        int start = 0;
        while (start < path.Length)
        {
            int end = path.IndexOf('\\', start);
            if (end < 0) end = path.Length;
            if (LooksShort(path[start..end])) return path[..end];
            start = end + 1;
        }
        return null;
    }

    private static bool LooksShort(string s)
    {
        int tilde = s.IndexOf('~');
        if (tilde is < 1 or > 6) return false;
        int dot = s.IndexOf('.');
        string stem = dot < 0 ? s : s[..dot];
        if (dot >= 0 && (s.Length - dot - 1 > 3 || dot < tilde)) return false;
        return stem.Length <= 8 && tilde + 1 < stem.Length && stem[(tilde + 1)..].All(char.IsAsciiLetterOrDigit);
    }

    /// <summary>
    /// The long form of a path. Works for something just deleted: the part that still exists is expanded and the rest kept as is.
    /// </summary>
    public static string ToLong(string path)
    {
        if (!path.Contains('~')) return path;
        var buffer = ArrayPool<char>.Shared.Rent(BufferSize);
        try
        {
            string existing = path, tail = "";
            while (true)
            {
                const string Prefix = @"\\?\";
                uint n = GetLongPathNameW(Prefix + existing, buffer, BufferSize);
                if (n > Prefix.Length && n < BufferSize)
                    return new string(buffer, Prefix.Length, (int)n - Prefix.Length) + tail;
                int i = existing.LastIndexOf('\\');
                if (i <= 2) return path;
                tail = existing[i..] + tail;
                existing = existing[..i];
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }
}
