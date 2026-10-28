using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>
/// Similarity signature of a background: the frame (before blur) reduced to 32×32 greyscale by averaging the pixels of each
/// cell (Rec. 709 luma, alpha ignored). Two frames are similar when the mean absolute difference of their signatures is at
/// most 3 of 255: this drops a still camera and video compression noise, but not a moved scene.
/// </summary>
internal static class BackgroundSignature
{
    internal const int Side = 32;
    internal const int Length = Side * Side;

    /// <summary>Sum of absolute differences at a mean difference of 3/255.</summary>
    internal const int SimilarLimit = 3 * Length;

    internal static byte[] Compute(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var signature = new byte[Length];
        Span<long> sums = stackalloc long[Side];
        Span<int> cellOfColumn = width <= 4096 ? stackalloc int[width] : new int[width];
        Span<int> columnsInCell = stackalloc int[Side];
        for (var x = 0; x < width; x++)
        {
            var cell = (int)((long)x * Side / width);
            cellOfColumn[x] = cell;
            columnsInCell[cell]++;
        }
        var pixels = MemoryMarshal.Cast<byte, uint>(rgba[..(width * height * 4)]);
        for (var cellY = 0; cellY < Side; cellY++)
        {
            var top = (int)((long)cellY * height / Side);
            var bottom = Math.Max(top + 1, (int)((long)(cellY + 1) * height / Side));
            sums.Clear();
            for (var y = top; y < bottom && y < height; y++)
            {
                var row = pixels.Slice(y * width, width);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    // Little-endian RGBA: R is the low byte.
                    sums[cellOfColumn[x]] += 54 * (pixel & 0xFF) + 183 * ((pixel >> 8) & 0xFF) + 19 * ((pixel >> 16) & 0xFF);
                }
            }
            var rows = Math.Min(bottom, height) - top;
            for (var cellX = 0; cellX < Side; cellX++)
            {
                var count = (long)rows * columnsInCell[cellX] * 256;
                signature[cellY * Side + cellX] = count == 0 ? (byte)0 : (byte)((sums[cellX] + count / 2) / count);
            }
        }
        return signature;
    }
}

/// <summary>
/// Sum of absolute differences between signatures, the hot loop of the similarity check: every new frame is compared with
/// every background of the set. AVX2 compares 32 bytes per instruction (VPSADBW), SSE2 16; the scalar loop is the fallback
/// and the reference the vector paths are checked against.
/// </summary>
internal static class SignatureDistance
{
    /// <summary>Measurement seam: false forces the scalar loop.</summary>
    internal static bool UseVectors { get; set; } = true;

    internal static int Sum(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Signatures differ in length.", nameof(right));
        if (UseVectors && Avx2.IsSupported && left.Length >= Vector256<byte>.Count) return Avx2Sum(left, right);
        if (UseVectors && Sse2.IsSupported && left.Length >= Vector128<byte>.Count) return Sse2Sum(left, right);
        return ScalarSum(left, right);
    }

    internal static int ScalarSum(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var total = 0;
        for (var index = 0; index < left.Length; index++) total += Math.Abs(left[index] - right[index]);
        return total;
    }

    /// <summary>
    /// Whether the sum stays within limit. Blocks of <see cref="Block"/> bytes are summed in turn and the loop stops once the
    /// limit is passed: a clearly different frame is rejected after a quarter of its signature or less.
    /// </summary>
    internal static bool IsWithin(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        if (left.Length != right.Length) throw new ArgumentException("Signatures differ in length.", nameof(right));
        var total = 0;
        for (var offset = 0; offset < left.Length; offset += Block)
        {
            var length = Math.Min(Block, left.Length - offset);
            total += Sum(left.Slice(offset, length), right.Slice(offset, length));
            if (total > limit) return false;
        }
        return true;
    }

    private const int Block = 256;

    /// <summary>Index of the first signature in packed (consecutive signatures of probe's length) within limit; −1 if none.</summary>
    internal static int FindWithin(ReadOnlySpan<byte> packed, ReadOnlySpan<byte> probe, int limit)
    {
        var length = probe.Length;
        for (int offset = 0, index = 0; offset + length <= packed.Length; offset += length, index++)
        {
            if (IsWithin(packed.Slice(offset, length), probe, limit)) return index;
        }
        return -1;
    }

    private static int Avx2Sum(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        ref var a = ref MemoryMarshal.GetReference(left);
        ref var b = ref MemoryMarshal.GetReference(right);
        var accumulator = Vector256<ulong>.Zero;
        var index = 0;
        for (; index <= left.Length - Vector256<byte>.Count; index += Vector256<byte>.Count)
        {
            var x = Vector256.LoadUnsafe(ref a, (nuint)index);
            var y = Vector256.LoadUnsafe(ref b, (nuint)index);
            accumulator = Avx2.Add(accumulator, Avx2.SumAbsoluteDifferences(x, y).AsUInt64());
        }
        var total = (int)Vector256.Sum(accumulator);
        return total + ScalarSum(left[index..], right[index..]);
    }

    private static int Sse2Sum(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        ref var a = ref MemoryMarshal.GetReference(left);
        ref var b = ref MemoryMarshal.GetReference(right);
        var accumulator = Vector128<ulong>.Zero;
        var index = 0;
        for (; index <= left.Length - Vector128<byte>.Count; index += Vector128<byte>.Count)
        {
            var x = Vector128.LoadUnsafe(ref a, (nuint)index);
            var y = Vector128.LoadUnsafe(ref b, (nuint)index);
            accumulator = Sse2.Add(accumulator, Sse2.SumAbsoluteDifferences(x, y).AsUInt64());
        }
        var total = (int)Vector128.Sum(accumulator);
        return total + ScalarSum(left[index..], right[index..]);
    }
}

/// <summary>
/// Signatures a run compares against: the backgrounds of the set (also removed ones) loaded once, then every frame the run
/// adds. Kept contiguous so a check is one linear pass of the vector kernel.
/// </summary>
internal sealed class SimilarityIndex
{
    private byte[] packed;
    private int count;

    internal SimilarityIndex(int capacity = 64)
    {
        packed = new byte[Math.Max(1, capacity) * BackgroundSignature.Length];
    }

    internal int Count => count;

    internal void Add(ReadOnlySpan<byte> signature)
    {
        if (signature.Length != BackgroundSignature.Length) throw new ArgumentException("Unexpected signature length.", nameof(signature));
        if ((count + 1) * BackgroundSignature.Length > packed.Length) Array.Resize(ref packed, packed.Length * 2);
        signature.CopyTo(packed.AsSpan(count * BackgroundSignature.Length));
        count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsSimilar(ReadOnlySpan<byte> signature)
        => SignatureDistance.FindWithin(packed.AsSpan(0, count * BackgroundSignature.Length), signature, BackgroundSignature.SimilarLimit) >= 0;
}
