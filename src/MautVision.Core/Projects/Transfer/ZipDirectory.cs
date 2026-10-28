using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MautVision.Core.Projects.Transfer;

/// <summary>An entry of the central directory as stored: only stored and deflated entries without encryption are accepted.</summary>
internal sealed record ZipEntry(string Name, bool Deflated, uint Crc, long CompressedLength, long Length, long HeaderOffset);

/// <summary>The archive cannot be read as a ZIP (truncated, inconsistent records, a CRC mismatch).</summary>
internal sealed class ZipDamagedException(string message) : Exception(message);

/// <summary>Not a ZIP, or a ZIP feature this reader refuses: encryption, compression other than store and deflate, several disks.</summary>
internal sealed class ZipUnsupportedException(string message) : Exception(message);

/// <summary>
/// Reads the central directory of a ZIP (with Zip64) and the content of its entries through positional reads of one handle, so several
/// threads extract at once. It checks what an archive from an unknown source must not do: entries overlapping each other or the
/// directory (a zip bomb made of shared data), sizes that do not match the method, more output than declared, a wrong CRC.
/// </summary>
internal sealed class ZipDirectory : IDisposable
{
    internal const int MaxEntries = 10_000_000;
    private const long MaxDirectoryBytes = 1L << 30;
    // Deflate cannot expand data more than about 1032 times; a larger declared ratio is a forged size.
    private const long MaxDeflateRatio = 1032;
    private const int CopyBuffer = 1 << 20;

    private readonly SafeFileHandle handle;

    private ZipDirectory(SafeFileHandle handle, long length, IReadOnlyList<ZipEntry> entries)
    {
        this.handle = handle;
        Length = length;
        Entries = entries;
    }

    internal long Length { get; }

    internal IReadOnlyList<ZipEntry> Entries { get; }

    internal static ZipDirectory Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            var length = RandomAccess.GetLength(handle);
            return new ZipDirectory(handle, length, ReadDirectory(handle, length));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    /// <summary>Reads the whole content of a small entry (a manifest) into memory, checking its size and CRC.</summary>
    internal byte[] ReadAll(ZipEntry entry, int maxLength)
    {
        if (entry.Length > maxLength) throw new ZipDamagedException($"Entry {entry.Name} is larger than {maxLength} bytes.");
        using var target = new MemoryStream((int)entry.Length);
        CopyTo(entry, target, null, CancellationToken.None);
        return target.ToArray();
    }

