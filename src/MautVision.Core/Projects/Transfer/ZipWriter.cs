using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace MautVision.Core.Projects.Transfer;

/// <summary>
/// Sequential ZIP writer with Zip64: the output is written once from start to end, never sought. An entry whose CRC is known before its
/// data (a file read whole by a prefetch thread) gets a complete local header; a streamed entry (a large file, deflated data) is
/// followed by a Zip64 data descriptor. The central directory and the Zip64 end records come last. Names are UTF-8 (flag bit 11).
/// </summary>
internal sealed class ZipWriter(Stream output)
{
    private const ushort Zip64Version = 45;
    private const ushort Utf8Flag = 0x0800;
    private const ushort DescriptorFlag = 0x0008;
    private const ushort Stored = 0;
    private const ushort Deflated = 8;
    private const uint Mask32 = uint.MaxValue;

    private readonly List<Entry> entries = [];
    private readonly byte[] header = new byte[64];
    private long position;
    private bool finished;

    /// <summary>Bytes written so far: the size of the archive when it is finished.</summary>
    internal long Position => position;

    internal int Count => entries.Count;

    /// <summary>A stored entry whose content is in memory with its CRC-32 (computed by the reader thread).</summary>
    internal void AddStored(string name, ReadOnlySpan<byte> content, uint crc, DateTime lastWriteTimeUtc)
    {
        var entry = Begin(name, Stored, 0, lastWriteTimeUtc);
        entry.Crc = crc;
        entry.Compressed = entry.Uncompressed = content.Length;
        WriteLocalHeader(entry, descriptor: false);
        Write(content);
    }

