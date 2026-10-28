using System.Globalization;
using System.IO;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop.Resources;

/// <summary>
/// Serves https://maut-resource.invalid/r/&lt;token&gt; from WebResourceRequested. The handler runs on the UI thread only to
/// check the request and to hand over the response; opening and reading the file run on the thread pool under a deferral.
/// WebView2 needs the whole body when the deferral completes, so one response is at most <see cref="MaxRangeLength"/>
/// bytes for a Range request and <see cref="MaxWholeLength"/> without one. A transient handle (a preview) is served from memory. WebView2 does not report an aborted request;
/// reads stop when the page session ends (navigation, reload) or the window closes.
/// </summary>
internal sealed class ResourceServer(ICore core, ResourceHandles handles, ILogger<ResourceServer> logger)
{
    internal const int MaxRangeLength = 4 * 1024 * 1024;
    internal const int MaxWholeLength = 64 * 1024 * 1024;
    private const string Filter = WebViewPolicy.ResourceOrigin + "/*";

    private readonly HashSet<Task> pending = [];
    private CancellationTokenSource session = new();
    private CoreWebView2? webView;

    internal void Attach(CoreWebView2 web)
    {
        webView = web;
        web.AddWebResourceRequestedFilter(Filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        web.WebResourceRequested += OnRequested;
    }

    /// <summary>Called on the UI thread when a trusted navigation of the UI document starts.</summary>
    internal void ResetSession()
    {
        handles.ResetSession();
        var previous = session;
        session = new CancellationTokenSource();
        // Not disposed: reads of the previous page may still observe its token.
        previous.Cancel();
    }

    /// <summary>Refuses new requests, stops reads in progress and waits for their deferrals to complete.</summary>
    internal async Task CloseAsync()
    {
        handles.Close();
        if (webView is not null)
        {
            webView.WebResourceRequested -= OnRequested;
            try
            {
                webView.RemoveWebResourceRequestedFilter(Filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The browser process may already be gone; there is nothing left to unregister.
            }
            webView = null;
        }
        await session.CancelAsync();
        await Task.WhenAll(pending.ToArray());
    }

    private async void OnRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (sender is not CoreWebView2 web)
        {
            return;
        }
        CoreWebView2Deferral? deferral = null;
        try
        {
            var request = args.Request;
            var origin = Header(request.Headers, "Origin");
            if (args.RequestedSourceKind != CoreWebView2WebResourceRequestSourceKinds.Document
                || !WebViewPolicy.IsTrusted(web.Source)
                || !WebViewPolicy.IsTrustedResourceRequester(origin, Header(request.Headers, "Referer")))
            {
                logger.LogWarning("Resource request from an untrusted source was refused.");
                args.Response = Create(web, Reply.Empty(403, "Forbidden"));
                return;
            }
            var method = request.Method;
            if (method == "OPTIONS")
            {
                args.Response = Create(web, Reply.Preflight());
                return;
            }
            if (method is not ("GET" or "HEAD"))
            {
                args.Response = Create(web, Reply.Empty(405, "Method Not Allowed"));
                return;
            }
            if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri) || uri.Query.Length > 0
                || !uri.AbsolutePath.StartsWith(ResourceHandles.PathPrefix, StringComparison.Ordinal)
                || uri.AbsolutePath[ResourceHandles.PathPrefix.Length..] is not { } token || !ResourceHandles.IsToken(token)
                || !handles.TryResolve(token, out var handle))
            {
                args.Response = Create(web, Reply.Empty(404, "Not Found"));
                return;
            }
            deferral = args.GetDeferral();
            var range = Header(request.Headers, "Range");
            var cancellation = session.Token;
            var work = Task.Run(() => ReadAsync(handle, range, method == "HEAD", cancellation), cancellation);
            pending.Add(work);
            Reply reply;
            try
            {
                reply = await work;
            }
            catch (OperationCanceledException)
            {
                reply = Reply.Empty(404, "Not Found");
            }
            finally
            {
                pending.Remove(work);
            }
            if (webView is null)
            {
                return;
            }
            args.Response = Create(web, reply);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Resource request handling failed.");
        }
        finally
        {
            try
            {
                deferral?.Complete();
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The WebView was closed while the file was read; the request no longer exists.
            }
        }
    }

    private async Task<Reply> ReadAsync(ResourceHandle handle, string? rangeHeader, bool headOnly, CancellationToken cancellationToken)
    {
        try
        {
            await using var content = handle.Content is { } transient
                ? new ResourceContent(new MemoryStream(transient.Bytes, writable: false), transient.ContentType, transient.CreatedAt)
                : await (await core.Projects.GetAsync(handle.ProjectId, cancellationToken).ConfigureAwait(false)).Resources
                    .OpenAsync(handle.Reference, handle.Variant, cancellationToken).ConfigureAwait(false);
            var length = content.Length;
            var maxAge = Math.Max(0, (long)(handle.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            var headers = new List<string>
            {
                "Content-Type: " + content.ContentType,
                "Cache-Control: private, max-age=" + maxAge.ToString(CultureInfo.InvariantCulture),
                "Last-Modified: " + content.LastModified.ToString("R", CultureInfo.InvariantCulture),
                "Accept-Ranges: bytes",
            };
            long start = 0;
            long count = length;
            var status = 200;
            switch (ParseRange(rangeHeader, length, out var first, out var last))
            {
                case RangeKind.Unsatisfiable:
                    return Reply.Empty(416, "Range Not Satisfiable", "Content-Range: bytes */" + length.ToString(CultureInfo.InvariantCulture));
                case RangeKind.Range:
                    start = first;
                    count = Math.Min(last - first + 1, MaxRangeLength);
                    status = 206;
                    headers.Add(string.Create(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{start + count - 1}/{length}"));
                    break;
                case RangeKind.None when length > MaxWholeLength:
                    // Media elements always send Range; a whole large file is not buffered for one response.
                    logger.LogWarning("Resource of {Length} bytes was requested without Range and refused.", length);
                    return Reply.Empty(413, "Content Too Large");
            }
            headers.Add("Content-Length: " + count.ToString(CultureInfo.InvariantCulture));
            if (headOnly)
            {
                return new Reply(status, status == 206 ? "Partial Content" : "OK", headers, null);
            }
            var buffer = new byte[count];
            content.Stream.Seek(start, SeekOrigin.Begin);
            await content.Stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            // Created off the UI thread, as WebView2 recommends for response streams.
            return new Reply(status, status == 206 ? "Partial Content" : "OK", headers, new MemoryStream(buffer, writable: false));
        }
        catch (CoreException error) when (error.Code is "resources.not_found" or "resources.kind_unknown" or "projects.not_found")
        {
            return Reply.Empty(404, "Not Found");
        }
        catch (CoreException error) when (error.Code is "resources.thumbnail_unsupported")
        {
            return Reply.Empty(415, "Unsupported Media Type");
        }
        catch (CoreException error) when (error.Code is "resources.decode_failed")
        {
            logger.LogWarning("Resource {Kind}/{Id} could not be decoded for a thumbnail.", handle.Reference.Kind, handle.Reference.Id);
            return Reply.Empty(422, "Unprocessable Content");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Resource {Kind}/{Id} of project {ProjectId} could not be read.", handle.Reference.Kind, handle.Reference.Id, handle.ProjectId);
            return Reply.Empty(500, "Internal Server Error");
        }
    }

    internal enum RangeKind
    {
        None,
        Range,
        Unsatisfiable,
    }

    /// <summary>One byte range, "bytes=a-b", "bytes=a-" or "bytes=-n"; anything else is ignored and the whole file is sent.</summary>
    internal static RangeKind ParseRange(string? header, long length, out long first, out long last)
    {
        first = 0;
        last = length - 1;
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeKind.None;
        }
        var spec = header.AsSpan(6).Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0 || spec.Contains(','))
        {
            return RangeKind.None;
        }
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        const NumberStyles digits = NumberStyles.None;
        if (left.IsEmpty)
        {
            if (!long.TryParse(right, digits, CultureInfo.InvariantCulture, out var suffix))
            {
                return RangeKind.None;
            }
            if (suffix == 0 || length == 0)
            {
                return RangeKind.Unsatisfiable;
            }
            first = Math.Max(0, length - suffix);
            return RangeKind.Range;
        }
        if (!long.TryParse(left, digits, CultureInfo.InvariantCulture, out first))
        {
            return RangeKind.None;
        }
        if (!right.IsEmpty)
        {
            if (!long.TryParse(right, digits, CultureInfo.InvariantCulture, out last) || last < first)
            {
                return RangeKind.None;
            }
            last = Math.Min(last, length - 1);
        }
        return first >= length ? RangeKind.Unsatisfiable : RangeKind.Range;
    }

    private static string? Header(CoreWebView2HttpRequestHeaders headers, string name)
        => headers.Contains(name) ? headers.GetHeader(name) : null;

    private static CoreWebView2WebResourceResponse Create(CoreWebView2 web, Reply reply)
    {
        var headers = new List<string>(reply.Headers)
        {
            "Access-Control-Allow-Origin: " + WebViewPolicy.UiOrigin,
            "Access-Control-Expose-Headers: Content-Range, Content-Length, Accept-Ranges",
            "X-Content-Type-Options: nosniff",
        };
        if (reply.Status >= 400)
        {
            headers.Add("Cache-Control: no-store");
        }
        return web.Environment.CreateWebResourceResponse(reply.Body, reply.Status, reply.Reason, string.Join("\r\n", headers));
    }

    private sealed record Reply(int Status, string Reason, IReadOnlyList<string> Headers, Stream? Body)
    {
        internal static Reply Empty(int status, string reason, params string[] headers) => new(status, reason, headers, null);

        internal static Reply Preflight() => new(204, "No Content",
            ["Access-Control-Allow-Methods: GET, HEAD", "Access-Control-Allow-Headers: Range", "Access-Control-Max-Age: 600"], null);
    }
}
