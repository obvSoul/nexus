using System.Globalization;

namespace Nexus.Core.Formatting;

/// <summary>Formats byte counts for display (binary steps, French unit labels).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To", "Po"];

    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        if (u == 0) return $"{bytes.ToString(CultureInfo.CurrentCulture)} o";
        string fmt = v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
        return $"{v.ToString(fmt, CultureInfo.CurrentCulture)} {Units[u]}";
    }
}
