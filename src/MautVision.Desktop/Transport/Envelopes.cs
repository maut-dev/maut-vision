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
/// <summary>
/// Code — the stable error code the page translates; Details — safe values that help the page react and fill the text, such as the form
/// field of a validation error; null when there are none. There is no text: the UI owns the wording in every language.
/// </summary>
internal sealed record BridgeError(string Code, IReadOnlyDictionary<string, string>? Details = null);
