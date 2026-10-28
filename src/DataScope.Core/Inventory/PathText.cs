using System.Text;

namespace DataScope.Core.Inventory;

public static class PathText
{
    /// <summary>
    /// Windows names may contain unpaired UTF-16 surrogates, which cannot be stored as text in PostgreSQL.
    /// Replaces them with U+FFFD and reports whether anything changed, so the loss is recorded as a gap.
    /// </summary>
    public static string Sanitize(string value, out bool lossy)
    {
        lossy = false;
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = c != '\0' && (!char.IsSurrogate(c)
                || (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])));
            if (char.IsHighSurrogate(c) && ok)
            {
                sb?.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }
            if (!ok)
            {
                lossy = true;
                sb ??= new StringBuilder(value, 0, i, value.Length);
                sb.Append('\uFFFD');
                continue;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? value;
    }

    /// <summary>Case-insensitive comparison key for a share-relative path (Windows semantics).</summary>
    public static string Key(string relativePath) => relativePath.ToUpperInvariant();

    /// <summary>Joins a share-relative folder path (backslash separated, empty for the root) with a child name.</summary>
    public static string Child(string parent, string name) => parent.Length == 0 ? name : parent + "\\" + name;

    /// <summary>Lower-case extension without the dot, at most 32 characters, or empty.</summary>
    public static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
        {
            return "";
        }
        var ext = fileName[(dot + 1)..].ToLowerInvariant();
        return ext.Length > 32 ? "" : ext;
    }
}
