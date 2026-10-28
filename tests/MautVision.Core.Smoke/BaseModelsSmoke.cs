using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Installation scenarios against a fake HTTP source: no network access is needed.</summary>
internal static class BaseModelsSmoke
{
    internal static async Task<int> RunAsync(string fixture)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task<T> Rejects<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T error) { checks++; return error; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }

        var catalog = Path.Combine(fixture, "catalog", "base-models.json");
        var directory = Path.Combine(fixture, "base-models");
        var weights = Path.Combine(directory, "weights");
        Directory.CreateDirectory(weights);
        var good = Payload(4096, 1);
        var slow = Payload(64 * 1024, 2);
        var source = new FakeSource();
        source.Add("good", good);
        source.Add("slow", slow, chunk: 1024, delay: TimeSpan.FromMilliseconds(20));
        source.Add("tampered", Payload(4096, 3));
        source.Add("resized", good, reportedLength: good.Length + 1);
        source.Add("missing", good, status: HttpStatusCode.NotFound);
        var preinstalled = Payload(2048, 4);
        var damaged = Payload(2048, 5);
        await WriteCatalogAsync(catalog, [
            Entry("good", good),
            Entry("slow", slow),
            Entry("tampered", good),
            Entry("resized", good),
            Entry("missing", good),
            Entry("preinstalled", preinstalled),
            Entry("damaged", damaged),
        ]);
        await File.WriteAllBytesAsync(Path.Combine(weights, "preinstalled.pt"), preinstalled);
        await File.WriteAllBytesAsync(Path.Combine(weights, "damaged.pt"), damaged[..100]);
        var leftover = Path.Combine(weights, ".good.pt.0123.download");
        await File.WriteAllTextAsync(leftover, "interrupted");

        var log = new LogCapture();
        var manager = Create(catalog, directory, source, log.CreateLogger("BaseModels"));
        var events = new ConcurrentQueue<(string Id, BaseModelStatus Status, double Progress)>();
        manager.ModelChanged += (_, _) => throw new InvalidOperationException("Broken observer.");
        manager.ModelChanged += (_, args) => events.Enqueue((args.Model.Id, args.Model.Status, args.Model.Progress));

        var models = await manager.ListAsync();
        var model = models.Single(entry => entry.Id == "good");
        Check(!File.Exists(leftover), "Loading the catalogue removes interrupted downloads.");
        Check(model.Status == BaseModelStatus.NotInstalled && model.Progress == 0 && model.ErrorCode is null, "A missing file is not installed.");
        Check((await manager.GetOrNullAsync("preinstalled"))!.Status == BaseModelStatus.Installed, "A stored file of the catalogue size is installed.");
        var broken = (await manager.GetOrNullAsync("damaged"))!;
        Check(broken.Status == BaseModelStatus.Failed && broken.ErrorCode == "base_models.integrity_failed", "A stored file of another size is failed.");
        Check(source.Requests == 0, "Loading the catalogue downloads nothing.");

        await model.InstallAsync();
        Check(model.Status == BaseModelStatus.Installed && model.Progress == 0 && model.ErrorCode is null, "Installation publishes an installed model.");
        Check((await File.ReadAllBytesAsync(Path.Combine(weights, "good.pt"))).SequenceEqual(good), "Installed weights match the source.");
        Check(events.Any(entry => entry.Id == "good" && entry.Status == BaseModelStatus.Installing)
            && events.Last(entry => entry.Id == "good").Status == BaseModelStatus.Installed, "Installation reports its transitions despite a failing observer.");
        Check(ReferenceEquals(model, await manager.GetOrNullAsync("good")), "Installation changes the shared instance.");
        await model.InstallAsync();
        Check(source.Requests == 1, "An installed model is not downloaded again.");
        Check(await model.VerifyIntegrityAsync(), "Integrity check accepts installed weights.");
        using (var copy = new MemoryStream())
        {
            await model.CopyToAsync(copy);
            Check(copy.ToArray().SequenceEqual(good), "CopyTo streams installed weights.");
        }

        var slowModel = (await manager.GetOrNullAsync("slow"))!;
        var installations = Enumerable.Range(0, 4).Select(_ => slowModel.InstallAsync()).ToArray();
        Check(slowModel.Status == BaseModelStatus.Installing, "Starting installation changes status at once.");
        await WaitUntil(() => slowModel.Progress > 0, "Installation reports progress.");
        await Rejects<CoreException>(() => slowModel.DeleteAsync());
        await Rejects<CoreException>(() => slowModel.VerifyIntegrityAsync());
        await slowModel.StopInstallationAsync();
        await Task.WhenAll(installations);
        Check(source.RequestsFor("slow") == 1, "Concurrent installation requests share one download.");
        Check(slowModel.Status == BaseModelStatus.NotInstalled && slowModel.Progress == 0, "Stopping installation leaves the model not installed.");
        Check(!Directory.EnumerateFiles(weights, "*.download").Any() && !File.Exists(Path.Combine(weights, "slow.pt")), "Stopping removes the partial download.");

        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = slowModel.InstallAsync(cancellation.Token);
            await WaitUntil(() => slowModel.Progress > 0, "Second installation reports progress.");
            cancellation.Cancel();
            await Rejects<OperationCanceledException>(() => cancelled);
            Check(slowModel.Status == BaseModelStatus.NotInstalled, "Cancelling the token stops installation.");
        }

        await Failed("tampered", "base_models.integrity_failed");
        await Failed("resized", "base_models.source_changed");
        await Failed("missing", "base_models.download_failed");
        Check(log.Records.Any(record => record.Level == LogLevel.Error && record.Exception is not null
            && record.Message.Contains("missing") && record.Message.Contains("base_models.download_failed")), "Installation failure is logged with model id, code and exception.");
        Check(!File.Exists(Path.Combine(weights, "tampered.pt")) && !Directory.EnumerateFiles(weights, "*.download").Any(), "Rejected downloads publish nothing.");

        await File.WriteAllBytesAsync(Path.Combine(weights, "good.pt"), Payload(good.Length, 9));
        Check(!await model.VerifyIntegrityAsync() && model.Status == BaseModelStatus.Failed && model.ErrorCode == "base_models.integrity_failed", "Integrity check rejects changed weights.");
        await model.InstallAsync();
        Check(model.Status == BaseModelStatus.Installed && (await File.ReadAllBytesAsync(Path.Combine(weights, "good.pt"))).SequenceEqual(good), "Reinstallation replaces damaged weights.");

        await model.DeleteAsync();
        Check(model.Status == BaseModelStatus.NotInstalled && !File.Exists(Path.Combine(weights, "good.pt")), "Delete removes installed weights.");
        await model.DeleteAsync();
        Check(model.Status == BaseModelStatus.NotInstalled, "Deleting a missing file succeeds.");
        Check(!await model.VerifyIntegrityAsync() && model.Status == BaseModelStatus.NotInstalled, "Integrity check of a missing file reports not installed.");
        var notInstalled = await Rejects<CoreException>(() => model.CopyToAsync(new MemoryStream()));
        Check(notInstalled.Code == "base_models.not_installed", "CopyTo requires an installed model.");

        var restarted = Create(catalog, directory, source);
        Check((await restarted.GetOrNullAsync("good"))!.Status == BaseModelStatus.NotInstalled
            && (await restarted.GetOrNullAsync("preinstalled"))!.Status == BaseModelStatus.Installed, "A new manager restores state from stored files.");

        var invalid = Path.Combine(fixture, "base-models-invalid");
        var invalidCatalog = Path.Combine(fixture, "catalog", "invalid.json");
        await WriteCatalogAsync(invalidCatalog, [Entry("escape", good) with { FileName = "../escape.pt" }]);
        await Rejects<InvalidDataException>(() => Create(invalidCatalog, invalid, source, log.CreateLogger("BaseModels")).ListAsync());
        Check(log.Records.Any(record => record.Level == LogLevel.Error && record.Exception is InvalidDataException
            && record.Message.Contains("catalogue")), "Catalogue load failure is logged.");
        await WriteCatalogAsync(invalidCatalog, [Entry("plain", good) with { Url = "http://models.test/plain" }]);
        await Rejects<InvalidDataException>(() => Create(invalidCatalog, invalid, source).ListAsync());
        return checks;

        async Task Failed(string id, string code)
        {
            var entry = (await manager.GetOrNullAsync(id))!;
            await entry.InstallAsync();
            Check(entry.Status == BaseModelStatus.Failed && entry.ErrorCode == code && entry.Progress == 0, $"{id} installation fails with {code}.");
        }
    }

    private static IBaseModelsManager Create(string catalog, string directory, FakeSource source, ILogger? logger = null)
    {
        var type = typeof(CoreFactory).Assembly.GetType("MautVision.Core.BaseModels.BaseModelsManager", throwOnError: true)!;
        return (IBaseModelsManager)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [catalog, directory, new HttpClient(source), logger ?? NullLogger.Instance], culture: null)!;
    }

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException(message);
    }

    private static byte[] Payload(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static CatalogEntry Entry(string id, byte[] content) => new(
        id, "TEST", id.ToUpperInvariant(), "detect", id + ".pt", content.Length,
        Convert.ToHexStringLower(SHA256.HashData(content)), "https://models.test/" + id, 32, [id], ["test"], "n");

    private static async Task WriteCatalogAsync(string path, CatalogEntry[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(entries, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed record CatalogEntry(string Id, string Family, string Variant, string Task, string FileName, long ByteSize,
        string Sha256, string Url, int Stride, string[] YamlNames, string[] YamlFamilies, string Scale);

    private sealed class FakeSource : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> responses = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> requests = new(StringComparer.Ordinal);

        internal int Requests => requests.Values.Sum();

        internal int RequestsFor(string id) => requests.GetValueOrDefault(id);

        internal void Add(string id, byte[] content, int chunk = 4096, TimeSpan delay = default, long? reportedLength = null, HttpStatusCode status = HttpStatusCode.OK)
        {
            responses[id] = () => new HttpResponseMessage(status)
            {
                Content = new StreamContent(new ChunkedStream(content, chunk, delay))
                {
                    Headers = { ContentLength = reportedLength ?? content.Length },
                },
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = request.RequestUri!.AbsolutePath.TrimStart('/');
            requests.AddOrUpdate(id, 1, (_, count) => count + 1);
            return Task.FromResult(responses[id]());
        }
    }

    private sealed class ChunkedStream(byte[] content, int chunk, TimeSpan delay) : Stream
    {
        private int position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            var count = Math.Min(Math.Min(chunk, buffer.Length), content.Length - position);
            content.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
