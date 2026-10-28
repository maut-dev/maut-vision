using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MautVision.Core.Jobs;

/// <summary>Position and running totals of an import; RejectedFiles is capped like the result.</summary>
internal sealed record ImportCheckpoint(int Next, int Added, int Duplicates, int Rejected, IReadOnlyList<ImportRejectedFile> RejectedFiles);

/// <summary>
/// Imports source images of a class variant in batches. Within a batch a few workers handle one file each: its bytes are
/// read once, hashed with SHA-256 (hardware accelerated by the platform hash) and fully decoded with SkiaSharp, so a
/// truncated or corrupt file is rejected; a valid one is written unchanged next to its final place. Then, in order,
/// duplicates are skipped and files are renamed into place; the rows of the batch are inserted in one transaction and the
/// checkpoint is saved.
/// An image ID is derived from the job ID and the file index, so a run repeated after a crash rewrites the same file and
/// finds its own row instead of producing a second copy or counting the file as a duplicate.
/// </summary>
internal sealed class ImportClassAssetsHandler(IProjectsManager projects) : IJobHandler<ImportClassAssetsInput>
{
    internal const int BatchSize = 64;
    internal const string InvalidReason = "classes.asset_invalid";
    internal const string UnreadableReason = "classes.asset_unreadable";
    private const int CopyBufferSize = 1 << 20;
    // Files up to this size are read once into memory; camera photos are well below it.
    private const int InMemoryLimit = 32 << 20;
    // Rows decoded per scanline call: large enough to amortize the call, small enough to stay in cache.
    private const int ScanlineBufferSize = 1 << 18;
    private const int MaxNameLength = 120;

    public JobResource Resource => JobResource.Cpu;

    public bool Resumable => true;

    /// <summary>Concurrent file workers inside the one CPU slot the job holds: copying is I/O bound, decoding uses the CPU.</summary>
    internal int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Smoke seam: awaited at "inserted" (rows committed, checkpoint not saved) and "saved" points of each batch.</summary>
    internal Func<string, int, Task>? Pause { get; set; }

