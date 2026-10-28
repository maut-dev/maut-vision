namespace MautVision.Desktop.Transport;

internal static class WebViewPolicy
{
    internal const string UiHost = "maut-ui.invalid";
    internal const string UiOrigin = "https://" + UiHost;

    /// <summary>Material files by opaque handle; never navigated to and not mapped to a folder.</summary>
    internal const string ResourceHost = "maut-resource.invalid";
    internal const string ResourceOrigin = "https://" + ResourceHost;

    internal static bool IsTrusted(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == UiHost && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.AbsolutePath is "/" or "/index.html";

    /// <summary>
    /// A resource request must come from the UI document: its Origin (fetch) or Referer (img, video) names the UI.
    /// The default referrer policy sends only the origin to another host, so "/" is the usual Referer path.
    /// </summary>
    internal static bool IsTrustedResourceRequester(string? origin, string? referrer)
    {
        if (origin is not null && origin != UiOrigin)
        {
            return false;
        }
        if (referrer is not null)
        {
            return IsTrusted(referrer);
        }
        return origin is not null;
    }
}