    /// <summary>
    /// Writes the content of an entry to the target and checks its length and CRC; copied gets the number of bytes after every block.
    /// </summary>
    internal void CopyTo(ZipEntry entry, Stream target, Action<int>? copied, CancellationToken cancellationToken)
    {
        var start = DataOffset(entry);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
        try
        {
            var crc = new Crc32();
            long total = 0;
            using (var raw = new Window(handle, start, entry.CompressedLength))
            using (var source = entry.Deflated ? new DeflateStream(raw, CompressionMode.Decompress) : (Stream)raw)
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += read;
                    if (total > entry.Length) throw new ZipDamagedException($"Entry {entry.Name} is longer than declared.");
                    crc.Append(buffer.AsSpan(0, read));
                    target.Write(buffer, 0, read);
                    copied?.Invoke(read);
                }
            }
            if (total != entry.Length) throw new ZipDamagedException($"Entry {entry.Name} is shorter than declared.");
            if (crc.GetCurrentHashAsUInt32() != entry.Crc) throw new ZipDamagedException($"Entry {entry.Name} does not match its CRC-32.");
        }
        catch (InvalidDataException error)
        {
            throw new ZipDamagedException($"Entry {entry.Name} cannot be inflated: {error.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Start of the entry data after its local header, which must name the same entry.</summary>
    private long DataOffset(ZipEntry entry)
    {
        Span<byte> local = stackalloc byte[30];
        if (RandomAccess.Read(handle, local, entry.HeaderOffset) != 30 || BinaryPrimitives.ReadUInt32LittleEndian(local) != 0x04034b50)
            throw new ZipDamagedException($"Local header of {entry.Name} is missing.");
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
        var start = entry.HeaderOffset + 30 + nameLength + extraLength;
        if (start + entry.CompressedLength > Length) throw new ZipDamagedException($"Entry {entry.Name} goes past the end of the archive.");
        return start;
    }

    private static List<ZipEntry> ReadDirectory(SafeFileHandle handle, long length)
    {
        // The end record is within the last 64 KB + 22 bytes (the comment is at most 64 KB).
        var tailLength = (int)Math.Min(length, 65_557);
        if (tailLength < 22) throw new ZipUnsupportedException("The file is too short to be a ZIP.");
        var tail = new byte[tailLength];
        ReadExactly(handle, tail, length - tailLength);
        var end = -1;
        for (var index = tailLength - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50
                && index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) == tailLength)
            {
                end = index;
                break;
            }
        }
        if (end < 0)
        {
            // A file that starts as a ZIP but has no end record was cut short; anything else is not a ZIP at all.
            Span<byte> start = stackalloc byte[4];
            if (RandomAccess.Read(handle, start, 0) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(start) == 0x04034b50)
                throw new ZipDamagedException("The end of the central directory is missing: the archive is cut short.");
            throw new ZipUnsupportedException("The file is not a ZIP: the end of the central directory is missing.");
        }
        var record = tail.AsSpan(end);
        if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0)
            throw new ZipUnsupportedException("Archives split over several disks are not supported.");
        long count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        long directoryStart = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        var endOffset = length - tailLength + end;
        var directoryEnd = endOffset;
        if (end >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end - 20)) == 0x07064b50)
        {
            var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(end - 12));
            if (zip64Offset < 0 || zip64Offset + 56 > endOffset - 20) throw new ZipDamagedException("The Zip64 end record is out of the file.");
            var zip64 = new byte[56];
            ReadExactly(handle, zip64, zip64Offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50) throw new ZipDamagedException("The Zip64 end record is missing.");
            count = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(32));
            directorySize = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(40));
            directoryStart = BinaryPrimitives.ReadInt64LittleEndian(zip64.AsSpan(48));
            directoryEnd = zip64Offset;
        }
        if (count < 0 || count > MaxEntries || directorySize < 0 || directorySize > MaxDirectoryBytes || directoryStart < 0
            || directoryStart + directorySize > directoryEnd)
            throw new ZipDamagedException("The central directory is inconsistent.");
        var directory = new byte[directorySize];
        ReadExactly(handle, directory, directoryStart);
        var entries = new List<ZipEntry>((int)count);
        var offset = 0;
        for (long index = 0; index < count; index++)
        {
            if (offset + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset)) != 0x02014b50)
                throw new ZipDamagedException("A central directory record is damaged.");
            var header = directory.AsSpan(offset, 46);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
            long headerOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            if (offset + 46 + nameLength + extraLength + commentLength > directory.Length) throw new ZipDamagedException("A central directory record is cut.");
            var nameBytes = directory.AsSpan(offset + 46, nameLength);
            // Bit 0 — traditional encryption, bit 6 — strong encryption; method 99 — AES.
            if ((flags & 0x0041) != 0 || method == 99) throw new ZipUnsupportedException("Encrypted entries are not supported.");
            if (method is not (0 or 8)) throw new ZipUnsupportedException($"Compression method {method} is not supported.");
            ReadOnlySpan<byte> extra = directory.AsSpan(offset + 46 + nameLength, extraLength);
            while (extra.Length >= 4)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                if (4 + size > extra.Length) throw new ZipDamagedException("An extra field is cut.");
                if (tag == 0x0001)
                {
                    var values = extra.Slice(4, size);
                    if (uncompressed == uint.MaxValue) uncompressed = TakeLong(ref values);
                    if (compressed == uint.MaxValue) compressed = TakeLong(ref values);
                    if (headerOffset == uint.MaxValue) headerOffset = TakeLong(ref values);
                    if (disk == ushort.MaxValue && values.Length >= 4) disk = 0;
                }
                extra = extra[(4 + size)..];
            }
            if (disk != 0) throw new ZipUnsupportedException("Archives split over several disks are not supported.");
            var name = (flags & 0x0800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
            if (compressed < 0 || uncompressed < 0 || headerOffset < 0 || headerOffset + 30 + compressed > directoryStart
                || (method == 0 && compressed != uncompressed) || (method == 8 && uncompressed > (compressed + 1) * MaxDeflateRatio))
                throw new ZipDamagedException($"Entry {name} has inconsistent sizes.");
            entries.Add(new ZipEntry(name, method == 8, crc, compressed, uncompressed, headerOffset));
            offset += 46 + nameLength + extraLength + commentLength;
        }
        // Entries must not share data: each one ends before the next local header (an overlap is how quines and bombs reuse bytes).
        var ordered = entries.OrderBy(entry => entry.HeaderOffset).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            if (previous.HeaderOffset + 30 + previous.CompressedLength > ordered[index].HeaderOffset)
                throw new ZipDamagedException($"Entries {previous.Name} and {ordered[index].Name} overlap.");
        }
        return entries;
    }

    private static long TakeLong(ref ReadOnlySpan<byte> values)
    {
        if (values.Length < 8) throw new ZipDamagedException("A Zip64 extra field is cut.");
        var value = BinaryPrimitives.ReadInt64LittleEndian(values);
        values = values[8..];
        return value;
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            var read = RandomAccess.Read(handle, buffer, offset);
            if (read == 0) throw new ZipDamagedException("The file ends early.");
            buffer = buffer[read..];
            offset += read;
        }
    }

    /// <summary>A read-only view of a range of the archive through positional reads; each thread gets its own window.</summary>
    private sealed class Window(SafeFileHandle handle, long start, long length) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - position;
            if (remaining <= 0) return 0;
            if (buffer.Length > remaining) buffer = buffer[..(int)remaining];
            var read = RandomAccess.Read(handle, buffer, start + position);
            if (read == 0) throw new ZipDamagedException("The file ends early.");
            position += read;
            return read;
        }
    }
}
