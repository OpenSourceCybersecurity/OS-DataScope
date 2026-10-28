using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DataScope.Core.Inventory;

/// <summary>
/// Validates file server names supplied by administrators. Only plain DNS or
/// NetBIOS host names are accepted: no UNC prefixes, paths, ports, IP literals
/// or wildcards, so a value can never redirect the scanner somewhere unexpected.
/// </summary>
public static partial class HostName
{
    public const int MaxLength = 253;

    [GeneratedRegex(@"^(?!-)[A-Za-z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Label();

    [GeneratedRegex(@"^\d+(\.\d+){3}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Ipv4();

    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? normalized, out string? error)
    {
        normalized = null;
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Enter the server's name, for example FS01 or fs01.corp.local.";
            return false;
        }
        if (value.Length > MaxLength)
        {
            error = $"Server names can be at most {MaxLength} characters.";
            return false;
        }
        if (Ipv4().IsMatch(value) || value.Contains(':', StringComparison.Ordinal))
        {
            error = "Use the server's DNS name, not an IP address, so Kerberos authentication works.";
            return false;
        }
        var labels = value.Split('.');
        if (labels.Any(l => !Label().IsMatch(l)))
        {
            error = "Server names may contain only letters, digits, hyphens and dots (no \\, /, spaces or ports).";
            return false;
        }
        if (labels.Length == 1 && labels[0].Length > 15)
        {
            error = "Single-label (NetBIOS) names are limited to 15 characters. Use the full DNS name instead.";
            return false;
        }
        normalized = value.ToLowerInvariant();
        error = null;
        return true;
    }
}
