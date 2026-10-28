using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Isolation;

/// <summary>A file for the reader process to read. A null path asks it only to answer, to show it started.</summary>
internal sealed record HostRequest(string? Path);

/// <summary>What the reader process read, or why it couldn't.</summary>
internal sealed record HostResponse(ContentState State, string? Text, string? Note);

/// <summary>
/// The worker and the reader process talk over the reader's standard input and output, one message at a time: a
/// four-byte length, then that many bytes of JSON. Each side refuses a message longer than it expects, so neither can
/// be made to allocate without limit by the other.
/// </summary>
internal static class HostProtocol
{
    /// <summary>Requests and the limits sent at start are small: a path, or a handful of numbers.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };

    /// <summary>The longest response to accept: the extracted text, escaped as JSON at worst, plus the note.</summary>
    public static int MaxResponseBytes(ContentLimits limits) => checked(limits.MaxCharsPerFile * 6 + 64 * 1024);

    public static void Write<T>(Stream stream, T message)
    {
        // One write per message: the length and the body together, so the other side wakes once.
        var buffer = new ArrayBufferWriter<byte>(256);
        buffer.Advance(4);
        using (var json = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(json, message, Json);
        }
        var written = buffer.WrittenMemory;
        var frame = MemoryMarshal.AsMemory(written);
        BinaryPrimitives.WriteInt32LittleEndian(frame.Span, written.Length - 4);
        stream.Write(written.Span);
        stream.Flush();
    }

    /// <summary>The next message, or default when the other side closed the stream cleanly before a new message.</summary>
    /// <exception cref="InvalidDataException">The message is cut off, too long or not what was expected.</exception>
    public static T? Read<T>(Stream stream, int maxBytes)
    {
        Span<byte> length = stackalloc byte[4];
        var got = stream.ReadAtLeast(length, 4, throwOnEndOfStream: false);
        if (got == 0)
        {
            return default;
        }
        if (got < 4)
        {
            throw new InvalidDataException("The message was cut off.");
        }
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 2 || size > maxBytes)
        {
            throw new InvalidDataException("The message is longer than allowed.");
        }
        var body = new byte[size];
        if (stream.ReadAtLeast(body, size, throwOnEndOfStream: false) < size)
        {
            throw new InvalidDataException("The message was cut off.");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("The message was empty.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The message is not valid.");
        }
    }
}
