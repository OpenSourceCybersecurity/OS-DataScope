using System.Text.Json;
using System.Text.Json.Nodes;
using DataScope.Persistence.Configuration;

namespace DataScope.Setup;

/// <summary>
/// Reads and edits datascope.json while keeping settings it doesn't touch.
/// Saves through a temporary file in the same folder, so a crash never leaves
/// a half-written file and the new file inherits the folder's locked-down ACL.
/// Passwords are saved encrypted with DPAPI on Windows (see <see cref="SettingSecrets"/>).
/// </summary>
internal sealed class ConfigFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly JsonObject _root;
    private readonly ISecretCipher? _cipher;

    public string Path { get; }

    private ConfigFile(string path, JsonObject root, ISecretCipher? cipher)
    {
        Path = path;
        _root = root;
        _cipher = cipher;
    }

    public static ConfigFile Load(string path) => Load(path, SettingSecrets.Default);

    public static ConfigFile Load(string path, ISecretCipher? cipher)
    {
        if (!File.Exists(path))
        {
            return new ConfigFile(path, [], cipher);
        }
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node is JsonObject obj
            ? new ConfigFile(path, obj, cipher)
            : throw new SetupException($"{path} is not a JSON object. Fix or remove it, then run setup again.");
    }

    /// <summary>True when passwords are saved encrypted (on Windows).</summary>
    public bool EncryptsSecrets => _cipher is not null;

    /// <summary>A password setting, decrypted. Throws a <see cref="SetupException"/> when it was encrypted on another computer.</summary>
    public string? GetSecret(params string[] keys)
    {
        var value = GetString(keys);
        try
        {
            return value is null ? null : SettingSecrets.Reveal(value, _cipher, string.Join(':', keys));
        }
        catch (SecretUnreadableException ex)
        {
            throw new SetupException(ex.Message);
        }
    }

    /// <summary>Saves a password setting, encrypted when this computer can.</summary>
    public void SetSecret(string value, params string[] keys) =>
        Set(_cipher is null ? value : SettingSecrets.Protect(value, _cipher), keys);

    /// <summary>Password settings that are still plain text, as "A:B:C".</summary>
    public IReadOnlyList<string> PlainSecrets() =>
        [.. SettingSecrets.Keys.Where(k => GetString(k) is { Length: > 0 } v && !SettingSecrets.IsProtected(v)).Select(k => string.Join(':', k))];

    /// <summary>Encrypts every password setting that is still plain text. Returns how many it changed.</summary>
    public int ProtectSecrets()
    {
        if (_cipher is null)
        {
            return 0;
        }
        var changed = 0;
        foreach (var keys in SettingSecrets.Keys)
        {
            if (GetString(keys) is { Length: > 0 } value && !SettingSecrets.IsProtected(value))
            {
                SetSecret(value, keys);
                changed++;
            }
        }
        return changed;
    }

    public bool Exists => File.Exists(Path);

    public string? GetString(params string[] keys) => Find(keys) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public int? GetInt(params string[] keys) => Find(keys) is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    public bool? GetBool(params string[] keys) => Find(keys) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public IReadOnlyList<string> GetStrings(params string[] keys) =>
        Find(keys) is JsonArray a ? [.. a.Select(n => n?.GetValue<string>()).OfType<string>()] : [];

    public void Set(JsonNode? value, params string[] keys)
    {
        var parent = _root;
        foreach (var key in keys[..^1])
        {
            if (parent[key] is not JsonObject child)
            {
                parent[key] = child = [];
            }
            parent = child;
        }
        parent[keys[^1]] = value;
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(dir);
        var temp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, _root.ToJsonString(Indented));
        File.Move(temp, Path, overwrite: true);
    }

    private JsonNode? Find(string[] keys)
    {
        JsonNode? node = _root;
        foreach (var key in keys)
        {
            node = node is JsonObject obj && obj.TryGetPropertyValue(key, out var next) ? next : null;
        }
        return node;
    }
}

/// <summary>An error the person running setup can act on; printed without a stack trace.</summary>
internal sealed class SetupException(string message) : Exception(message);
