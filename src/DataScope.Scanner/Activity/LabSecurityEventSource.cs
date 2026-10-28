using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using DataScope.Core.Activity;

namespace DataScope.Scanner.Activity;

/// <summary>
/// Development and lab stand-in for a file server's Security log: <c>{root}/{host}.security-events.xml</c>, in the
/// format <c>wevtutil qe Security /f:xml /e:Events</c> writes (written with synthetic events by
/// <c>scripts/make-lab.mjs</c>). Never registered in production.
/// </summary>
public sealed partial class LabSecurityEventSource(string root) : ISecurityEventSource
{
    /// <summary>Largest lab event file read.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        Async = true,
    };

    [GeneratedRegex(@"<(?:\w+:)?EventRecordID>\s*(\d{1,18})\s*</(?:\w+:)?EventRecordID>")]
    private static partial Regex RecordIdPattern();

    public async Task<IReadOnlyList<RawSecurityEvent>> ReadAsync(string hostname, long? afterRecordId, int max, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var events = await LoadAsync(hostname, ct);
        return events.Where(e => afterRecordId is null || e.RecordId > afterRecordId).OrderBy(e => e.RecordId).Take(max).ToList();
    }

    /// <summary>The lab file stands for the whole log: its lowest and highest record ids are the log's oldest and newest.</summary>
    public async Task<SecurityLogInfo> GetLogInfoAsync(string hostname, CancellationToken ct)
    {
        var events = await LoadAsync(hostname, ct);
        if (events.Count == 0)
        {
            return SecurityLogInfo.Empty;
        }
        var oldest = events.MinBy(e => e.RecordId)!;
        var oldestAt = SecurityEventXml.TryParse(oldest.Xml, out var parsed, out _) ? parsed!.TimeCreated : (DateTimeOffset?)null;
        return new SecurityLogInfo(oldest.RecordId, events.Max(e => e.RecordId), events.Count, oldestAt, new FileInfo(FileFor(hostname)).Length, MaxFileBytes, null);
    }

    private string FileFor(string hostname)
    {
        if (hostname.Length == 0 || hostname.IndexOfAny(['/', '\\', ':']) >= 0 || hostname.Contains("..", StringComparison.Ordinal))
        {
            throw new SecurityEventReadException("The server name is not valid for the lab event folder.");
        }
        return Path.Combine(root, $"{hostname}.security-events.xml");
    }

    private async Task<List<RawSecurityEvent>> LoadAsync(string hostname, CancellationToken ct)
    {
        var file = FileFor(hostname);
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            throw new SecurityEventReadException($"The lab has no Security log for {hostname} ({hostname}.security-events.xml).");
        }
        if (info.Length > MaxFileBytes)
        {
            throw new SecurityEventReadException($"The lab Security log for {hostname} is larger than {MaxFileBytes / 1024 / 1024} MB.");
        }

        var events = new List<RawSecurityEvent>();
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            using var reader = XmlReader.Create(stream, Settings);
            await reader.MoveToContentAsync();
            if (reader.LocalName != "Events" || reader.IsEmptyElement)
            {
                return events;
            }
            await reader.ReadAsync();
            var position = 0;
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Events"))
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    await reader.ReadAsync();
                    continue;
                }
                position++;
                var xml = await reader.ReadOuterXmlAsync();
                var match = RecordIdPattern().Match(xml);
                if (!match.Success)
                {
                    throw new SecurityEventReadException($"Event {position} in the lab Security log for {hostname} has no EventRecordID.");
                }
                var recordId = long.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
                events.Add(new RawSecurityEvent(recordId, xml));
            }
        }
        catch (XmlException ex)
        {
            throw new SecurityEventReadException($"The lab Security log for {hostname} is not well-formed XML (line {ex.LineNumber}).", ex);
        }
        return events;
    }
}
