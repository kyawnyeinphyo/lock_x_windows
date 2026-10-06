using System.Buffers.Binary;
using System.Text;

namespace LockX.Core.Crypto;

/// <summary>
/// On-disk format for encrypted item payloads (same as the macOS build).
///
/// The payload is split into fixed-size chunks, each sealed independently with
/// AES-GCM. That costs 28 bytes per chunk but buys random access, which is what
/// lets a video stream out of the vault without landing on disk in the clear.
///
///     header  24 bytes  magic(8) version(1) reserved(3) chunkSize(4 LE) plainSize(8 LE)
///     chunk i           nonce(12) ciphertext(n) tag(16)
///
/// Each chunk authenticates its own index and item id, so chunks cannot be
/// reordered, duplicated or spliced in from another file.
/// </summary>
public static class BlobCrypto
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOCKXBLB");
    public const byte Version = 1;
    public const int HeaderSize = 24;
    public const int ChunkOverhead = 28;
    public const int DefaultChunkSize = 1 << 20; // 1 MiB

    public readonly record struct Header(int ChunkSize, long PlainSize);

    public static byte[] Aad(Guid itemId, long chunkIndex)
    {
        var d = new byte[24];
        itemId.TryWriteBytes(d, bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(16), (ulong)chunkIndex);
        return d;
    }

    static byte[] MakeHeader(int chunkSize, long plainSize)
    {
        var h = new byte[HeaderSize];
        Magic.CopyTo(h, 0);
        h[8] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)chunkSize);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(16), (ulong)plainSize);
        return h;
    }

    public static Header ParseHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || !d[..8].SequenceEqual(Magic))
            throw new CryptoException("The encrypted file is corrupt or not a Lock X blob.");
        if (d[8] != Version) throw new CryptoException($"Unsupported vault format (version {d[8]}).");
        uint chunk = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12, 4));
        ulong plain = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(16, 8));
        if (chunk == 0) throw new CryptoException("The encrypted file is corrupt or not a Lock X blob.");
        return new Header((int)chunk, (long)plain);
    }

    public static long ChunkFileOffset(long index, int chunkSize) =>
        HeaderSize + index * (chunkSize + ChunkOverhead);

    // ---- In memory -------------------------------------------------------

    public static byte[] Encrypt(byte[] data, byte[] key, Guid itemId, int chunkSize = DefaultChunkSize)
    {
        using var ms = new MemoryStream();
        ms.Write(MakeHeader(chunkSize, data.Length));
        long index = 0;
        for (int offset = 0; offset < data.Length; offset += chunkSize, index++)
        {
            int n = Math.Min(chunkSize, data.Length - offset);
            ms.Write(Sealed.Seal(data.AsSpan(offset, n), key, Aad(itemId, index)));
        }
        return ms.ToArray();
    }

    public static byte[] Decrypt(byte[] blob, byte[] key, Guid itemId)
    {
        var header = ParseHeader(blob);
        var output = new byte[header.PlainSize];
        int written = 0, offset = HeaderSize;
        long index = 0;
        while (offset < blob.Length)
        {
            int end = (int)Math.Min((long)offset + header.ChunkSize + ChunkOverhead, blob.Length);
            var plain = Sealed.Open(blob.AsSpan(offset, end - offset), key, Aad(itemId, index));
            if (written + plain.Length > output.Length) throw new CryptoException("The encrypted file is corrupt.");
            plain.CopyTo(output, written);
            written += plain.Length;
            offset = end;
            index++;
        }
        if (written != output.Length) throw new CryptoException("The encrypted file is truncated.");
        return output;
    }

    // ---- Streaming to / from disk ---------------------------------------

    /// <summary>Encrypts a source file straight to <paramref name="destination"/> without holding it in memory.</summary>
    public static long EncryptFile(string source, string destination, byte[] key, Guid itemId,
                                   int chunkSize = DefaultChunkSize)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long plainSize = input.Length;
        try
        {
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            output.Write(MakeHeader(chunkSize, plainSize));
            var buffer = new byte[chunkSize];
            long index = 0;
            long remaining = plainSize;
            while (remaining > 0)
            {
                int want = (int)Math.Min(chunkSize, remaining);
                input.ReadExactly(buffer, 0, want);
                output.Write(Sealed.Seal(buffer.AsSpan(0, want), key, Aad(itemId, index++)));
                remaining -= want;
            }
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
        return plainSize;
    }

    public static void Write(byte[] data, string destination, byte[] key, Guid itemId)
    {
        var tmp = destination + ".tmp";
        File.WriteAllBytes(tmp, Encrypt(data, key, itemId));
        File.Move(tmp, destination, overwrite: true);
    }

    public static byte[] ReadAll(string path, byte[] key, Guid itemId)
    {
        using var reader = new BlobReader(path, key, itemId);
        return reader.Read(0, checked((int)reader.PlainSize));
    }

    /// <summary>Decrypts to a plaintext file. Used for explicit user exports and temporary "open with" copies.</summary>
    public static void ExportFile(string path, string destination, byte[] key, Guid itemId)
    {
        using var reader = new BlobReader(path, key, itemId);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        long offset = 0;
        while (offset < reader.PlainSize)
        {
            int n = (int)Math.Min(reader.ChunkSize, reader.PlainSize - offset);
            output.Write(reader.Read(offset, n));
            offset += n;
        }
    }
}

