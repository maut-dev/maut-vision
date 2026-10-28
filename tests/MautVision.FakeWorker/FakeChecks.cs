using System.Text.Json.Nodes;

/// <summary>
/// Model checks of the fake worker (protocol v1 without PyTorch). The scenario of an evaluation is the dataset name in
/// input/manifest.json, as for training («fake:fail=&lt;code&gt;», «fake:hang», «fake:slow»); of predict — the source file name
/// (contains «fake-fail» → source_invalid, «fake-hang» → waits for cancel, «fake-many» → a video of 5 100 frames with
/// detections); of live — the frequency: fps ≥ 50 sends 2 100 frames at once (the ring of the journal), otherwise frames come at
/// fps until the worker is killed or maxDurationSec passes (a time-limit warning and a result). Detections per frame are
/// min(maxDetections, 3), so a check sees whether the limit was passed. Previews are JPEG files under output/previews/.
/// </summary>
internal static class FakeChecks
{
    private static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYa"
        + "KCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAEAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAA"
        + "AgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWG"
        + "h4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREA"
        + "AgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOE"
        + "hYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCzRRRXwR9sf//Z");

    internal static int Run(string job, JsonObject request, string operation, Action<string, JsonObject> emit)
    {
        var operationId = request["operationId"]!.GetValue<string>();
        var parameters = request["params"]!.AsObject();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(job, "input", "manifest.json")))!.AsObject();
        var classes = manifest["classes"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
        var output = Directory.CreateDirectory(Path.Combine(job, "output")).FullName;
        var started = DateTimeOffset.UtcNow;
        emit("started", new JsonObject { ["operation"] = operation, ["operationId"] = operationId, ["protocolVersion"] = 1, ["pid"] = Environment.ProcessId });
        emit("stage", new JsonObject { ["stage"] = "validating" });
        var model = request["input"]!["model"]!.GetValue<string>();
        if (!File.Exists(Path.Combine(job, model.Replace('/', Path.DirectorySeparatorChar)))) return Fail("model_not_found", "Нет весов версии.");
        emit("stage", new JsonObject { ["stage"] = "loading_model" });
        return operation switch
        {
            "evaluate" => Evaluate(),
            "predict" => Predict(),
            "live" => Live(),
            _ => Fail("operation_not_implemented", $"Операции {operation} нет."),
        };

        int Evaluate()
        {
            var scenario = Scenario(manifest["dataset"]?["name"]?.GetValue<string>());
            var split = parameters["split"]!.GetValue<string>();
            var images = Directory.EnumerateFiles(Path.Combine(job, "input", "dataset", "images", split)).Count();
            if (images == 0) return Fail("dataset_invalid", $"В выборке {split} нет изображений.");
            if (scenario.StartsWith("fail=", StringComparison.Ordinal)) return Fail(scenario["fail=".Length..], $"Сообщение worker: {scenario["fail=".Length..]}.");
            emit("stage", new JsonObject { ["stage"] = "evaluating" });
            if (scenario == "hang") Thread.Sleep(Timeout.Infinite);
            if (scenario == "slow") Thread.Sleep(1500);
            return Succeed(new JsonObject
            {
                ["architecture"] = new JsonObject { ["yamlFile"] = parameters["architecture"]!.GetValue<string>() + ".yaml", ["scale"] = "n", ["task"] = "detect" },
                ["effectiveParams"] = new JsonObject
                {
                    ["architecture"] = parameters["architecture"]!.DeepClone(),
                    ["imgsz"] = parameters["imgsz"]!.DeepClone(),
                    ["batch"] = parameters["batch"]!.DeepClone(),
                    ["device"] = parameters["device"]!.DeepClone(),
                    ["split"] = split,
                    ["dataset"] = new JsonObject { ["classes"] = classes.Length, [split == "test" ? "imagesTest" : "imagesVal"] = images },
                },
                ["metrics"] = new JsonObject { ["precision"] = 0.8, ["recall"] = 0.7, ["mAP50"] = 0.6, ["mAP50_95"] = 0.4 },
                ["classMetrics"] = new JsonArray(classes.Select((name, index) => (JsonNode)new JsonObject
                {
                    ["index"] = index,
                    ["name"] = name,
                    ["metrics"] = new JsonObject { ["precision"] = 0.8, ["recall"] = 0.7, ["mAP50"] = 0.6 - index * 0.1, ["mAP50_95"] = 0.4 },
                }).ToArray()),
                ["environment"] = new JsonObject { ["python"] = "3.14.7", ["torch"] = "2.14.0+fake", ["ultralytics"] = "8.4.157" },
            });
        }

        int Predict()
        {
            var name = manifest["source"]?["name"]?.GetValue<string>() ?? "";
            var input = request["input"]!.AsObject();
            var source = input["source"] is { } relative ? Path.Combine(job, relative.GetValue<string>().Replace('/', Path.DirectorySeparatorChar))
                : input["externalSource"]!.GetValue<string>();
            if (!File.Exists(source) || name.Contains("fake-fail", StringComparison.Ordinal)) return Fail("source_invalid", "Файл не распознан как изображение или видео.");
            emit("stage", new JsonObject { ["stage"] = "predicting" });
            if (name.Contains("fake-hang", StringComparison.Ordinal)) Thread.Sleep(Timeout.Infinite);
            if (parameters["source"]!.GetValue<string>() == "image")
            {
                var found = Detections();
                emit("frame", Frame(0, 0, found, Preview(0)));
                return Succeed(new JsonObject
                {
                    ["summary"] = Summary(1, found.Count > 0 ? 1 : 0, found.Count),
                    ["source"] = new JsonObject { ["width"] = 64, ["height"] = 48 },
                    ["frames"] = new JsonArray(),
                });
            }
            var total = name.Contains("fake-many", StringComparison.Ordinal) ? 5100 : 12;
            var withDetections = 0;
            var boxes = 0;
            for (var index = 0; index < total; index++)
            {
                // Every other frame of a short video is empty; it carries only a preview now and then.
                var found = total > 12 || index % 2 == 0 ? Detections() : new JsonArray();
                if (found.Count > 0)
                {
                    withDetections++;
                    boxes += found.Count;
                }
                var preview = index % 4 == 0 ? Preview(index) : null;
                if (found.Count > 0 || preview is not null) emit("frame", Frame(index, index * 100, found, preview));
                if (index % 3 == 2) emit("progress", new JsonObject { ["framesProcessed"] = index + 1, ["framesTotal"] = total, ["positionMs"] = index * 100, ["durationMs"] = total * 100 });
                if (total <= 12) Thread.Sleep(30);
            }
            return Succeed(new JsonObject
            {
                ["summary"] = Summary(total, withDetections, boxes),
                ["source"] = new JsonObject { ["width"] = 640, ["height"] = 360, ["durationMs"] = total * 100, ["fps"] = 10.0 },
            });
        }

        int Live()
        {
            var fps = parameters["fps"]!.GetValue<double>();
            var limit = TimeSpan.FromSeconds(parameters["maxDurationSec"]!.GetValue<int>());
            var interval = parameters["previewIntervalMs"]!.GetValue<int>();
            emit("stage", new JsonObject { ["stage"] = "capturing" });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var lastPreview = TimeSpan.FromDays(-1);
            var frames = 0;
            var boxes = 0;
            var burst = fps >= 50 ? 2100 : 0;
            while (clock.Elapsed < limit)
            {
                var found = Detections();
                boxes += found.Count;
                string? preview = null;
                if (clock.Elapsed - lastPreview >= TimeSpan.FromMilliseconds(interval))
                {
                    lastPreview = clock.Elapsed;
                    preview = Preview(frames);
                }
                emit("frame", Frame(frames, (long)clock.Elapsed.TotalMilliseconds, found, preview));
                frames++;
                if (frames > burst) Thread.Sleep(TimeSpan.FromSeconds(1 / fps));
            }
            emit("warning", new JsonObject { ["code"] = "live_time_limit", ["message"] = $"Проверка с экрана остановлена по пределу {limit.TotalSeconds:0} с" });
            return Succeed(new JsonObject
            {
                ["summary"] = Summary(frames, frames, boxes),
                ["source"] = new JsonObject { ["width"] = 64, ["height"] = 48 },
                ["warnings"] = new JsonArray(new JsonObject { ["code"] = "live_time_limit", ["message"] = $"Проверка с экрана остановлена по пределу {limit.TotalSeconds:0} с" }),
            });
        }

        JsonArray Detections()
        {
            var count = Math.Min(parameters["maxDetections"]?.GetValue<int>() ?? 3, 3);
            return new JsonArray(Enumerable.Range(0, count).Select(index => (JsonNode)new JsonObject
            {
                ["classIndex"] = index % classes.Length,
                ["name"] = classes[index % classes.Length],
                ["confidence"] = 0.9 - index * 0.1,
                ["box"] = new JsonObject { ["x"] = 0.1 * index, ["y"] = 0.2, ["width"] = 0.25, ["height"] = 0.3 },
            }).ToArray());
        }

        JsonObject Frame(long index, long timeMs, JsonArray detections, string? preview)
        {
            var frame = new JsonObject
            {
                ["index"] = index,
                ["timeMs"] = timeMs,
                ["detections"] = detections,
                ["width"] = 64,
                ["height"] = 48,
                ["inferenceMs"] = 3,
            };
            if (preview is not null) frame["previewFile"] = preview;
            return frame;
        }

        string Preview(long index)
        {
            var folder = Directory.CreateDirectory(Path.Combine(output, "previews")).FullName;
            File.WriteAllBytes(Path.Combine(folder, $"frame-{index}.jpg"), Jpeg);
            return $"previews/frame-{index}.jpg";
        }

        JsonObject Summary(long frames, long withDetections, long boxes) => new()
        {
            ["framesProcessed"] = frames,
            ["framesWithDetections"] = withDetections,
            ["detections"] = boxes,
            ["classes"] = new JsonArray(classes.Select((name, index) => (JsonNode)new JsonObject
            {
                ["index"] = index,
                ["name"] = name,
                ["frames"] = withDetections,
                ["detections"] = boxes,
                ["maxConfidence"] = 0.9,
            }).Take(Math.Min(classes.Length, 3)).ToArray()),
        };

        int Succeed(JsonObject fields)
        {
            var result = new JsonObject
            {
                ["protocolVersion"] = 1,
                ["operationId"] = operationId,
                ["operation"] = operation,
                ["status"] = "succeeded",
                ["startedAt"] = started.ToString("O"),
                ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["durationSec"] = 0.1,
                ["error"] = null,
            };
            foreach (var (key, value) in fields) result[key] = value?.DeepClone();
            var temporary = Path.Combine(output, $".result.json.{Environment.ProcessId}.tmp");
            File.WriteAllText(temporary, result.ToJsonString());
            File.Move(temporary, Path.Combine(output, "result.json"), overwrite: true);
            emit("finished", new JsonObject { ["status"] = "succeeded", ["errorCode"] = null });
            return 0;
        }

        int Fail(string code, string message)
        {
            var failed = new JsonObject
            {
                ["protocolVersion"] = 1,
                ["operationId"] = operationId,
                ["operation"] = operation,
                ["status"] = "failed",
                ["startedAt"] = started.ToString("O"),
                ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["durationSec"] = 0.1,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["details"] = new JsonObject() },
            };
            File.WriteAllText(Path.Combine(output, "result.json"), failed.ToJsonString());
            emit("finished", new JsonObject { ["status"] = "failed", ["errorCode"] = code });
            return 1;
        }
    }

    private static string Scenario(string? name) => name is not null && name.StartsWith("fake:", StringComparison.Ordinal) ? name["fake:".Length..] : "";
}
