namespace MautVision.Desktop.Transport;

/// <summary>A serialized reply together with the method and requestId it answers, for logging its delivery.</summary>
public readonly record struct BridgeResponse(string Method, string RequestId, string Json);
