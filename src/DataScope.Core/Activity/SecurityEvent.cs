using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DataScope.Core.Inventory;

namespace DataScope.Core.Activity;

/// <summary>
/// One Windows event as Windows rendered it (the XML that Event Viewer, <c>wevtutil qe /f:xml</c> and
/// <c>EventRecord.ToXml()</c> produce), reduced to its header and named data. Nothing is interpreted here.
/// </summary>
/// <param name="Keywords">The System/Keywords bit mask; bit 52 marks an audit success, bit 53 an audit failure.</param>
/// <param name="Data">EventData/Data by Name, or the fields of the UserData element (event 1102). First value wins.</param>
public sealed record SecurityEvent(
    int EventId,
    string Provider,
    string Channel,
    string? Computer,
    long RecordId,
    DateTimeOffset TimeCreated,
    ulong Keywords,
    IReadOnlyDictionary<string, string> Data)
{
    public const ulong AuditFailureKeyword = 0x0010_0000_0000_0000;
    public const ulong AuditSuccessKeyword = 0x0020_0000_0000_0000;

    public string? Get(string name) => Data.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}

/// <summary>An event that could not be read: not XML, not a Windows event, missing a required field, or over a limit.</summary>
public sealed class SecurityEventFormatException : Exception
{
    public SecurityEventFormatException() : base("The event could not be read.") { }

    public SecurityEventFormatException(string message) : base(message) { }

    public SecurityEventFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Reads one rendered Windows event. Event text comes from file servers and can carry anything a user can put in a
/// file name, so the reader refuses document type declarations and external entities, caps the size, the number of
/// data fields and every value, and never echoes event content in its error messages.
/// </summary>
public static class SecurityEventXml
{
    public const string Namespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Largest event accepted, in characters. Windows caps an event's data at 64 KB; real file events are 2 to 4 KB.</summary>
    public const int MaxChars = 65_536;

    public const int MaxDataFields = 64;

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxChars,
        MaxCharactersFromEntities = 1_024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = true,
    };

    public static SecurityEvent Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.Length > MaxChars)
        {
            throw new SecurityEventFormatException($"The event is larger than {MaxChars:N0} characters.");
        }
        XDocument doc;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, Settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            // XmlException messages can quote the input; only its position is reported.
            throw new SecurityEventFormatException($"The event is not well-formed XML (line {ex.LineNumber}, position {ex.LinePosition}).", ex);
        }

        var root = doc.Root!;
        if (root.Name.LocalName != "Event" || (root.Name.NamespaceName.Length > 0 && root.Name.NamespaceName != Namespace))
        {
            throw new SecurityEventFormatException("The XML is not a Windows event.");
        }
        var ns = root.Name.Namespace;
        var system = root.Element(ns + "System") ?? throw new SecurityEventFormatException("The event has no System section.");

        var eventId = ParseInt(system.Element(ns + "EventID")?.Value, "EventID");
        var recordId = ParseLong(system.Element(ns + "EventRecordID")?.Value, "EventRecordID");
        var provider = Text(system.Element(ns + "Provider")?.Attribute("Name")?.Value, 256) ?? throw Missing("Provider");
        var channel = Text(system.Element(ns + "Channel")?.Value, 256) ?? throw Missing("Channel");
        var computer = Text(system.Element(ns + "Computer")?.Value, 253);
        var time = ParseTime(system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value);
        var keywords = ParseKeywords(system.Element(ns + "Keywords")?.Value);

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.Element(ns + "EventData") is { } eventData)
        {
            foreach (var item in eventData.Elements(ns + "Data"))
            {
                if (item.Attribute("Name")?.Value is { Length: > 0 } name)
                {
                    Add(data, name, item.Value);
                }
            }
        }
        else if (root.Element(ns + "UserData")?.Elements().FirstOrDefault() is { } userData)
        {
            foreach (var item in userData.Elements())
            {
                Add(data, item.Name.LocalName, item.Value);
            }
        }
        return new SecurityEvent(eventId, provider, channel, computer, recordId, time, keywords, data);
    }

    public static bool TryParse(string xml, out SecurityEvent? parsed, out string? error)
    {
        try
        {
            parsed = Parse(xml);
            error = null;
            return true;
        }
        catch (SecurityEventFormatException ex)
        {
            parsed = null;
            error = ex.Message;
            return false;
        }
    }

    private static void Add(Dictionary<string, string> data, string name, string value)
    {
        if (data.Count >= MaxDataFields)
        {
            throw new SecurityEventFormatException($"The event has more than {MaxDataFields} data fields.");
        }
        var key = PathText.Sanitize(name, out _);
        data.TryAdd(key.Length > 128 ? key[..128] : key, PathText.Sanitize(value, out _));
    }

    private static SecurityEventFormatException Missing(string field) => new($"The event has no {field}.");

    private static string? Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var clean = PathText.Sanitize(value.Trim(), out _);
        return clean.Length > max ? clean[..max] : clean;
    }

    private static int ParseInt(string? value, string field) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new SecurityEventFormatException($"The event's {field} is missing or not a number.");

    private static long ParseLong(string? value, string field) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new SecurityEventFormatException($"The event's {field} is missing or not a number.");

    private static DateTimeOffset ParseTime(string? value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            throw new SecurityEventFormatException("The event's TimeCreated is missing or not a date.");
        }
        // PostgreSQL keeps microseconds; Windows writes 100-nanosecond ticks.
        return new DateTimeOffset(time.UtcTicks - (time.UtcTicks % 10), TimeSpan.Zero);
    }

    private static ulong ParseKeywords(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }
        var text = value.Trim();
        return ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new SecurityEventFormatException("The event's Keywords are not a number.");
    }

    /// <summary>Reads the "0x1ac" hexadecimal form Windows uses for masks, handles, process and logon ids.</summary>
    public static bool TryParseHex(string? value, out long result)
    {
        result = 0;
        if (value is null)
        {
            return false;
        }
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return text.Length is > 0 and <= 16
            && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unsigned)
            && unsigned <= long.MaxValue
            && (result = (long)unsigned) >= 0;
    }
}
