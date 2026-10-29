using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Desktop.Controllers.Projects.Checks.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    /// <summary>Quality metrics of the Analytics table, higher is better (lib/checks/check-view.ts qualityMetrics).</summary>
    private static readonly (string Key, string Label)[] QualityMetrics = [("mAP50", "mAP50"), ("mAP50_95", "mAP50-95"), ("precision", "Precision"), ("recall", "Recall")];

    private IEnumerable<McpToolDefinition> AnalyticsTools()
    {
        yield return McpToolDefinition.Create("compare_model_versions", "Compare model versions", McpToolClass.Read, true,
            """
            The "Metrics and parameters" table of the Analytics section for the given model versions: quality metrics of best.pt (mAP50,
            mAP50-95, precision, recall) with the best version of each row (only when at least two values differ), best epoch, epochs
            completed / requested, dataset (marked archived or deleted), train / val frames, date, batch (requested and effective), lr0 (tuned when
            the worker computed it), AMP and device. With datasetVersionId (and split, default test) rows of the latest successful evaluation of
            each version on that dataset are added; run evaluate_model for versions without one.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "versionIds": { "type": "array", "minItems": 1, "maxItems": 20, "items": { "type": "string" } },
                "datasetVersionId": { "type": "string" },
                "split": { "type": "string", "enum": ["val", "test"], "default": "test" } },
              "required": ["projectId", "versionIds"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "versions": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "string" }, "number": { "type": "integer" }, "isWorking": { "type": "boolean" } } } },
                "rows": { "type": "array", "items": { "type": "object", "properties": {
                  "key": { "type": "string" }, "label": { "type": "string" }, "values": { "type": "array" },
                  "best": { "type": ["string", "null"], "description": "versionId with the best value" } } } } },
              "required": ["versions", "rows"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionIds", "datasetVersionId", "split");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var ids = GuidList(arguments, "versionIds", 1, 20).Distinct().ToArray();
                var split = arguments.OptionalString("split") ?? "test";
                if (split is not ("val" or "test")) throw McpToolException.InvalidArgument("split", "must be val or test");
                var versions = new List<ModelVersionResponseDto>();
                foreach (var id in ids) versions.Add(await TrainingApi.GetVersionAsync(new GetModelVersionRequestDto(project.Id, id), call.CancellationToken));
                var datasets = (await project.Datasets.ListAsync(true, call.CancellationToken)).ToDictionary(item => item.Id);
                string DatasetName(ModelVersionResponseDto version) => datasets.TryGetValue(version.Dataset.VersionId, out var dataset)
                    ? dataset.ArchivedAt is null ? dataset.Name : dataset.Name + " (archived)"
                    : version.Dataset.Name + " (deleted)";

                var rows = new JsonArray();
                foreach (var (key, label) in QualityMetrics) rows.Add(QualityRow(key, label, versions, version => version.Metrics.TryGetValue(key, out var value) ? value : null));
                rows.Add(Row("bestEpoch", "Best epoch", versions, version => version.BestEpoch));
                rows.Add(Row("epochs", "Epochs", versions, version => $"{version.EpochsCompleted} / {Text(version.Parameters.GetValueOrDefault("epochs"))}"));
                rows.Add(Row("dataset", "Dataset", versions, DatasetName));
                rows.Add(Row("split", "Train / val frames", versions, version => $"{version.Dataset.Counts.Train} / {version.Dataset.Counts.Val}"));
                rows.Add(Row("date", "Date", versions, version => version.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
                rows.Add(Row("batch", "Batch", versions, version =>
                {
                    var requested = Text(version.Parameters.GetValueOrDefault("batch"));
                    var effective = Number(version.EffectiveParams, "effectiveBatch");
                    return effective is { } value && value.ToString(CultureInfo.InvariantCulture) != requested
                        ? $"{requested} ({value.ToString(CultureInfo.InvariantCulture)})"
                        : requested;
                }));
                rows.Add(Row("lr0", "lr0", versions, version =>
                {
                    var value = Number(version.EffectiveParams, "lr0") ?? (version.Parameters.TryGetValue("lr0", out var lr0) && lr0.ValueKind == JsonValueKind.Number ? lr0.GetDouble() : null);
                    if (value is null) return "—";
                    var source = version.EffectiveParams.ValueKind == JsonValueKind.Object && version.EffectiveParams.TryGetProperty("lr0Source", out var item) ? item.GetString() : null;
                    var text = value.Value.ToString("0.######", CultureInfo.InvariantCulture);
                    return source is "computed" or "optimizer-auto" ? text + " (tuned)" : text;
                }));
                rows.Add(Row("amp", "AMP", versions, version =>
                {
                    var value = version.EffectiveParams.ValueKind == JsonValueKind.Object && version.EffectiveParams.TryGetProperty("amp", out var effective)
                        ? effective
                        : version.Parameters.GetValueOrDefault("amp");
                    return value.ValueKind switch { JsonValueKind.True => "on", JsonValueKind.False => "off", _ => "—" };
                }));
                rows.Add(Row("device", "Device", versions, version => Text(version.Parameters.GetValueOrDefault("device")) is var device && device == "cpu" ? "CPU" : "GPU " + device));

                if (arguments.OptionalGuid("datasetVersionId") is { } datasetVersionId)
                {
                    var latest = await ChecksApi.ListLatestEvaluationsAsync(new ListLatestModelEvaluationsRequestDto(project.Id, datasetVersionId, split),
                        call.CancellationToken);
                    var byVersion = latest.GroupBy(item => item.VersionId).ToDictionary(group => group.Key, group => group.First());
                    var name = datasets.TryGetValue(datasetVersionId, out var dataset) ? dataset.Name : datasetVersionId.ToString("D");
                    foreach (var (key, label) in QualityMetrics)
                    {
                        rows.Add(QualityRow($"evaluation.{key}", $"{label} on {name} · {split}", versions,
                            version => byVersion.TryGetValue(version.Id, out var evaluation) && evaluation.Metrics.TryGetValue(key, out var value) ? value : null));
                    }
                }
                var structured = new JsonObject
                {
                    ["versions"] = new JsonArray(versions.Select(version => (JsonNode?)new JsonObject
                    {
                        ["id"] = version.Id.ToString("D"), ["number"] = version.Number, ["isWorking"] = version.IsWorking, ["runId"] = version.RunId.ToString("D"),
                    }).ToArray()),
                    ["rows"] = rows,
                };
                var bestMap = rows[0]!["best"]?.GetValue<string>();
                var bestNumber = versions.FirstOrDefault(version => version.Id.ToString("D") == bestMap)?.Number;
                return Result(structured, $"{versions.Count} versions compared" + (bestNumber is { } number ? $"; the best mAP50 is version {number}." : "."));
            });
    }

    private static JsonObject Row<T>(string key, string label, IReadOnlyList<ModelVersionResponseDto> versions, Func<ModelVersionResponseDto, T> value) => new()
    {
        ["key"] = key,
        ["label"] = label,
        ["values"] = new JsonArray(versions.Select(version => McpToolResult.Node(value(version))).ToArray()),
        ["best"] = null,
    };

    /// <summary>A quality row: values rounded to 0.001 and the best version, as bestOf of the UI (two or more values, not all equal).</summary>
    private static JsonObject QualityRow(string key, string label, IReadOnlyList<ModelVersionResponseDto> versions, Func<ModelVersionResponseDto, double?> value)
    {
        var values = versions.Select(value).ToArray();
        var known = values.Where(item => item is not null).Select(item => item!.Value).ToArray();
        string? best = null;
        if (known.Length >= 2 && known.Distinct().Count() > 1)
        {
            var maximum = known.Max();
            best = versions[Array.FindIndex(values, item => item == maximum)].Id.ToString("D");
        }
        return new JsonObject
        {
            ["key"] = key,
            ["label"] = label,
            ["values"] = new JsonArray(values.Select(item => (JsonNode?)(item is { } number ? JsonValue.Create(Math.Round(number, 4)) : null)).ToArray()),
            ["best"] = best,
        };
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "—",
    };

    private static double? Number(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
