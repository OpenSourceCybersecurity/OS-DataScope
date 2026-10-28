using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataScope.Setup;

/// <summary>What a backup file says about itself, readable without the key. Holds no customer data.</summary>
internal sealed record BackupHeader(string Product, DateTimeOffset CreatedAt, string Database, string LatestMigration, string KeyId, string ProductVersion);

/// <summary>
/// An encrypted database backup: a plain header, then the pg_dump archive in 1 MiB chunks, each sealed with AES-256-GCM.
/// Every chunk's authentication covers the header, its position and whether it is the last one, so a changed, reordered
/// or cut-off file is refused rather than half restored.
/// </summary>
internal static class BackupFormat
{
    public const string Extension = ".dsbackup";
    public const int ChunkSize = 1 << 20;
    private const int TagSize = 16;
    private const int MaxHeader = 64 * 1024;
    private static readonly byte[] Magic = "DATASCOPE-BACKUP-1\n"u8.ToArray();

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>A short fingerprint of a key, stored in each backup, so a wrong key is named rather than guessed at.</summary>
    public static string KeyId(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..16].ToLowerInvariant();

    public static async Task EncryptAsync(Stream plain, Stream output, byte[] key, BackupHeader header, CancellationToken ct)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        var prefix = RandomNumberGenerator.GetBytes(8);
        await output.WriteAsync(Magic, ct);
        await output.WriteAsync(Int32(headerBytes.Length), ct);
        await output.WriteAsync(headerBytes, ct);
        await output.WriteAsync(prefix, ct);
        var context = Context(headerBytes, prefix);

        using var aes = new AesGcm(key, TagSize);
        var buffer = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        for (long index = 0; ; index++)
        {
            var read = await plain.ReadAtLeastAsync(buffer, ChunkSize, throwOnEndOfStream: false, ct);
            var final = read < ChunkSize;
            aes.Encrypt(Nonce(prefix, index), buffer.AsSpan(0, read), cipher.AsSpan(0, read), tag, Aad(context, index, final));
            await output.WriteAsync(new[] { (byte)(final ? 1 : 0) }, ct);
            await output.WriteAsync(Int32(read), ct);
            await output.WriteAsync(cipher.AsMemory(0, read), ct);
            await output.WriteAsync(tag, ct);
            if (final)
            {
                return;
            }
        }
    }

    public static async Task<BackupHeader> ReadHeaderAsync(Stream input, CancellationToken ct) => (await ReadStartAsync(input, ct)).Header;

    /// <summary>Decrypts to <paramref name="plain"/>. Throws <see cref="SetupException"/> for a wrong key or a damaged file.</summary>
    public static async Task<BackupHeader> DecryptAsync(Stream input, Stream plain, byte[] key, CancellationToken ct)
    {
        var (header, headerBytes, prefix) = await ReadStartAsync(input, ct);
        if (header.KeyId != KeyId(key))
        {
            throw new SetupException($"This backup was made with a different backup key (key {header.KeyId}; this server's is {KeyId(key)}). Supply the key it was made with.");
        }
        var context = Context(headerBytes, prefix);
        using var aes = new AesGcm(key, TagSize);
        var cipher = new byte[ChunkSize];
        var buffer = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var meta = new byte[5];
        for (long index = 0; ; index++)
        {
            await ReadExactly(input, meta, ct);
            var final = meta[0] switch
            {
                0 => false,
                1 => true,
                _ => throw Damaged(),
            };
            var length = BinaryPrimitives.ReadInt32LittleEndian(meta.AsSpan(1));
            if (length < 0 || length > ChunkSize || (!final && length != ChunkSize))
            {
                throw Damaged();
            }
            await ReadExactly(input, cipher.AsMemory(0, length), ct);
            await ReadExactly(input, tag, ct);
            try
            {
                aes.Decrypt(Nonce(prefix, index), cipher.AsSpan(0, length), tag, buffer.AsSpan(0, length), Aad(context, index, final));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw Damaged();
            }
            await plain.WriteAsync(buffer.AsMemory(0, length), ct);
            if (final)
            {
                if (input.ReadByte() != -1)
                {
                    throw Damaged();
                }
                return header;
            }
        }
    }

    private static async Task<(BackupHeader Header, byte[] HeaderBytes, byte[] Prefix)> ReadStartAsync(Stream input, CancellationToken ct)
    {
        var magic = new byte[Magic.Length];
        if (await input.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, ct) != magic.Length || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new SetupException("This is not a Data Scope backup file.");
        }
        var lengthBytes = new byte[4];
        await ReadExactly(input, lengthBytes, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > MaxHeader)
        {
            throw Damaged();
        }
        var headerBytes = new byte[length];
        await ReadExactly(input, headerBytes, ct);
        var prefix = new byte[8];
        await ReadExactly(input, prefix, ct);
        try
        {
            var header = JsonSerializer.Deserialize<BackupHeader>(headerBytes) ?? throw Damaged();
            return (header, headerBytes, prefix);
        }
        catch (JsonException)
        {
            throw Damaged();
        }
    }

    private static SetupException Damaged() =>
        new("The backup file is damaged or incomplete (it failed its integrity check). Nothing was restored from it.");

    private static async Task ReadExactly(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            await input.ReadExactlyAsync(buffer, ct);
        }
        catch (EndOfStreamException)
        {
            throw Damaged();
        }
    }

    private static byte[] Context(byte[] headerBytes, byte[] prefix) => SHA256.HashData([.. Magic, .. headerBytes, .. prefix]);

    private static byte[] Aad(byte[] context, long index, bool final)
    {
        var aad = new byte[context.Length + 9];
        context.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(context.Length), index);
        aad[^1] = (byte)(final ? 1 : 0);
        return aad;
    }

    private static byte[] Nonce(byte[] prefix, long index)
    {
        if (index > uint.MaxValue)
        {
            throw new SetupException("The backup is too large for this format.");
        }
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), (uint)index);
        return nonce;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>Encodes a key for people to copy: 64 hex digits in groups of eight.</summary>
    public static string FormatKey(byte[] key) =>
        string.Join('-', Convert.ToHexString(key).ToLowerInvariant().Chunk(8).Select(c => new string(c)));

    public static byte[] ParseKey(string text)
    {
        var hex = new string([.. text.Where(Uri.IsHexDigit)]);
        if (hex.Length != 64)
        {
            throw new SetupException("A backup key is 64 hexadecimal digits (dashes and spaces are ignored).");
        }
        return Convert.FromHexString(hex);
    }
}
