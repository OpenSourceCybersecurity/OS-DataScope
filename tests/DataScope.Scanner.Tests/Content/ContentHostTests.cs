using System.Buffers.Binary;
using System.Text;
using DataScope.Core.Classification;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Isolation;

namespace DataScope.Scanner.Tests.Content;

/// <summary>The reader process's side of D127, run in memory. The real process is tested in the API tests.</summary>
public sealed class ContentHostTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ds-host-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static (int Exit, List<HostResponse> Responses) Run(params object?[] messages)
    {
        var input = new MemoryStream();
        foreach (var m in messages)
        {
            if (m is byte[] raw)
            {
                input.Write(raw);
            }
            else
            {
                HostProtocol.Write(input, m);
            }
        }
        input.Position = 0;
        var output = new MemoryStream();
        var exit = ContentHost.Run(input, output);
        output.Position = 0;
        var responses = new List<HostResponse>();
        while (HostProtocol.Read<HostResponse>(output, 1 << 24) is { } r)
        {
            responses.Add(r);
        }
        return (exit, responses);
    }

    [Fact]
    public void Answers_a_start_check_then_reads_files_in_order()
    {
        var csv = Path.Combine(dir, "staff.dat");
        File.WriteAllText(csv, "Name,SSN\nAvery,512-48-3307\n");
        var (exit, responses) = Run(new ContentLimits(), new HostRequest(null), new HostRequest(csv), new HostRequest(Path.Combine(dir, "gone.txt")));
        Assert.Equal(0, exit);
        Assert.Equal(3, responses.Count);
        Assert.Null(responses[0].Text);
        Assert.Equal(ContentState.Scanned, responses[1].State);
        Assert.Contains("512-48-3307", responses[1].Text, StringComparison.Ordinal);
        Assert.Equal(ContentState.NotFound, responses[2].State);
    }

    [Fact]
    public void Uses_the_limits_it_was_given()
    {
        var log = Path.Combine(dir, "big.log");
        File.WriteAllText(log, new string('x', 5_000));
        var (_, responses) = Run(new ContentLimits { MaxChars = 1_000 }, new HostRequest(log));
        Assert.Equal(ContentState.Partial, responses[0].State);
        Assert.Equal(1_000, responses[0].Text!.Length);
    }

    [Fact]
    public void A_mailbox_returns_more_text_than_one_file_may()
    {
        var pst = Path.Combine(dir, "mail.pst");
        var builder = new PstBuilder();
        for (var i = 0; i < 40; i++)
        {
            builder.Add(new PstMessage { Subject = $"Message {i}", Body = new string('é', 80) });
        }
        File.WriteAllBytes(pst, builder.Build());
        var limits = new ContentLimits { MaxChars = 1_000, MaxMailboxChars = 5_000 };
        var (_, responses) = Run(limits, new HostRequest(pst));
        Assert.Equal(ContentState.Scanned, responses[0].State);
        Assert.InRange(responses[0].Text!.Length, 1_001, 5_000);

        // The worker accepts a response that size, in the longest encoding its characters can take.
        var stream = new MemoryStream();
        HostProtocol.Write(stream, new HostResponse(ContentState.Scanned, new string('\u0001', limits.MaxCharsPerFile), null));
        stream.Position = 0;
        Assert.NotNull(HostProtocol.Read<HostResponse>(stream, HostProtocol.MaxResponseBytes(limits)));
    }

    [Fact]
    public void Exits_quietly_when_the_worker_closes_the_pipe()
    {
        Assert.Equal((0, 0), (Run().Exit, Run().Responses.Count));
        Assert.Equal(0, Run(new ContentLimits()).Exit);
    }

    [Fact]
    public void Refuses_limits_outside_the_allowed_range()
    {
        var (exit, responses) = Run(new ContentLimits { MaxChars = 5 }, new HostRequest(null));
        Assert.Equal(2, exit);
        Assert.Empty(responses);
    }

    [Fact]
    public void Refuses_a_message_longer_than_allowed_without_reading_it()
    {
        var huge = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(huge, int.MaxValue);
        var (exit, responses) = Run(new ContentLimits(), huge);
        Assert.Equal(2, exit);
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData(new byte[] { 0x10, 0x00 })]
    [InlineData(new byte[] { 0x05, 0x00, 0x00, 0x00, 0x7B, 0x7B })]
    public void Refuses_cut_off_or_broken_messages(byte[] raw) => Assert.Equal(2, Run(new ContentLimits(), raw).Exit);

    [Fact]
    public void Protocol_round_trips_text_with_any_characters()
    {
        var text = "Ünïcødé \u0000 \u001F \"quotes\" \\ 😀 " + new string('y', 10_000);
        var stream = new MemoryStream();
        HostProtocol.Write(stream, new HostResponse(ContentState.Partial, text, "note"));
        stream.Position = 0;
        var back = HostProtocol.Read<HostResponse>(stream, HostProtocol.MaxResponseBytes(new ContentLimits()));
        Assert.Equal(text, back!.Text);
        Assert.Equal(ContentState.Partial, back.State);
    }

    [Fact]
    public void The_worker_refuses_a_response_over_its_limit()
    {
        var stream = new MemoryStream();
        HostProtocol.Write(stream, new HostResponse(ContentState.Scanned, new string('z', 5_000), null));
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => HostProtocol.Read<HostResponse>(stream, 1_000));
        Assert.Throws<InvalidDataException>(() => HostProtocol.Read<HostResponse>(new MemoryStream(Encoding.UTF8.GetBytes("\u0002\0\0\0[]")), 100));
    }

    [Fact]
    public void Starts_itself_with_dotnet_unless_it_is_the_installed_worker()
    {
        var start = ContentHostCommand.For("/opt/datascope/DataScope.Worker.dll");
        Assert.Equal(["/opt/datascope/DataScope.Worker.dll", ContentHost.Argument], start.ArgumentList);
        Assert.Contains("dotnet", Path.GetFileName(start.FileName), StringComparison.OrdinalIgnoreCase);
    }
}
