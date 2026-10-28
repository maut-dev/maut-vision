using System.Text.RegularExpressions;

namespace MautVision.Core.Interfaces.Projects.Catalog;

/// <summary>
/// Box colors of class variants. Without a saved color a binding gets a palette color by FNV-1a of its ID,
/// so the annotation is multicolored right away and the color does not change between runs.
/// </summary>
public static partial class VariantColors
{
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#ef4444", "#f97316", "#fcd34d", "#a3e635", "#10b981", "#5eead4",
        "#22d3ee", "#3b82f6", "#a78bfa", "#d946ef", "#f472b6", "#f5f5f4",
    ];

    public static string Default(Guid classVariantId)
    {
        // FNV-1a 32 over UTF-16 code units of the canonical ID, as the UI computed it before.
        var hash = 0x811c9dc5u;
        foreach (var character in classVariantId.ToString("D"))
        {
            hash ^= character;
            hash *= 0x01000193u;
        }
        return Palette[(int)(hash % (uint)Palette.Count)];
    }

    /// <summary>The <c>#rrggbb</c> value in lowercase, or null if the format is invalid.</summary>
    public static string? Normalize(string? value)
        => value is not null && HexPattern().IsMatch(value) ? value.ToLowerInvariant() : null;

    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();
}
