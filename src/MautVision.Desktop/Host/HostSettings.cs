using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Host;

/// <summary>
/// Small facts of the host kept between launches in the work directory (host-settings.json): whether the notice about the notification
/// area was shown. A file that cannot be read counts as empty; a failed write is logged.
/// </summary>
internal sealed class HostSettings(string workDirectory, ILogger logger)
{
    internal const string FileName = "host-settings.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.Combine(workDirectory, FileName);
    private Values? values;

    internal bool TrayNoticeShown => Load().TrayNoticeShown;

    internal void MarkTrayNoticeShown()
    {
        values = Load() with { TrayNoticeShown = true };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(values, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Host settings were not saved.");
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

    private Values Load()
    {
        if (values is not null) return values;
        try
        {
            values = File.Exists(path) ? JsonSerializer.Deserialize<Values>(File.ReadAllBytes(path), Json) ?? new Values() : new Values();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(error, "Host settings could not be read.");
            values = new Values();
        }
        return values;
    }

    private sealed record Values
    {
        public bool TrayNoticeShown { get; init; }
    }
}
