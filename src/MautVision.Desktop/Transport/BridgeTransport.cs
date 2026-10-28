using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Transport;

public sealed class BridgeTransport(ILogger<BridgeTransport> logger) : IDisposable
{
    private const int MaxLoggedTextLength = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 12
    };
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object>>> routes = new(StringComparer.Ordinal);
    private int disposed;

    public event Action<string>? EventReady;

    public async Task<BridgeResponse> ExecuteAsync(string message, CancellationToken cancellationToken)
    {
        var requestId = "";
        var method = "";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Encoding.UTF8.GetByteCount(message) > 32_768)
            {
                return Fail(method, requestId, new("bridge.invalid_request"), null, "The message is too large.");
            }
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 12 });
            RejectDuplicateProperties(document.RootElement);
            var request = document.RootElement.Deserialize<RequestEnvelope>(Json) ?? throw new JsonException();
            if (request.RequestId is not { Length: >= 1 and <= 80 }
                || request.RequestId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            {
                throw new JsonException();
            }
            requestId = request.RequestId;
            method = Shorten(request.Method ?? "");
            if (string.IsNullOrEmpty(request.Method) || request.Payload.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }
            if (!routes.TryGetValue(request.Method, out var route))
            {
                return Fail(method, requestId, new("bridge.method_unavailable"), null, "The method is not registered.");
            }
            var data = await route(request.Payload, cancellationToken).ConfigureAwait(false);
            return new(method, requestId, JsonSerializer.Serialize(new SuccessEnvelope(requestId, data), Json));
        }
        catch (Exception error)
        {
            return Fail(method, requestId, BridgeErrors.From(error, cancellationToken), error, error.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        EventReady = null;
    }

    internal void Register<TRequest, TResponse>(string method, Func<TRequest, CancellationToken, Task<TResponse>> handler)
        where TRequest : class where TResponse : class
    {
        routes.Add(method, async (payload, token) => await handler(payload.Deserialize<TRequest>(Json) ?? throw new JsonException(), token).ConfigureAwait(false));
    }

    internal void PublishEvent(string name, object data)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var message = JsonSerializer.Serialize(new EventEnvelope(name, data), Json);
        var subscribers = EventReady;
        if (subscribers is null) return;
        foreach (Action<string> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(message); }
            catch (Exception error) { logger.LogError(error, "Bridge event {Event} delivery failed.", name); }
        }
    }

    /// <summary>Logs the failure with the method and requestId; the full exception only for errors, never the payload.</summary>
    private BridgeResponse Fail(string method, string requestId, BridgeError error, Exception? exception, string reason)
    {
        var level = BridgeErrors.LogLevelFor(error.Code);
        logger.Log(level, level >= LogLevel.Error ? exception : null, "Bridge request {Method} {RequestId} failed with {Code}: {Reason}",
            method, requestId, error.Code, Shorten(reason));
        return new(method, requestId, JsonSerializer.Serialize(new FailureEnvelope(requestId, error), Json));
    }

    /// <summary>Text from the page is limited and kept on one line so that it cannot imitate other records.</summary>
    private static string Shorten(string text)
    {
        var limited = text.Length <= MaxLoggedTextLength ? text : text[..MaxLoggedTextLength] + "…";
        return string.Create(limited.Length, limited, (span, source) =>
        {
            for (var index = 0; index < source.Length; index++) span[index] = char.IsControl(source[index]) ? ' ' : source[index];
        });
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
