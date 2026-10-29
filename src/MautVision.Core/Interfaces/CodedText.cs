using System.Globalization;

namespace MautVision.Core.Interfaces;

/// <summary>
/// A line the user sees, sent as a code and values: the UI chooses its text in the current language and substitutes Args. A code is
/// <c>&lt;namespace&gt;.&lt;key&gt;</c> of the UI catalogs (<c>progress.epoch</c> is the key <c>epoch</c> of the namespace <c>progress</c>).
/// Text is shown as is when there is no code (a name the user typed, a line stored by version 1.0.0) or when the catalogs do not know the
/// code (an English fallback, for example a message of a newer worker).
/// </summary>
public sealed record CodedText(string? Code, IReadOnlyDictionary<string, string>? Args = null, string? Text = null)
{
    /// <summary>A code with values; numbers and other values are written with the invariant culture.</summary>
    public static CodedText Of(string code, params (string Name, object? Value)[] args) => new(code, args.Length == 0
        ? null
        : args.ToDictionary(arg => arg.Name, arg => Convert.ToString(arg.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal));

    /// <summary>A text without a code: shown as is.</summary>
    public static CodedText Plain(string text) => new(null, null, text);
}
