using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MautVision.Desktop.Host;

/// <summary>
/// Small facts of the host kept between launches in the work directory (host-settings.json): whether the notice about the notification
/// area was shown, the interface language and the settings of the MCP server (<see cref="Mcp.McpSettings"/>). It is read before the log exists (the loading window needs the language); a file that
/// cannot be read counts as empty; a failed write is logged.
/// </summary>
internal sealed class HostSettings(string workDirectory)
{
    internal const string FileName = "host-settings.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();
    private readonly string path = Path.Combine(workDirectory, FileName);
    private Values? values;

    /// <summary>The log of the application, once it is open.</summary>
    internal ILogger Logger { get; set; } = NullLogger.Instance;

    internal bool TrayNoticeShown => Load().TrayNoticeShown;

    /// <summary>The stored interface language: system, ru or en; null when it was never chosen.</summary>
    internal string? Language => Load().Language;

    internal void MarkTrayNoticeShown() => Save(Load() with { TrayNoticeShown = true });

    internal void SaveLanguage(string language) => Save(Load() with { Language = language });

    /// <summary>The stored MCP settings; null when they were never saved (the server is off).</summary>
    internal Mcp.McpSettings? Mcp => Load().Mcp;

    internal void SaveMcp(Mcp.McpSettings settings) => Save(Load() with { Mcp = settings });

    private void Save(Values next)
    {
        lock (gate)
        {
            values = next;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next, Json));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Host settings were not saved.");
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // A leftover temporary file is never read.
                }
            }
        }
    }

    private Values Load()
    {
        lock (gate)
        {
            if (values is not null) return values;
            try
            {
                values = File.Exists(path) ? JsonSerializer.Deserialize<Values>(File.ReadAllBytes(path), Json) ?? new Values() : new Values();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Host settings could not be read.");
                values = new Values();
            }
            return values;
        }
    }

    private sealed record Values
    {
        public bool TrayNoticeShown { get; init; }

        public string? Language { get; init; }

        public Mcp.McpSettings? Mcp { get; init; }
    }
}
