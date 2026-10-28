using DataScope.Core.Activity;
using DataScope.Tests.Activity;

namespace DataScope.Core.Tests.Activity;

public class SecurityEventXmlTests
{
    [Fact]
    public void Reads_the_header_and_named_data_of_a_rendered_event()
    {
        var ev = SecurityEventXml.Parse(EventXml.ObjectAccess(273866, @"C:\Shares\Finance\Budget.xlsx", 0x1));

        Assert.Equal(4663, ev.EventId);
        Assert.Equal(273866, ev.RecordId);
        Assert.Equal("Security", ev.Channel);
        Assert.Equal("Microsoft-Windows-Security-Auditing", ev.Provider);
        Assert.Equal("fs01.northwind.test", ev.Computer);
        Assert.Equal(EventXml.T0.AddSeconds(273866), ev.TimeCreated);
        Assert.Equal(EventXml.Success, ev.Keywords);
        Assert.Equal(@"C:\Shares\Finance\Budget.xlsx", ev.Get("ObjectName"));
        Assert.Equal(EventXml.Alice, ev.Get("SubjectUserSid"));
        Assert.Null(ev.Get("ProcessName")); // empty values read as missing
    }

    [Fact]
    public void Keeps_microseconds_and_reads_time_as_utc()
    {
        var xml = EventXml.ObjectAccess(1, @"C:\a.txt", 1, time: new DateTimeOffset(2026, 10, 1, 9, 0, 1, TimeSpan.Zero).AddTicks(1_234_567));
        var ev = SecurityEventXml.Parse(xml);
        Assert.Equal(TimeSpan.Zero, ev.TimeCreated.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 1, TimeSpan.Zero).AddTicks(1_234_560), ev.TimeCreated);
    }

    [Fact]
    public void Reads_user_data_events_such_as_log_cleared()
    {
        var ev = SecurityEventXml.Parse(EventXml.LogCleared(500));
        Assert.Equal(1102, ev.EventId);
        Assert.Equal("Microsoft-Windows-Eventlog", ev.Provider);
        Assert.Equal(EventXml.Alice, ev.Get("SubjectUserSid"));
    }

    [Fact]
    public void Accepts_events_without_the_namespace()
    {
        var xml = EventXml.ObjectAccess(7, @"C:\a.txt", 1).Replace(" xmlns='http://schemas.microsoft.com/win/2004/08/events/event'", "");
        Assert.Equal(7, SecurityEventXml.Parse(xml).RecordId);
    }

    [Theory]
    [InlineData("", "not well-formed")]
    [InlineData("not xml at all", "not well-formed")]
    [InlineData("<Event><System>", "not well-formed")]
    [InlineData("<Other/>", "not a Windows event")]
    [InlineData("<Event xmlns='urn:something-else'><System/></Event>", "not a Windows event")]
    [InlineData("<Event/>", "no System")]
    [InlineData("<Event><System><EventID>x</EventID></System></Event>", "EventID")]
    [InlineData("<Event><System><EventID>4663</EventID></System></Event>", "EventRecordID")]
    [InlineData("<Event><System><EventID>4663</EventID><EventRecordID>-5</EventRecordID></System></Event>", "EventRecordID")]
    [InlineData("<Event><System><EventID>4663</EventID><EventRecordID>1</EventRecordID><Channel>Security</Channel></System></Event>", "Provider")]
    [InlineData("<Event><System><Provider Name='p'/><EventID>4663</EventID><EventRecordID>1</EventRecordID><Channel>Security</Channel></System></Event>", "TimeCreated")]
    public void Refuses_damaged_events_without_quoting_them(string xml, string expected)
    {
        var ex = Assert.Throws<SecurityEventFormatException>(() => SecurityEventXml.Parse(xml));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(SecurityEventXml.TryParse(xml, out _, out var error));
        Assert.Equal(ex.Message, error);
    }

    [Fact]
    public void Refuses_entity_expansion_and_external_entities()
    {
        const string laughs = """
            <?xml version="1.0"?>
            <!DOCTYPE Event [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <Event><System><EventID>4663</EventID></System><EventData><Data Name='ObjectName'>&c;</Data></EventData></Event>
            """;
        const string external = """
            <?xml version="1.0"?>
            <!DOCTYPE Event [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <Event><System><EventID>4663</EventID></System><EventData><Data Name='ObjectName'>&x;</Data></EventData></Event>
            """;
        Assert.Throws<SecurityEventFormatException>(() => SecurityEventXml.Parse(laughs));
        var ex = Assert.Throws<SecurityEventFormatException>(() => SecurityEventXml.Parse(external));
        Assert.DoesNotContain("passwd", ex.Message);
    }

    [Fact]
    public void Refuses_oversized_events_and_too_many_fields()
    {
        var big = EventXml.ObjectAccess(1, @"C:\" + new string('a', SecurityEventXml.MaxChars), 1);
        Assert.Contains("larger", Assert.Throws<SecurityEventFormatException>(() => SecurityEventXml.Parse(big)).Message);

        var many = EventXml.Event(4663, 1, Enumerable.Range(0, SecurityEventXml.MaxDataFields + 1).Select(i => ($"F{i}", "v")));
        Assert.Contains("data fields", Assert.Throws<SecurityEventFormatException>(() => SecurityEventXml.Parse(many)).Message);
    }

    [Fact]
    public void File_names_are_kept_as_text_whatever_they_contain()
    {
        const string hostile = @"C:\Shares\Public\=cmd|' /C calc'!A0 <script>alert(1)</script> & ""quotes"".csv";
        var ev = SecurityEventXml.Parse(EventXml.ObjectAccess(1, hostile, 1));
        Assert.Equal(hostile, ev.Get("ObjectName"));
    }

    [Fact]
    public void Invalid_characters_are_refused_rather_than_stored()
    {
        var xml = EventXml.ObjectAccess(1, "C:\\a.txt", 1).Replace("C:\\a.txt", "C:\\a&#0;b.txt");
        Assert.Throws<SecurityEventFormatException>(() => SecurityEventXml.Parse(xml));
    }

    [Theory]
    [InlineData("0x1ac", 0x1ac)]
    [InlineData("0X1AC", 0x1ac)]
    [InlineData("1ac", 0x1ac)]
    [InlineData("0x7fffffffffffffff", long.MaxValue)]
    public void Reads_hexadecimal_fields(string text, long expected)
    {
        Assert.True(SecurityEventXml.TryParseHex(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("-")]
    [InlineData("0xffffffffffffffff")]
    [InlineData("0x10000000000000000")]
    [InlineData("zz")]
    public void Rejects_bad_hexadecimal_fields(string? text) => Assert.False(SecurityEventXml.TryParseHex(text, out _));
}
