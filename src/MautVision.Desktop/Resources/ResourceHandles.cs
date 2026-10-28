using System.Buffers.Text;
using System.Security.Cryptography;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Transport;

namespace MautVision.Desktop.Resources;

/// <summary>Content — the bytes of a transient resource (a preview that is not a project file); null for material files.</summary>
internal sealed record ResourceHandle(string ProjectId, ResourceReference Reference, ResourceVariant Variant, DateTimeOffset ExpiresAt,
    TransientContent? Content = null);

/// <summary>An encoded image held in memory for one page session, such as the preview of a chosen file.</summary>
internal sealed record TransientContent(byte[] Bytes, string ContentType, DateTimeOffset CreatedAt);

/// <summary>
/// Issues resource URLs for DTOs: <c>https://maut-resource.invalid/r/&lt;token&gt;</c>. A token is 128 random bits and means
/// nothing by itself; it stays valid for <see cref="Lifetime"/> and only in the page session that received it — a navigation
/// or reload of the UI and closing the window invalidate all tokens. The registry is bounded: past <see cref="Capacity"/>
/// the oldest token is dropped and the UI asks for a fresh DTO. Thread-safe; controllers call <see cref="Issue"/> from any thread.
/// </summary>
internal sealed class ResourceHandles(TimeProvider? time = null)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal const int Capacity = 20_000;

    /// <summary>Bytes of transient resources kept at once; past it the oldest previews are dropped.</summary>
    internal const long TransientCapacity = 96L * 1024 * 1024;
    internal const string PathPrefix = "/r/";
    private const int TokenBytes = 16;

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, ResourceHandle> byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceHandle, string> byResource = new(ResourceComparer.Instance);
    // Issue order is expiry order because every token lives for the same time.
    private readonly Queue<string> order = new();
    // Transient resources in issue order, for the byte bound.
    private readonly Queue<string> transientOrder = new();
    private long transientBytes;
    private bool closed;

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return byToken.Count;
            }
        }
    }

    /// <summary>
    /// URL of a material file for the current page. Repeated calls within half the lifetime return the same URL,
    /// so the WebView can reuse an image it already holds.
    /// </summary>
    internal string Issue(string projectId, ResourceReference reference, ResourceVariant variant)
    {
        if (!Guid.TryParseExact(projectId, "D", out var project) || project == Guid.Empty)
        {
            throw new ArgumentException("Project ID must be a canonical GUID.", nameof(projectId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Kind, nameof(reference));
        if (!Enum.IsDefined(variant))
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        var now = clock.GetUtcNow();
        var handle = new ResourceHandle(projectId, reference, variant, now + Lifetime);
        lock (gate)
        {
            RemoveExpired(now);
            if (!closed && byResource.TryGetValue(handle, out var existing) && byToken[existing].ExpiresAt - now > Lifetime / 2)
            {
                return Url(existing);
            }
            var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
            if (closed)
            {
                // A late DTO after the window closed gets a URL that resolves to nothing.
                return Url(token);
            }
            while (byToken.Count >= Capacity)
            {
                Remove(order.Dequeue());
            }
            byToken.Add(token, handle);
            byResource[handle] = token;
            order.Enqueue(token);
            return Url(token);
        }
    }

    /// <summary>
    /// URL of encoded bytes that are not a project file, such as the preview of a chosen file or a sample of a run. Each call
    /// gives a new URL; the bytes live as long as the token, in memory, within <see cref="TransientCapacity"/>.
    /// </summary>
    internal string IssueTransient(string projectId, byte[] content, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.LongLength > TransientCapacity) throw new ArgumentException("Transient content is too large.", nameof(content));
        var now = clock.GetUtcNow();
        // A unique reference keeps transient handles out of the reuse of material URLs.
        var handle = new ResourceHandle(projectId, new ResourceReference("transient", Guid.NewGuid()), ResourceVariant.Original, now + Lifetime,
            new TransientContent(content, contentType, now));
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        lock (gate)
        {
            RemoveExpired(now);
            if (closed) return Url(token);
            while (byToken.Count >= Capacity) Remove(order.Dequeue());
            while (transientBytes + content.LongLength > TransientCapacity && transientOrder.TryDequeue(out var oldest)) Remove(oldest);
            byToken.Add(token, handle);
            order.Enqueue(token);
            transientOrder.Enqueue(token);
            transientBytes += content.LongLength;
            return Url(token);
        }
    }

    internal bool TryResolve(string token, out ResourceHandle handle)
    {
        lock (gate)
        {
            if (byToken.TryGetValue(token, out handle!) && handle.ExpiresAt > clock.GetUtcNow())
            {
                return true;
            }
            handle = null!;
            return false;
        }
    }

    /// <summary>The UI document is being replaced: tokens issued to the previous page stop working.</summary>
    internal void ResetSession()
    {
        lock (gate)
        {
            byToken.Clear();
            byResource.Clear();
            order.Clear();
            transientOrder.Clear();
            transientBytes = 0;
        }
    }

    /// <summary>The project was deleted: its URLs stop working (the order queues skip removed tokens).</summary>
    internal void RevokeProject(string projectId)
    {
        lock (gate)
        {
            foreach (var token in byToken.Where(entry => entry.Value.ProjectId == projectId).Select(entry => entry.Key).ToArray()) Remove(token);
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            byToken.Clear();
            byResource.Clear();
            order.Clear();
            transientOrder.Clear();
            transientBytes = 0;
        }
    }

    internal static bool IsToken(string value) => value.Length == 22 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Url(string token) => WebViewPolicy.ResourceOrigin + PathPrefix + token;

    private void RemoveExpired(DateTimeOffset now)
    {
        while (order.TryPeek(out var token) && (!byToken.TryGetValue(token, out var handle) || handle.ExpiresAt <= now))
        {
            Remove(order.Dequeue());
        }
        while (transientOrder.TryPeek(out var token) && !byToken.ContainsKey(token))
        {
            transientOrder.Dequeue();
        }
    }

    private void Remove(string token)
    {
        if (!byToken.Remove(token, out var handle)) return;
        if (handle.Content is { } content) transientBytes -= content.Bytes.LongLength;
        else if (byResource.TryGetValue(handle, out var current) && current == token) byResource.Remove(handle);
    }

    /// <summary>Same project, material and variant; the expiry is not part of the identity.</summary>
    private sealed class ResourceComparer : IEqualityComparer<ResourceHandle>
    {
        internal static readonly ResourceComparer Instance = new();

        public bool Equals(ResourceHandle? left, ResourceHandle? right) => left is not null && right is not null
            && left.ProjectId == right.ProjectId && left.Reference == right.Reference && left.Variant == right.Variant;

        public int GetHashCode(ResourceHandle handle) => HashCode.Combine(handle.ProjectId, handle.Reference, handle.Variant);
    }
}
