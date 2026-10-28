using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// A stand-in for python/worker that speaks protocol v1 over a real job folder: request.json and input/ in, JSON Lines events on
// stdout, output/history.jsonl, artifacts and an atomically published output/result.json. The scenario comes from the dataset
// name in input/manifest.json («fake:<scenario>»), so a smoke chooses it per run:
//   (none) success · hang — one epoch, then a child process (PID in logs/child.pid) and waiting forever · fail=<code> — a failed
//   result · crash — exit 7 without a result · exit0 — exit 0 without a result · badsha — a wrong artifact hash · sequence — a
//   repeated sequence number · publish-hang — a complete result, then waiting forever · slow — 300 ms per epoch · better — success
//   with higher final metrics (versions to compare).
// evaluate, predict and live are answered by FakeChecks (scenarios named there).
// The worker writes UTF-8 to both streams (PYTHONIOENCODING=utf-8).
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
if (args.Length == 2 && args[0] == "--child")
{
    Thread.Sleep(TimeSpan.FromSeconds(int.Parse(args[1])));
    return 0;
}
if (args.Length < 1 || !Directory.Exists(args[^1]))
{
    Console.Error.WriteLine("usage: fake-worker <job folder>");
    return 2;
}
var job = Path.GetFullPath(args[^1]);
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var sequence = 0L;
void Emit(string type, JsonObject fields)
{
    fields["type"] = type;
    fields["sequence"] = ++sequence;
    fields["ts"] = DateTimeOffset.UtcNow.ToString("O");
    stdout.WriteLine(fields.ToJsonString());
}
Console.Error.WriteLine($"[fake-worker] pid {Environment.ProcessId}, cwd {Environment.CurrentDirectory}, catalog {Environment.GetEnvironmentVariable("MAUT_MODEL_CATALOG") is { Length: > 0 }}");

