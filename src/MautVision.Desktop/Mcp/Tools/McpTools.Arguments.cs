using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Mcp.Tools;

/// <summary>Helpers every area of the tools shares: arguments, files of imports, the controllers' registries and deletion reviews.</summary>
internal sealed partial class McpTools
{
    // The controllers of the page over the same Core, with their own selection tokens and resource URLs: an AI client never takes the
    // capacity of the page's registries, and a reload of the page does not drop a token between its registration and the import.
    private readonly FileSelections importSelections = new();
    private readonly ResourceHandles importResources = new();

    private static readonly NativeDialogs NoMcpDialogs = new(
        (_, _) => throw new InvalidOperationException("MCP tools take paths, not dialogs."),
        (_, _) => throw new InvalidOperationException("MCP tools take paths, not dialogs."),
        (_, _) => throw new InvalidOperationException("MCP tools take paths, not dialogs."),
        (_, _, _, _) => throw new InvalidOperationException("MCP tools take paths, not dialogs."),
        (_, _) => throw new InvalidOperationException("MCP tools take paths, not dialogs."),
        (_, _) => throw new InvalidOperationException("MCP tools take paths, not dialogs."));

    /// <summary>Prepares the project of the call (in the background, without switching the window) and returns its ID.</summary>
    private async Task<string> PreparedIdAsync(McpToolCall call) => (await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken)).Id;

    /// <summary>The revision the AI passed, or the current one when it passed none (the change then wins over nobody).</summary>
    private static long Revision(McpArguments arguments, long current) => arguments.Has("revision") ? Long(arguments, "revision") : current;

    private static long Long(McpArguments arguments, string name)
    {
        var value = arguments.Element(name) ?? throw McpToolException.InvalidArgument(name, "required");
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
            ? number
            : throw McpToolException.InvalidArgument(name, "must be a non-negative integer");
    }

    private static Guid[] GuidList(McpArguments arguments, string name, int minimum, int maximum)
    {
        var value = arguments.Element(name) ?? throw McpToolException.InvalidArgument(name, "required");
        if (value.ValueKind != JsonValueKind.Array) throw McpToolException.InvalidArgument(name, "must be an array of IDs");
        var items = value.EnumerateArray().ToArray();
        if (items.Length < minimum || items.Length > maximum)
            throw McpToolException.InvalidArgument(name, string.Create(CultureInfo.InvariantCulture, $"must have from {minimum} to {maximum} items"));
        return items.Select((item, index) => item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id)
            ? id
            : throw McpToolException.InvalidArgument($"{name}[{index}]", "must be an ID (GUID)")).ToArray();
    }

    private static string[] StringList(McpArguments arguments, string name, int minimum, int maximum)
    {
        var value = arguments.Element(name) ?? throw McpToolException.InvalidArgument(name, "required");
        if (value.ValueKind != JsonValueKind.Array) throw McpToolException.InvalidArgument(name, "must be an array of strings");
        var items = value.EnumerateArray().ToArray();
        if (items.Length < minimum || items.Length > maximum)
            throw McpToolException.InvalidArgument(name, string.Create(CultureInfo.InvariantCulture, $"must have from {minimum} to {maximum} items"));
        return items.Select((item, index) => item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw McpToolException.InvalidArgument($"{name}[{index}]", "must be a string")).ToArray();
    }

    /// <summary>The operationId of the call: the AI's key of the action, or a new one.</summary>
    private static string OperationId(McpArguments arguments)
        => arguments.OptionalString("operationId") is { Length: > 0 and <= 200 } given ? "mcp:" + given : "mcp:" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Files of an import from path | paths | uploadId | uploadIds (exactly one of the path pair or the upload pair may be combined freely):
    /// paths resolved like a file dialog would (McpPaths), uploads of this server run. With expandFolders a folder contributes its image files
    /// in natural name order; otherwise folders are refused.
    /// </summary>
    private IReadOnlyList<string> ImportFiles(McpArguments arguments, bool expandFolders)
    {
        var given = new List<(string Value, string Argument)>();
        if (arguments.OptionalString("path") is { } single) given.Add((single, "path"));
        if (arguments.Has("paths")) given.AddRange(StringList(arguments, "paths", 1, 10_000).Select((path, index) => (path, $"paths[{index}]")));
        var files = new List<string>();
        var recursive = arguments.Bool("recursive", false);
        foreach (var (value, argument) in given)
        {
            var resolved = dependencies.Paths.Resolve(value, argument);
            if (!resolved.IsDirectory)
            {
                files.Add(resolved.Path);
                continue;
            }
            if (!expandFolders) throw new McpToolException("mcp.path_not_file", "The path is a folder, a file is expected.", new Dictionary<string, string> { ["path"] = resolved.Path });
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
            };
            files.AddRange(Directory.EnumerateFiles(resolved.Path, "*", options)
                .Where(file => ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .OrderBy(file => Path.GetRelativePath(resolved.Path, file), MautVision.Core.Interfaces.Projects.Images.ImageNameOrder.Instance));
        }
        if (arguments.Has("uploadId")) files.Add(dependencies.Uploads.Get(arguments.Guid("uploadId")).Path);
        if (arguments.Has("uploadIds")) files.AddRange(GuidList(arguments, "uploadIds", 1, 1000).Select(id => dependencies.Uploads.Get(id).Path));
        if (given.Count == 0 && !arguments.Has("uploadId") && !arguments.Has("uploadIds"))
            throw McpToolException.InvalidArgument("path", "pass path, paths, uploadId or uploadIds");
        if (files.Count == 0) throw new McpToolException("mcp.no_files", "No image files were found at the given paths.");
        return files;
    }

    /// <summary>Resource URLs are for the page; an AI client looks at images with view.</summary>
    private static JsonObject WithoutUrls(JsonObject node)
    {
        foreach (var item in node.ToArray())
        {
            if (item.Key is "originalUrl" or "thumbnailUrl") node.Remove(item.Key);
            else if (item.Value is JsonObject inner) WithoutUrls(inner);
            else if (item.Value is JsonArray array)
            {
                foreach (var element in array.OfType<JsonObject>()) WithoutUrls(element);
            }
        }
        return node;
    }

    /// <summary>
    /// A review with blockers refuses the deletion like the dialog of the window: the area's deletion_blocked code with the blockers as
    /// "code: count (examples)" in details.blockers.
    /// </summary>
    private static void ThrowIfBlocked(string area, string target, IReadOnlyList<DeletionBlockerResponseDto> blockers)
    {
        if (blockers.Count == 0) return;
        var text = string.Join("; ", blockers.GroupBy(blocker => blocker.Code).Select(group =>
        {
            var examples = group.SelectMany(blocker => blocker.Examples).Distinct().Take(5).ToArray();
            return $"{group.Key}: {group.Sum(blocker => blocker.Count)}" + (examples.Length > 0 ? $" ({string.Join(", ", examples)})" : "");
        }));
        throw new CoreException($"{area}.deletion_blocked", $"Deletion of {target} is blocked: {text}.")
        {
            Details = new Dictionary<string, string> { ["blockers"] = text, ["target"] = target },
        };
    }
}