/// <summary>Random-access reader over a chunked blob. Only chunks covering the requested range are decrypted.</summary>
public sealed class BlobReader : IDisposable
{
    readonly FileStream _file;
    readonly byte[] _key;
    readonly Guid _itemId;
    readonly object _gate = new();
    long _cachedIndex = -1;
    byte[] _cachedChunk = Array.Empty<byte>();

    public int ChunkSize { get; }
    public long PlainSize { get; }

    public BlobReader(string path, byte[] key, Guid itemId)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _key = (byte[])key.Clone();
        _itemId = itemId;
        var head = new byte[BlobCrypto.HeaderSize];
        int got = _file.Read(head, 0, head.Length);
        var header = BlobCrypto.ParseHeader(head.AsSpan(0, got));
        ChunkSize = header.ChunkSize;
        PlainSize = header.PlainSize;
    }

    public byte[] Read(long offset, int length)
    {
        if (offset < 0 || offset > PlainSize) throw new CryptoException("Requested a byte range outside the file.");
        int want = (int)Math.Min(length, PlainSize - offset);
        if (want <= 0) return Array.Empty<byte>();

        lock (_gate)
        {
            var output = new byte[want];
            int got = 0;
            long cursor = offset;
            while (got < want)
            {
                long index = cursor / ChunkSize;
                int inChunk = (int)(cursor % ChunkSize);
                var chunk = ChunkData(index);
                if (inChunk >= chunk.Length) break;
                int take = Math.Min(chunk.Length - inChunk, want - got);
                Buffer.BlockCopy(chunk, inChunk, output, got, take);
                got += take;
                cursor += take;
            }
            return got == want ? output : output.AsSpan(0, got).ToArray();
        }
    }

    byte[] ChunkData(long index)
    {
        if (index == _cachedIndex) return _cachedChunk;
        _file.Seek(BlobCrypto.ChunkFileOffset(index, ChunkSize), SeekOrigin.Begin);
        var sealedBuf = new byte[ChunkSize + BlobCrypto.ChunkOverhead];
        int n = 0;
        while (n < sealedBuf.Length)
        {
            int more = _file.Read(sealedBuf, n, sealedBuf.Length - n);
            if (more <= 0) break;
            n += more;
        }
        if (n <= 0) throw new CryptoException("Requested a byte range outside the file.");
        var plain = Sealed.Open(sealedBuf.AsSpan(0, n), _key, BlobCrypto.Aad(_itemId, index));
        _cachedIndex = index;
        _cachedChunk = plain;
        return plain;
    }

    public void Dispose()
    {
        _file.Dispose();
        Array.Clear(_key);
    }
}
