namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>
/// Name order on import: case-insensitive, numbers inside a name are compared as numbers (frame2 before frame10).
/// This is how import orders the files of a folder; Desktop passes the files chosen in the dialog in the same order.
/// </summary>
public sealed class ImageNameOrder : IComparer<string>
{
    public static readonly ImageNameOrder Instance = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                var startI = i;
                var startJ = j;
                while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                while (j < right.Length && char.IsAsciiDigit(right[j])) j++;
                var a = left.AsSpan(startI, i - startI).TrimStart('0');
                var b = right.AsSpan(startJ, j - startJ).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var digits = a.SequenceCompareTo(b);
                if (digits != 0) return digits;
                continue;
            }
            var compared = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
            if (compared != 0) return compared;
            i++;
            j++;
        }
        var length = (left.Length - i).CompareTo(right.Length - j);
        return length != 0 ? length : string.CompareOrdinal(left, right);
    }
}
