using System.Text.Json;

namespace MautVision.Desktop.Transport;

internal sealed record RequestEnvelope(string RequestId, string Method, JsonElement Payload);
internal sealed record SuccessEnvelope(string RequestId, object Data)
{
    public string Kind => "response";
    public bool Ok => true;
}
internal sealed record FailureEnvelope(string RequestId, BridgeError Error)
{
    public string Kind => "response";
    public bool Ok => false;
}
internal sealed record EventEnvelope(string Event, object Data)
{
    public string Kind => "event";
}
/// <summary>Details — safe values that help the page react, such as the form field of a validation error; null when there are none.</summary>
internal sealed record BridgeError(string Code, string Message, IReadOnlyDictionary<string, string>? Details = null);
