using System.Globalization;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Desktop.Controllers.BaseModels;
using MautVision.Desktop.Controllers.BaseModels.RequestDto;
using MautVision.Desktop.Controllers.BaseModels.ResponseDto;
using Microsoft.Extensions.Logging.Abstractions;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string BaseModelSchema = """
        { "type": "object", "description": "A catalog base model: id (for example yolo26n), family, variant (n, s, m, l, x), byteSize of the weights, stride (project input sides are multiples of it), status (not_installed, installing, installed, failed), progress 0-100 while installing, error {code} when failed.",
          "properties": { "id": { "type": "string" }, "family": { "type": "string" }, "variant": { "type": "string" }, "byteSize": { "type": "integer" },
            "stride": { "type": "integer" }, "status": { "type": "string", "enum": ["not_installed", "installing", "installed", "failed"] },
            "progress": { "type": "number" }, "error": { "type": ["object", "null"] } },
          "required": ["id", "family", "variant", "status"] }
        """;

    private BaseModelsController? baseModelsController;

    /// <summary>The controller of the page's base model screen over the same Core; the installation belongs to the model, not to the call.</summary>
    private BaseModelsController BaseModels => baseModelsController ??= new BaseModelsController(Core.BaseModels, NullLogger<BaseModelsController>.Instance);

    private IEnumerable<McpToolDefinition> BaseModelTools()
    {
        yield return McpToolDefinition.Create("list_base_models", "List base models", McpToolClass.Read, true,
            """
            The YOLO base models of the catalog and whether their weights are installed on this computer. A project needs an installed base
            model (create_project and update_project take its id as baseModelId); training starts from its weights.
            """,
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            $$"""{ "type": "object", "properties": { "models": { "type": "array", "items": {{BaseModelSchema}} } }, "required": ["models"] }""",
            async call =>
            {
                call.Arguments.Only();
                var models = await BaseModels.ListAsync(new ListBaseModelsRequestDto(), call.CancellationToken);
                return Result(new JsonObject { ["models"] = McpToolResult.Node(models) },
                    $"{models.Length} base models: " + string.Join("; ", models.Select(model => $"{model.Id} {model.Status}")) + ".");
            });

        yield return McpToolDefinition.Create("install_base_model", "Install base model", McpToolClass.Write, true,
            """
            Downloads and verifies the weights of a catalog base model (from the Ultralytics releases on GitHub; size in byteSize). The
            installation is not a project job: it belongs to the model and continues after this call. The call returns the model state at once,
            or waits up to waitSeconds (at most 120) while sending notifications/progress (0-100) when the client passed a progressToken. Call
            it again to keep waiting: a repeated call joins the running installation, and an installed model is not downloaded again. On a
            failure status is failed with error.code (for example base_models.download_failed).
            """,
            """
            { "type": "object", "properties": {
                "modelId": { "type": "string", "description": "Catalog model ID from list_base_models." },
                "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0, "description": "Wait for the end of the installation up to this many seconds." } },
              "required": ["modelId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "model": {{BaseModelSchema}}, "finished": { "type": "boolean" }, "timedOut": { "type": "boolean" } },
              "required": ["model", "finished", "timedOut"] }
            """,
            async call =>
            {
                call.Arguments.Only("modelId", "waitSeconds");
                var modelId = call.Arguments.String("modelId");
                var waitSeconds = call.Arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var model = await BaseModels.InstallAsync(new InstallBaseModelRequestDto(modelId), call.CancellationToken);
                var timedOut = false;
                if (waitSeconds > 0) (model, timedOut) = await WaitInstallationAsync(modelId, TimeSpan.FromSeconds(waitSeconds), call);
                var finished = model.Status != "installing";
                var structured = new JsonObject { ["model"] = McpToolResult.Node(model), ["finished"] = finished, ["timedOut"] = timedOut };
                return Result(structured, model.Status switch
                {
                    "installed" => $"Base model {model.Id} is installed.",
                    "failed" => $"Base model {model.Id} failed to install: {model.Error?.Code}.",
                    _ => string.Create(CultureInfo.InvariantCulture,
                        $"Base model {model.Id} is installing ({model.Progress:0}%); call install_base_model again with waitSeconds to follow it."),
                });
            });

        yield return McpToolDefinition.Create("delete_base_model", "Delete base model weights", McpToolClass.Destructive, true,
            """
            Removes the installed weights of a base model from this computer; the catalog entry stays and install_base_model can download it
            again. Projects and trained model versions are not touched. Refused while the model is installing (base_models.installation_active).
            """,
            """{ "type": "object", "properties": { "modelId": { "type": "string" } }, "required": ["modelId"], "additionalProperties": false }""",
            $$"""{ "type": "object", "properties": { "model": {{BaseModelSchema}} }, "required": ["model"] }""",
            async call =>
            {
                call.Arguments.Only("modelId");
                var model = await BaseModels.DeleteAsync(new DeleteBaseModelRequestDto(call.Arguments.String("modelId")), call.CancellationToken);
                return Result(Wrap("model", model), $"The weights of base model {model.Id} are removed.");
            });
    }

    /// <summary>Waits until the model leaves Installing or the timeout passes; growing progress goes to the client.</summary>
    private async Task<(BaseModelResponseDto Model, bool TimedOut)> WaitInstallationAsync(string modelId, TimeSpan timeout, McpToolCall call)
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<IBaseModel>(new() { SingleReader = true });
        void OnChanged(object? sender, BaseModelChangedEventArgs args)
        {
            if (args.Model.Id == modelId) changes.Writer.TryWrite(args.Model);
        }
        var reported = -1.0;
        async Task ReportAsync(IBaseModel model)
        {
            var value = model.Status == BaseModelStatus.Installing ? model.Progress : 100;
            if (!call.WantsProgress || value <= reported) return;
            reported = value;
            try
            {
                await call.ReportProgressAsync(value, 100, model.Status == BaseModelStatus.Installing ? "installing" : BaseModelResponseMapper.Map(model).Status);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A client that went away does not stop the wait.
            }
        }
        Core.BaseModels.ModelChanged += OnChanged;
        try
        {
            var model = await Core.BaseModels.GetOrNullAsync(modelId, call.CancellationToken)
                ?? throw new CoreException("base_models.not_found", $"Base model {modelId} is not in the catalogue.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(call.CancellationToken);
            deadline.CancelAfter(timeout);
            while (model.Status == BaseModelStatus.Installing)
            {
                await ReportAsync(model);
                try
                {
                    await changes.Reader.ReadAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!call.CancellationToken.IsCancellationRequested)
                {
                    return (BaseModelResponseMapper.Map(model), model.Status == BaseModelStatus.Installing);
                }
            }
            await ReportAsync(model);
            return (BaseModelResponseMapper.Map(model), false);
        }
        finally
        {
            Core.BaseModels.ModelChanged -= OnChanged;
        }
    }
}
