using MautVision.Core.Interfaces;

namespace MautVision.Desktop.Controllers;

/// <summary>
/// A line the page translates (<see cref="CodedText"/>): Code — <c>&lt;namespace&gt;.&lt;key&gt;</c> of the UI catalogs, Args — values for its
/// placeholders, Text — shown as is when there is no code or the catalogs do not know it. Null fields are sent as null.
/// </summary>
public sealed record CodedTextDto(string? Code, IReadOnlyDictionary<string, string>? Args, string? Text)
{
    internal static CodedTextDto Map(CodedText text) => new(text.Code, text.Args, text.Text);

    internal static CodedTextDto? MapOrNull(CodedText? text) => text is null ? null : Map(text);
}
