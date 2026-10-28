using System.Net.Mail;

namespace DataScope.Core.Privacy;

/// <summary>
/// Who a person search (D160) looks for: a directory account, names and email addresses, as an administrator typed
/// them. Checked here, before anything is searched, so a search is always narrow enough to be about one person.
/// </summary>
public sealed record PersonTerms(string? Sid, IReadOnlyList<string> Names, IReadOnlyList<string> Addresses)
{
    public bool IsEmpty => Sid is null && Names.Count == 0 && Addresses.Count == 0;
}

public static class PersonSearch
{
    public const int MaxNames = 5;
    public const int MinNameLength = 3;
    public const int MaxNameLength = 100;
    public const int MaxAddresses = 5;
    public const int MaxAddressLength = 254;
    public const int MinReasonLength = 3;
    public const int MaxReasonLength = 500;

    /// <summary>
    /// Names as typed: trimmed, inner spaces collapsed, duplicates ignoring case dropped. Null with a problem when one
    /// is too short to be about one person, too long, or not plain text.
    /// </summary>
    public static IReadOnlyList<string>? Names(IEnumerable<string?>? names, out string? problem)
    {
        problem = null;
        var result = new List<string>();
        foreach (var raw in names ?? [])
        {
            var name = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0)
            {
                continue;
            }
            if (name.Length < MinNameLength || name.Length > MaxNameLength || name.Any(char.IsControl) || !name.Any(char.IsLetter))
            {
                problem = $"Each name needs {MinNameLength} to {MaxNameLength} characters of plain text, with letters.";
                return null;
            }
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }
        if (result.Count > MaxNames)
        {
            problem = $"Search for at most {MaxNames} names at once.";
            return null;
        }
        return result;
    }

    /// <summary>Email addresses, lower-cased, duplicates dropped. Null with a problem when one isn't a plain address.</summary>
    public static IReadOnlyList<string>? Addresses(IEnumerable<string?>? addresses, out string? problem)
    {
        problem = null;
        var result = new List<string>();
        foreach (var raw in addresses ?? [])
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0)
            {
                continue;
            }
            if (!IsAddress(text))
            {
                problem = "Use plain email addresses, such as jo@contoso.com.";
                return null;
            }
            var address = text.ToLowerInvariant();
            if (!result.Contains(address, StringComparer.Ordinal))
            {
                result.Add(address);
            }
        }
        if (result.Count > MaxAddresses)
        {
            problem = $"Search for at most {MaxAddresses} email addresses at once.";
            return null;
        }
        return result;
    }

    /// <summary>A plain address: one @, no display name, spaces, quotes or control characters.</summary>
    public static bool IsAddress(string text) =>
        text.Length <= MaxAddressLength
        && text.Count(c => c == '@') == 1
        && !text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '<' or '>' or '(' or ')' or ',' or ';' or '\\' or '%')
        && MailAddress.TryCreate(text, out var parsed)
        && string.Equals(parsed.Address, text, StringComparison.Ordinal)
        && parsed.Host.Contains('.', StringComparison.Ordinal);

    /// <summary>Why a reason can't be used, or null. Every search says why, for the audit log.</summary>
    public static string? ReasonProblem(string? reason)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length < MinReasonLength)
        {
            return "Say why, for the audit log. For example: subject access request 2026-014.";
        }
        return text.Length > MaxReasonLength || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r')
            ? $"Use at most {MaxReasonLength} characters of plain text."
            : null;
    }

    /// <summary>
    /// The account names a directory account adds to the search: its display name, and its logon name when that is
    /// long enough to be about one person and doesn't just repeat it.
    /// </summary>
    public static IEnumerable<string> AccountNames(string logonName, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length >= MinNameLength)
        {
            yield return displayName.Trim();
        }
        if (logonName.Length >= 4 && !string.Equals(logonName, displayName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            yield return logonName;
        }
    }

    /// <summary>A LIKE pattern matching <paramref name="text"/> anywhere.</summary>
    public static string Contains(string text) => "%" + Like(text) + "%";

    /// <summary>Escapes text for LIKE with the backslash escape, so it matches literally.</summary>
    public static string Like(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
