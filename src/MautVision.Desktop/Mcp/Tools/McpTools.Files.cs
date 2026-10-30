using System.IO;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces.Media;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    /// <summary>Image files the import tools accept, the same set as the file dialogs of the window.</summary>
    internal static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private const int MaxPreviewedFiles = 200_000;

    private IEnumerable<McpToolDefinition> FileTools()
    {
        yield return McpToolDefinition.Create("preview_folder", "Preview folder", McpToolClass.Read, true,
            """
            Looks into a folder on this computer without importing anything: how many files match, their total size, counts by extension and
            example names (relative to the folder). By default it counts images (png, jpg, jpeg, webp, bmp); pass extensions to count others
            (for example [".mp4"]). Paths are absolute Windows paths; the user may restrict them to allowed folders (mcp.path_not_allowed).
            """,
            """
            { "type": "object", "properties": {
                "path": { "type": "string", "description": "Absolute folder path, for example D:\\data\\frames." },
                "recursive": { "type": "boolean", "default": false },
                "extensions": { "type": "array", "items": { "type": "string" }, "description": "Extensions with a dot; default: image extensions." },
                "examples": { "type": "integer", "minimum": 0, "maximum": 100, "default": 20 } },
              "required": ["path"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "path": { "type": "string" }, "matchingFiles": { "type": "integer" }, "matchingBytes": { "type": "integer" },
                "otherFiles": { "type": "integer" }, "videoFiles": { "type": "integer" }, "byExtension": { "type": "object" },
                "examples": { "type": "array", "items": { "type": "string" } }, "truncated": { "type": "boolean" } },
              "required": ["path", "matchingFiles", "matchingBytes", "examples", "truncated"] }
            """,
            call => Task.Run(() =>
            {
                call.Arguments.Only("path", "recursive", "extensions", "examples");
                var folder = dependencies.Paths.ResolveDirectory(call.Arguments.String("path"));
                var recursive = call.Arguments.Bool("recursive", false);
                var limit = call.Arguments.Int("examples", 0, 100, 20);
                var extensions = Extensions(call.Arguments);
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
                    MatchType = MatchType.Simple,
                };
                long matching = 0, bytes = 0, other = 0, videos = 0, seen = 0;
                var byExtension = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var examples = new List<string>();
                var truncated = false;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    call.CancellationToken.ThrowIfCancellationRequested();
                    if (++seen > MaxPreviewedFiles)
                    {
                        truncated = true;
                        break;
                    }
                    var extension = file.Extension.ToLowerInvariant();
                    if (VideoFormats.IsVideo(file.Name)) videos++;
                    if (!extensions.Contains(extension))
                    {
                        other++;
                        continue;
                    }
                    matching++;
                    bytes += file.Length;
                    byExtension[extension] = byExtension.GetValueOrDefault(extension) + 1;
                    if (examples.Count < limit) examples.Add(Path.GetRelativePath(folder, file.FullName));
                }
                var structured = new JsonObject
                {
                    ["path"] = folder,
                    ["matchingFiles"] = matching,
                    ["matchingBytes"] = bytes,
                    ["otherFiles"] = other,
                    ["videoFiles"] = videos,
                    ["byExtension"] = McpToolResult.Node(byExtension),
                    ["examples"] = McpToolResult.Node(examples),
                    ["truncated"] = truncated,
                };
                return Result(structured, $"{matching} matching files ({bytes / 1048576.0:0.#} MB) in {folder}" + (recursive ? " and its subfolders" : "")
                    + $"; {other} other files" + (truncated ? $"; stopped after {MaxPreviewedFiles} files." : "."));
            }, call.CancellationToken));

        yield return McpToolDefinition.Create("upload_file", "Upload file", McpToolClass.Write, false,
            """
            Sends a file by content for clients that cannot give a path on this computer (for example an image from the chat). base64 is the
            file content, at most 10 MB per file and 50 MB per session. Returns uploadId, which tools accept instead of a path (view takes
            { uploadId }); the file is removed when the MCP session ends. Prefer paths when the file is on this computer.
            """,
            """
            { "type": "object", "properties": {
                "name": { "type": "string", "description": "File name with extension, for example frame.png." },
                "base64": { "type": "string", "description": "File content in base64 (standard alphabet)." } },
              "required": ["name", "base64"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "uploadId": { "type": "string" }, "name": { "type": "string" }, "bytes": { "type": "integer" },
                "sessionBytes": { "type": "integer" } }, "required": ["uploadId", "name", "bytes"] }
            """,
            async call =>
            {
                call.Arguments.Only("name", "base64");
                var name = call.Arguments.String("name");
                var text = call.Arguments.String("base64");
                // The decoded size is known from the text length: an oversized file is refused before it is decoded.
                if ((long)text.Length / 4 * 3 > McpUploads.MaxFileBytes + 3)
                    throw new McpToolException("mcp.upload_too_large", "The file is larger than 10 MB.", new Dictionary<string, string> { ["limitMb"] = "10" });
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(text);
                }
                catch (FormatException)
                {
                    throw McpToolException.InvalidArgument("base64", "is not valid base64");
                }
                var upload = await dependencies.Uploads.SaveAsync(call.SessionKey, name, content, call.CancellationToken);
                var structured = new JsonObject
                {
                    ["uploadId"] = upload.Id.ToString("D"),
                    ["name"] = upload.Name,
                    ["bytes"] = upload.Bytes,
                    ["sessionBytes"] = dependencies.Uploads.SessionBytes(call.SessionKey),
                };
                return Result(structured, $"Uploaded {upload.Name} ({upload.Bytes} bytes) as {upload.Id:D}.");
            });
    }

    private static HashSet<string> Extensions(McpArguments arguments)
    {
        if (!arguments.Has("extensions")) return new HashSet<string>(ImageExtensions, StringComparer.OrdinalIgnoreCase);
        var value = arguments.Element("extensions")!.Value;
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) throw McpToolException.InvalidArgument("extensions", "must be an array of strings");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 16 } extension)
                throw McpToolException.InvalidArgument("extensions", "must be an array of extensions such as .png");
            result.Add(extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant());
        }
        return result;
    }
}
