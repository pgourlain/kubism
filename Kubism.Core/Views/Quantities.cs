using System.Globalization;

namespace Kubism.Core.Views;

/// <summary>Kubernetes resource.Quantity parsing and formatting.</summary>
public static class Quantities
{
    private static readonly (string suffix, double factor)[] Suffixes =
    [
        ("Ki", 1024), ("Mi", 1024d * 1024), ("Gi", 1024d * 1024 * 1024), ("Ti", Math.Pow(1024, 4)), ("Pi", Math.Pow(1024, 5)), ("Ei", Math.Pow(1024, 6)),
        ("n", 1e-9), ("u", 1e-6), ("m", 1e-3), ("k", 1e3), ("K", 1e3), ("M", 1e6), ("G", 1e9), ("T", 1e12), ("P", 1e15), ("E", 1e18),
    ];

    /// <summary>Parses any quantity to its base value (cores for cpu, bytes for memory).</summary>
    public static double Parse(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return 0;
        q = q.Trim();
        foreach (var (suffix, factor) in Suffixes)
            if (q.EndsWith(suffix, StringComparison.Ordinal) && double.TryParse(q[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v * factor;
        // Exponent form such as 1e3 or plain numbers.
        return double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    public static double ParseCpu(string? q) => Parse(q);
    public static double ParseBytes(string? q) => Parse(q);

    /// <summary>Formats cores as millicores ("250m") or whole cores ("2").</summary>
    public static string FormatCpu(double cores)
    {
        var milli = Math.Round(cores * 1000);
        return milli % 1000 == 0 ? (milli / 1000).ToString(CultureInfo.InvariantCulture) : $"{milli.ToString(CultureInfo.InvariantCulture)}m";
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "Ki", "Mi", "Gi", "Ti"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0}B" : $"{bytes:0.#}{units[i]}".Replace(",", ".");
    }

    /// <summary>Memory in Mi for resource blocks ("512Mi", or "2Gi" when a round number of Gi).</summary>
    public static string FormatMemoryMi(double bytes)
    {
        var mi = Math.Ceiling(bytes / (1024 * 1024));
        return mi >= 1024 && mi % 1024 == 0 ? $"{mi / 1024:0}Gi" : $"{mi:0}Mi";
    }
}
