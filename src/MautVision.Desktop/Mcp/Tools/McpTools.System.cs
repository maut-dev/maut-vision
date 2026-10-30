using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using MautVision.Desktop.Controllers.Hardware.RequestDto;
using MautVision.Desktop.Controllers.Settings;
using MautVision.Desktop.Host;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private IEnumerable<McpToolDefinition> SystemTools()
    {
        yield return McpToolDefinition.Create("get_app_info", "Application info", McpToolClass.Read, true,
            """
            Maut Vision version, interface language, work directory, the state of the Python worker (needed for training and model checks), the
            ffmpeg tools (video), the access level the user gave this AI client and the number of projects. Call it first to learn what the
            application can do now.
            """,
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            """
            { "type": "object", "properties": {
                "name": { "type": "string" }, "version": { "type": "string" },
                "language": { "type": "object", "properties": { "setting": { "type": "string" }, "language": { "type": "string" } } },
                "workDirectory": { "type": "string" },
                "pythonWorker": { "type": "object" }, "ffmpegAvailable": { "type": "boolean" },
                "access": { "type": "object" }, "projects": { "type": "object" } },
              "required": ["name", "version", "language", "workDirectory", "pythonWorker", "access", "projects"] }
            """,
            async call =>
            {
                call.Arguments.Only();
                var projects = await Core.Projects.ListAsync(call.CancellationToken);
                var python = Core.Python.GetStatus();
                var settings = dependencies.Settings();
                var version = typeof(McpTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
                var structured = new JsonObject
                {
                    ["name"] = "Maut Vision",
                    ["version"] = version,
                    ["language"] = McpToolResult.Node(SettingsController.Map(dependencies.Language)),
                    ["workDirectory"] = dependencies.WorkDirectory,
                    ["pythonWorker"] = new JsonObject
                    {
                        ["configured"] = python.Configured, ["available"] = python.Available, ["mode"] = python.Mode, ["code"] = python.Code,
                    },
                    ["ffmpegAvailable"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe")),
                    ["access"] = new JsonObject
                    {
                        ["level"] = settings.AccessLevel,
                        ["restrictFolders"] = settings.RestrictFolders,
                        ["allowedFolders"] = new JsonArray(settings.AllowedFolders.Select(folder => (JsonNode?)folder).ToArray()),
                    },
                    ["projects"] = new JsonObject
                    {
                        ["total"] = projects.Count, ["active"] = projects.Count(project => project.ArchivedAt is null),
                    },
                };
                return Result(structured, $"Maut Vision {version}; access level {settings.AccessLevel}; {projects.Count} projects; Python worker "
                    + (python.Available ? "available." : $"unavailable ({python.Code})."));
            });

        yield return McpToolDefinition.Create("get_hardware", "Hardware", McpToolClass.Read, true,
            """
            A snapshot of the computer: CPU model and load, RAM, NVIDIA GPUs (index, memory, load, temperature) and the memory the application
            uses. The GPU index is the device number for training. Values the system does not report are null with a reason code.
            """,
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            """
            { "type": "object", "properties": {
                "timestamp": { "type": "string" }, "cpu": { "type": "object" }, "ram": { "type": ["object", "null"] },
                "application": { "type": "object" }, "gpus": { "type": "array", "items": { "type": "object" } },
                "gpuStatus": { "type": "string" }, "gpuReason": { "type": ["string", "null"] } },
              "required": ["timestamp", "cpu", "gpus", "gpuStatus"] }
            """,
            async call =>
            {
                call.Arguments.Only();
                var sample = await dependencies.Hardware.SampleAsync(new SampleHardwareRequestDto(), call.CancellationToken);
                var gpus = sample.Gpus.Length == 0 ? $"no GPU ({sample.GpuReason})" : string.Join(", ", sample.Gpus.Select(gpu => $"GPU {gpu.Index} {gpu.Name}"));
                return Result(McpToolResult.Object(sample), $"CPU {sample.Cpu.Model}, {gpus}.");
            });

        yield return McpToolDefinition.Create("set_interface_language", "Set interface language", McpToolClass.Write, true,
            """
            Switches the language of the application window for the user: "system" (as in Windows), "ru" or "en". Answers of the tools stay in
            English. Change it only when the user asks.
            """,
            """
            { "type": "object", "properties": {
                "language": { "type": "string", "enum": ["system", "ru", "en"], "description": "The interface language setting." } },
              "required": ["language"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "setting": { "type": "string" }, "language": { "type": "string" } }, "required": ["setting", "language"] }
            """,
            call =>
            {
                call.Arguments.Only("language");
                var value = call.Arguments.String("language");
                if (!HostLanguage.IsSetting(value)) throw McpToolException.InvalidArgument("language", "must be system, ru or en");
                dependencies.Language.Set(value);
                var result = SettingsController.Map(dependencies.Language);
                return Task.FromResult(Result(McpToolResult.Object(result), $"Interface language set to {result.Setting} ({result.Language})."));
            });
    }
}