    /// <summary>A stored entry streamed from a file of known length; the CRC follows in the data descriptor.</summary>
    internal void AddStored(string name, Stream source, long length, DateTime lastWriteTimeUtc, byte[] buffer, Action<int>? copied,
        CancellationToken cancellationToken)
    {
        var entry = Begin(name, Stored, DescriptorFlag, lastWriteTimeUtc);
        WriteLocalHeader(entry, descriptor: true);
        var crc = new Crc32();
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, read));
            Write(buffer.AsSpan(0, read));
            total += read;
            copied?.Invoke(read);
        }
        if (total != length) throw new IOException($"File {name} changed while it was exported.");
        entry.Crc = crc.GetCurrentHashAsUInt32();
        entry.Compressed = entry.Uncompressed = total;
        WriteDescriptor(entry);
    }

    /// <summary>A deflated entry streamed from the source; the CRC and both sizes follow in the data descriptor.</summary>
    internal void AddDeflated(string name, Stream source, DateTime lastWriteTimeUtc, CompressionLevel level, byte[] buffer, Action<int>? copied,
        CancellationToken cancellationToken)
    {
        var entry = Begin(name, Deflated, DescriptorFlag, lastWriteTimeUtc);
        WriteLocalHeader(entry, descriptor: true);
        var start = position;
        var crc = new Crc32();
        long total = 0;
        using (var deflate = new DeflateStream(new Sink(this), level, leaveOpen: false))
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc.Append(buffer.AsSpan(0, read));
                deflate.Write(buffer, 0, read);
                total += read;
                copied?.Invoke(read);
            }
        }
        entry.Crc = crc.GetCurrentHashAsUInt32();
        entry.Uncompressed = total;
        entry.Compressed = position - start;
        WriteDescriptor(entry);
    }

    /// <summary>Writes the central directory and the end records; the archive is complete after this.</summary>
    internal void Finish()
    {
        if (finished) throw new InvalidOperationException("The archive is already finished.");
        finished = true;
        var directoryStart = position;
        foreach (var entry in entries)
        {
            var extra = new List<long>(3);
            if (entry.Uncompressed >= Mask32) extra.Add(entry.Uncompressed);
            if (entry.Compressed >= Mask32) extra.Add(entry.Compressed);
            if (entry.Offset >= Mask32) extra.Add(entry.Offset);
            var span = header.AsSpan(0, 46);
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Zip64Version);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], Zip64Version);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], entry.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], entry.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], Clamp(entry.Compressed));
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], Clamp(entry.Uncompressed));
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)entry.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)(extra.Count == 0 ? 0 : 4 + 8 * extra.Count));
            BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[38..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], Clamp(entry.Offset));
            Write(span);
            Write(entry.Name);
            if (extra.Count > 0)
            {
                var block = header.AsSpan(0, 4 + 8 * extra.Count);
                BinaryPrimitives.WriteUInt16LittleEndian(block, 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)(8 * extra.Count));
                for (var index = 0; index < extra.Count; index++) BinaryPrimitives.WriteInt64LittleEndian(block[(4 + 8 * index)..], extra[index]);
                Write(block);
            }
        }
        var directorySize = position - directoryStart;
        var zip64End = position;
        var record = header.AsSpan(0, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteInt64LittleEndian(record[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], Zip64Version);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], Zip64Version);
        BinaryPrimitives.WriteUInt32LittleEndian(record[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(record[24..], entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record[32..], entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record[40..], directorySize);
        BinaryPrimitives.WriteInt64LittleEndian(record[48..], directoryStart);
        Write(record);
        var locator = header.AsSpan(0, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(locator[8..], zip64End);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        Write(locator);
        var end = header.AsSpan(0, 22);
        var count = (ushort)Math.Min(entries.Count, ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], Clamp(directorySize));
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], Clamp(directoryStart));
        BinaryPrimitives.WriteUInt16LittleEndian(end[20..], 0);
        Write(end);
    }

    private Entry Begin(string name, ushort method, ushort flags, DateTime lastWriteTimeUtc)
    {
        if (finished) throw new InvalidOperationException("The archive is already finished.");
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("The entry name is too long.", nameof(name));
        var (time, date) = DosTime(lastWriteTimeUtc);
        var entry = new Entry(bytes, method, (ushort)(flags | Utf8Flag), time, date, position);
        entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// A streamed entry has CRC 0 and sizes 0xFFFFFFFF with a Zip64 extra field of zero sizes, so its data descriptor carries 8-byte sizes.
    /// </summary>
    private void WriteLocalHeader(Entry entry, bool descriptor)
    {
        var span = header.AsSpan(0, 30);
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], descriptor ? Zip64Version : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], entry.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entry.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], entry.DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], entry.DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], descriptor ? 0 : entry.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], descriptor ? Mask32 : (uint)entry.Compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], descriptor ? Mask32 : (uint)entry.Uncompressed);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)entry.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)(descriptor ? 20 : 0));
        Write(span);
        Write(entry.Name);
        if (descriptor)
        {
            var extra = header.AsSpan(0, 20);
            extra.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
            Write(extra);
        }
    }

    private void WriteDescriptor(Entry entry)
    {
        var span = header.AsSpan(0, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x08074b50);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], entry.Crc);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], entry.Compressed);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], entry.Uncompressed);
        Write(span);
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        output.Write(data);
        position += data.Length;
    }

    private static uint Clamp(long value) => value >= Mask32 ? Mask32 : (uint)value;

    /// <summary>MS-DOS date and time in local time, limited to the years 1980–2107 the format can hold.</summary>
    private static (ushort Time, ushort Date) DosTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Year < 1980) local = new DateTime(1980, 1, 1);
        if (local.Year > 2107) local = new DateTime(2107, 12, 31, 23, 59, 58);
        return ((ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2)), (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day));
    }

    private sealed class Entry(byte[] name, ushort method, ushort flags, ushort dosTime, ushort dosDate, long offset)
    {
        internal byte[] Name { get; } = name;
        internal ushort Method { get; } = method;
        internal ushort Flags { get; } = flags;
        internal ushort DosTime { get; } = dosTime;
        internal ushort DosDate { get; } = dosDate;
        internal long Offset { get; } = offset;
        internal uint Crc { get; set; }
        internal long Compressed { get; set; }
        internal long Uncompressed { get; set; }
    }

    /// <summary>The deflate output: writes through the writer so that the position counts the compressed bytes.</summary>
    private sealed class Sink(ZipWriter writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => writer.Write(buffer);
    }
}
