using System.Text.RegularExpressions;

namespace MautVision.Core.Projects.Storage;

/// <summary>
/// Maps material kinds and IDs to files under projects/&lt;id&gt;/files/&lt;kind&gt;/. The root is validated once
/// when the project store opens, so building a path does no file system checks.
/// </summary>
internal sealed partial class MaterialFiles
{
    internal const string DirectoryName = "files";

    internal MaterialFiles(string root)
    {
        Root = root;
    }

    internal string Root { get; }

    internal static bool IsValidKind(string? kind) => KindPattern().IsMatch(kind ?? "");

    /// <summary>Directory of one kind, e.g. files/images. It is not created.</summary>
    internal string GetDirectory(string kind)
    {
        if (!IsValidKind(kind))
        {
            throw new ArgumentException("Material kind must be a lowercase identifier.", nameof(kind));
        }
        return Path.Combine(Root, kind!);
    }

    /// <summary>File of one material, e.g. files/images/&lt;guid&gt;.png.</summary>
    internal string GetPath(string kind, Guid id, string extension)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Material ID must not be empty.", nameof(id));
        }
        if (!ExtensionPattern().IsMatch(extension ?? ""))
        {
            throw new ArgumentException("Extension must be a dot and up to 10 lowercase letters or digits.", nameof(extension));
        }
        return Path.Combine(GetDirectory(kind), id.ToString("D") + extension);
    }

    internal string CreateDirectory(string kind)
    {
        var directory = GetDirectory(kind);
        Directory.CreateDirectory(directory);
        return directory;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"\A\.[a-z0-9]{1,10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();
}