    public async Task ExecuteAsync(ImportClassAssetsInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input?.Files);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var classes = (ClassesManager)project.Classes;
        var binding = input.ClassVariantId;
        await classes.RequireImportTargetAsync(binding, token).ConfigureAwait(false);
        var files = await classes.GetFilesAsync(token).ConfigureAwait(false);
        var directory = files.CreateDirectory(CatalogDeletion.AssetKind);
        // The kind directory is created after the store was opened, so it is checked here before files are written into it.
        if (ProjectStore.ValidateEntry(directory) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("Class asset directory must be a directory.");

        var checkpoint = context.Checkpoint<ImportCheckpoint>() ?? new ImportCheckpoint(0, 0, 0, 0, []);
        var total = input.Files.Count;
        var added = checkpoint.Added;
        var duplicates = checkpoint.Duplicates;
        var rejected = checkpoint.Rejected;
        var rejectedFiles = checkpoint.RejectedFiles.ToList();
        ImportClassAssetsResult Summary() => new(total, added, duplicates, rejected, rejectedFiles.ToArray());
        context.SetResult(Summary());
        context.ReportProgress(total == 0 ? 100 : 100.0 * checkpoint.Next / total, null, checkpoint.Next, total);

        var known = await classes.ReadHashesAsync(binding, token).ConfigureAwait(false);
        var processed = checkpoint.Next;
        // Workers finish in any order; the counter and its report change together so that progress never goes back.
        var progressGate = new Lock();
        for (var start = checkpoint.Next; start < total; start += BatchSize)
        {
            var count = Math.Min(BatchSize, total - start);
            var prepared = new Prepared[count];
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token },
                    async (offset, cancellation) =>
                    {
                        var index = start + offset;
                        prepared[offset] = await PrepareAsync(input.Files[index], AssetId(context.JobId, index), directory, cancellation).ConfigureAwait(false);
                        lock (progressGate)
                        {
                            processed++;
                            context.ReportProgress(100.0 * processed / total, null, processed, total);
                        }
                    }).ConfigureAwait(false);
            }
            catch
            {
                foreach (var item in prepared) item?.DiscardTemporary();
                throw;
            }

            // From here the batch is finished without observing cancellation: it is short and leaves no half-written state.
            var batch = new List<(ImportedAsset Asset, string Path)>();
            foreach (var item in prepared)
            {
                if (item.Reason is { } reason)
                {
                    rejected++;
                    if (rejectedFiles.Count < ImportClassAssetsResult.MaxRejectedFiles) rejectedFiles.Add(new ImportRejectedFile(item.Name, reason));
                    continue;
                }
                var asset = item.Asset!;
                if (known.TryGetValue(asset.Sha256, out var owner) && owner != asset.Id)
                {
                    duplicates++;
                    item.DiscardTemporary();
                    continue;
                }
                var path = files.GetPath(CatalogDeletion.AssetKind, asset.Id, CatalogSql.Extension(asset.Format));
                File.Move(item.Temporary!, path, overwrite: true);
                known[asset.Sha256] = asset.Id;
                batch.Add((asset, path));
            }
            ImportBatchOutcome outcome;
            try
            {
                outcome = await classes.InsertImportedAsync(binding, batch.Select(item => item.Asset).ToArray(), context.OperationId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The rows were rolled back (for example, the binding was archived meanwhile), so their files must go too.
                foreach (var item in batch) TryDelete(item.Path);
                throw;
            }
            added += outcome.Added.Count;
            duplicates += outcome.Duplicates.Count;
            foreach (var duplicate in outcome.Duplicates)
            {
                // Another import into the same binding committed this content first.
                var item = batch.First(entry => entry.Asset.Id == duplicate);
                TryDelete(item.Path);
                known.Remove(item.Asset.Sha256);
            }
            var batchIndex = start / BatchSize;
            if (Pause is { } pause) await pause("inserted", batchIndex).ConfigureAwait(false);
            await context.SaveCheckpointAsync(new ImportCheckpoint(start + count, added, duplicates, rejected, rejectedFiles.ToArray())).ConfigureAwait(false);
            context.SetResult(Summary());
            if (Pause is { } paused) await paused("saved", batchIndex).ConfigureAwait(false);
        }
        context.Logger.LogInformation("Job {JobId} imported {Added} class assets into {ClassVariantId}: {Duplicates} duplicates, {Rejected} rejected of {Total}.",
            context.JobId, added, binding, duplicates, rejected, total);
    }

    /// <summary>
    /// Reads one source file once: the bytes are hashed and fully decoded, and only a valid image is written next to its
    /// final place. A file above <see cref="InMemoryLimit"/> is streamed to disk and decoded from the copy instead.
    /// Problems with the file reject it; they never fail the job.
    /// </summary>
    private static async Task<Prepared> PrepareAsync(string source, Guid id, string directory, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(source);
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var temporary = Path.Combine(directory, "." + id.ToString("D") + ".import");
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = input.Length;
            if (length == 0) return Prepared.Rejected(name, InvalidReason);
            var (sha, probe) = length <= InMemoryLimit
                ? await ReadInMemoryAsync(input, (int)length, temporary, cancellationToken).ConfigureAwait(false)
                : await StreamAsync(input, length, temporary, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                TryDelete(temporary);
                return Prepared.Rejected(name, InvalidReason);
            }
            var asset = new ImportedAsset(id, probe.Value.Format, probe.Value.Width, probe.Value.Height, probe.Value.HasAlpha, length, sha);
            return new Prepared(name, null, asset, temporary);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            TryDelete(temporary);
            return Prepared.Rejected(name, UnreadableReason);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>The usual path: one read into a pooled buffer; the decoded and hashed bytes are exactly the bytes written.</summary>
    private static async Task<(string Sha, Probe? Probe)> ReadInMemoryAsync(FileStream input, int length, string temporary, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var filled = 0;
            while (filled < length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(filled, length - filled), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new IOException("Source file changed while it was read.");
                filled += read;
            }
            if (input.Length != length) throw new IOException("Source file changed while it was read.");
            var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, length)));
            Probe? probe;
            using (var memory = new MemoryStream(buffer, 0, length, writable: false))
            {
                probe = Inspect(memory);
            }
            if (probe is null) return (sha, null);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return (sha, probe);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A very large file: copied with the hash computed on the way, then the stored copy is decoded.</summary>
    private static async Task<(string Sha, Probe? Probe)> StreamAsync(FileStream input, long length, string temporary, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            output.SetLength(length);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (output.Position != length) throw new IOException("Source file changed while it was copied.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        using var stored = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), Inspect(stored));
    }

    /// <summary>
    /// Format, size with EXIF orientation applied and whether the file declares an alpha channel — or null when the file
    /// is not an accepted image or any of its pixels cannot be decoded.
    /// </summary>
    internal static Probe? Inspect(Stream stream)
    {
        using var codec = SKCodec.Create(stream);
        if (codec is null) return null;
        ClassAssetFormat? format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => ClassAssetFormat.Png,
            SKEncodedImageFormat.Jpeg => ClassAssetFormat.Jpeg,
            SKEncodedImageFormat.Webp => ClassAssetFormat.Webp,
            SKEncodedImageFormat.Bmp => ClassAssetFormat.Bmp,
            _ => null,
        };
        var info = codec.Info;
        if (format is null || info.Width <= 0 || info.Height <= 0 || !DecodesCompletely(codec)) return null;
        var transposed = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        return new Probe(format.Value, transposed ? info.Height : info.Width, transposed ? info.Width : info.Height, info.AlphaType != SKAlphaType.Opaque);
    }

    /// <summary>
    /// Decodes every pixel at full size in the codec's own colour type (no conversion). Codecs with scanline decoding
    /// (JPEG, non-interlaced PNG, BMP) go through a pooled buffer of a few rows; the others (WebP, interlaced PNG) into a
    /// pooled buffer of the whole image. Anything but a complete success — truncated or corrupt data — fails.
    /// </summary>
    private static bool DecodesCompletely(SKCodec codec)
    {
        var info = codec.Info;
        if (info.ColorType == SKColorType.Unknown) info = info.WithColorType(SKColorType.Bgra8888);
        var rowBytes = info.RowBytes;
        if (codec.StartScanlineDecode(info) == SKCodecResult.Success)
        {
            var rows = Math.Max(1, Math.Min(info.Height, ScanlineBufferSize / rowBytes));
            return WithPinned(rows * rowBytes, address =>
            {
                for (var line = 0; line < info.Height; line += rows)
                {
                    var count = Math.Min(rows, info.Height - line);
                    if (codec.GetScanlines(address, count, rowBytes) != count) return false;
                }
                return true;
            });
        }
        if (info.BytesSize64 > Array.MaxLength) return false;
        return WithPinned((int)info.BytesSize64, address => codec.GetPixels(info, address) == SKCodecResult.Success);
    }

    private static bool WithPinned(int size, Func<IntPtr, bool> decode)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return decode(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Stable per job and file index, so a repeated batch targets the same file and row.</summary>
    internal static Guid AssetId(Guid jobId, int index) => JobItemIds.Derive(jobId, index);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover import file is never served: resources resolve only <id>.<ext> of existing rows.
        }
    }

    /// <summary>What the file says about itself: format, size with EXIF orientation applied, and whether it declares an alpha channel.</summary>
    internal readonly record struct Probe(ClassAssetFormat Format, int Width, int Height, bool HasAlpha);

    private sealed record Prepared(string Name, string? Reason, ImportedAsset? Asset, string? Temporary)
    {
        internal static Prepared Rejected(string name, string reason) => new(name, reason, null, null);

        internal void DiscardTemporary()
        {
            if (Temporary is not null) TryDelete(Temporary);
        }
    }
}