var request = JsonNode.Parse(File.ReadAllText(Path.Combine(job, "request.json")))!.AsObject();
var operationId = request["operationId"]!.GetValue<string>();
var parameters = request["params"]!.AsObject();
var operation = request["operation"]!.GetValue<string>();
if (operation != "train") return FakeChecks.Run(job, request, operation, Emit);
var epochs = parameters["epochs"]!.GetValue<int>();
var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(job, "input", "manifest.json")))!.AsObject();
var datasetName = manifest["dataset"]!["name"]!.GetValue<string>();
var scenario = datasetName.StartsWith("fake:", StringComparison.Ordinal) ? datasetName["fake:".Length..] : "";
var classes = manifest["classes"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
var output = Path.Combine(job, "output");
var artifacts = Directory.CreateDirectory(Path.Combine(output, "artifacts")).FullName;
var started = DateTimeOffset.UtcNow;

if (scenario == "sequence")
{
    Emit("started", new JsonObject { ["operation"] = "train", ["operationId"] = operationId, ["protocolVersion"] = 1, ["pid"] = Environment.ProcessId });
    sequence = 0;
    Emit("stage", new JsonObject { ["stage"] = "validating" });
    Thread.Sleep(5000);
    return 0;
}
Emit("started", new JsonObject { ["operation"] = "train", ["operationId"] = operationId, ["protocolVersion"] = 1, ["pid"] = Environment.ProcessId });
Emit("stage", new JsonObject { ["stage"] = "validating" });
var datasetValid = File.Exists(Path.Combine(job, "input", "dataset", "data.yaml"))
    && File.Exists(Path.Combine(job, "input", "model", parameters["architecture"]!.GetValue<string>() + ".pt"));
if (!datasetValid) return Fail("dataset_invalid", "В папке задачи нет датасета или весов.");
if (scenario.StartsWith("fail=", StringComparison.Ordinal)) return Fail(scenario["fail=".Length..], $"Сообщение worker: {scenario["fail=".Length..]}.");
if (scenario == "crash")
{
    Console.Error.WriteLine("Traceback (most recent call last):");
    Console.Error.WriteLine("RuntimeError: поддельный сбой");
    return 7;
}
if (scenario == "exit0") return 0;

var requested = parameters.DeepClone().AsObject();
requested["lr0Source"] = "computed";
Emit("effectiveParams", new JsonObject { ["parameters"] = requested, ["capture"] = "requested" });
Emit("environment", new JsonObject { ["environment"] = Runtime() });
Emit("stage", new JsonObject { ["stage"] = "loading_model" });
Emit("stage", new JsonObject { ["stage"] = "training" });
var single = parameters["singleCls"]?.GetValue<bool>() == true;
var trainer = parameters.DeepClone().AsObject();
trainer["effectiveBatch"] = parameters["batch"]!.GetValue<double>() >= 1 ? parameters["batch"]!.GetValue<int>() : 8;
trainer["optimizer"] = "AdamW";
trainer["outputClasses"] = new JsonArray((single ? ["item"] : classes).Select((name, index) => (JsonNode)new JsonObject { ["index"] = index, ["name"] = name }).ToArray());
// A large snapshot (a long class map) must pass the line limit of effectiveParams.
trainer["padding"] = new string('x', 300_000);
Emit("effectiveParams", new JsonObject { ["parameters"] = trainer, ["capture"] = "trainer" });

var history = Path.Combine(output, "history.jsonl");
File.WriteAllText(history, "");
for (var epoch = 1; epoch <= epochs; epoch++)
{
    for (var batch = 1; batch <= 3; batch++) Emit("batch", new JsonObject { ["epoch"] = epoch, ["batch"] = batch, ["batches"] = 3 });
    Emit("log", new JsonObject { ["message"] = $"      {epoch}/{epochs}  box_loss {1.5 / epoch:F3}" });
    var record = new JsonObject
    {
        ["epoch"] = epoch,
        ["losses"] = new JsonObject { ["box_loss"] = 1.5 / epoch, ["cls_loss"] = 3.0 / epoch, ["dfl_loss"] = 1.1 },
        ["metrics"] = new JsonObject { ["precision"] = 0.1 * epoch, ["recall"] = 0.2, ["mAP50"] = 0.05 * epoch, ["mAP50_95"] = 0.03 * epoch, ["valBoxLoss"] = 1.2 },
        ["lr"] = 0.001,
        ["fitness"] = 0.03 * epoch,
    };
    File.AppendAllText(history, record.ToJsonString() + "\n");
    var eventFields = record.DeepClone().AsObject();
    eventFields["epochs"] = epochs;
    Emit("epoch", eventFields);
    if (scenario == "slow") Thread.Sleep(300);
    if (scenario == "hang")
    {
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child", "600" }, UseShellExecute = false, CreateNoWindow = true })!;
        File.WriteAllText(Path.Combine(job, "logs", "child.pid"), child.Id.ToString());
        Emit("warning", new JsonObject { ["code"] = "fake_hang", ["message"] = "Поддельный worker ждёт отмены." });
        Thread.Sleep(Timeout.Infinite);
    }
}
Emit("stage", new JsonObject { ["stage"] = "finalizing" });
var best = Write(Path.Combine(artifacts, "best.pt"), Encoding.ASCII.GetBytes("fake weights " + operationId));
var onnx = Write(Path.Combine(artifacts, "best.onnx"), Encoding.ASCII.GetBytes("fake onnx " + operationId));
var plot = Write(Path.Combine(artifacts, "plots", "results.png"), Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
var log = Write(Path.Combine(artifacts, "training.log"), Encoding.UTF8.GetBytes("Обучение поддельной модели\n"));
var bestEntry = Entry("weights_pt", best);
if (scenario == "badsha") bestEntry["sha256"] = new string('0', 64);
var result = new JsonObject
{
    ["protocolVersion"] = 1,
    ["operationId"] = operationId,
    ["operation"] = "train",
    ["status"] = "succeeded",
    ["startedAt"] = started.ToString("O"),
    ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
    ["durationSec"] = 0.1,
    ["error"] = null,
    ["warnings"] = new JsonArray(new JsonObject { ["code"] = "fake_warning", ["message"] = "Поддельное предупреждение." }),
    ["architecture"] = new JsonObject { ["yamlFile"] = parameters["architecture"]!.GetValue<string>() + ".yaml", ["scale"] = "n", ["task"] = "detect" },
    ["effectiveParams"] = trainer.DeepClone(),
    ["metrics"] = scenario == "better"
        ? new JsonObject { ["precision"] = 0.55, ["recall"] = 0.45, ["mAP50"] = 0.35, ["mAP50_95"] = 0.25 }
        : new JsonObject { ["precision"] = 0.5, ["recall"] = 0.4, ["mAP50"] = 0.3, ["mAP50_95"] = 0.2 },
    ["epochsCompleted"] = epochs,
    ["stoppedEarly"] = false,
    ["history"] = new JsonArray(),
    ["historyFile"] = Entry("history", history, relativeTo: output, includeKind: false, count: epochs),
    ["classMetrics"] = new JsonArray(),
    ["artifacts"] = new JsonArray(bestEntry, Entry("onnx", onnx)),
    ["reports"] = new JsonArray(Entry("plot", plot), Entry("log", log)),
    ["environment"] = Runtime(),
};
var temporary = Path.Combine(output, $".result.json.{System.Environment.ProcessId}.tmp");
File.WriteAllText(temporary, result.ToJsonString());
File.Move(temporary, Path.Combine(output, "result.json"), overwrite: true);
if (scenario == "publish-hang") Thread.Sleep(Timeout.Infinite);
Emit("finished", new JsonObject { ["status"] = "succeeded", ["errorCode"] = null });
return 0;

int Fail(string code, string message)
{
    var failed = new JsonObject
    {
        ["protocolVersion"] = 1,
        ["operationId"] = operationId,
        ["operation"] = "train",
        ["status"] = "failed",
        ["startedAt"] = started.ToString("O"),
        ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        ["durationSec"] = 0.1,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["details"] = new JsonObject() },
    };
    Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(output, "result.json"), failed.ToJsonString());
    Emit("finished", new JsonObject { ["status"] = "failed", ["errorCode"] = code });
    return 1;
}

string Write(string path, byte[] bytes)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    return path;
}

JsonObject Entry(string kind, string path, string? relativeTo = null, bool includeKind = true, int? count = null)
{
    var bytes = File.ReadAllBytes(path);
    var entry = new JsonObject
    {
        ["path"] = Path.GetRelativePath(relativeTo ?? output, path).Replace('\\', '/'),
        ["sizeBytes"] = bytes.Length,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
    };
    if (includeKind) entry["kind"] = kind;
    if (count is not null) entry["count"] = count;
    return entry;
}

JsonObject Runtime() => new()
{
    ["python"] = "3.14.7",
    ["torch"] = "2.14.0+fake",
    ["ultralytics"] = "8.4.157",
    ["cuda"] = "13.0",
    ["cudaAvailable"] = false,
    ["deviceName"] = "Поддельная видеокарта",
};
