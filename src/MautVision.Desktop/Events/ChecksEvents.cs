using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Desktop.Controllers.Projects.Checks;
using MautVision.Desktop.Resources;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Events;

/// <summary>
/// Forwards check changes as checks.changed: evaluations with new revisions and the snapshot of a live check (Core coalesces frames to
/// at most five snapshots a second). A new preview gets a transient resource URL once; snapshots reuse it until the next preview.
/// Live checks it saw running are stopped when the page session ends (reload, navigation, closing the window): nobody observes them.
/// </summary>
internal sealed class ChecksEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly ResourceHandles resources;
    private readonly Action<string, object> publish;
    private readonly Func<string, string> messageFor;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, (long Number, string Url)> previews = new();
    private readonly Dictionary<Guid, string> running = new();
    private int disposed;

    internal ChecksEvents(IProjectsManager projects, ResourceHandles resources, Action<string, object> publish, Func<string, string> messageFor, ILogger logger)
    {
        this.projects = projects;
        this.resources = resources;
        this.publish = publish;
        this.messageFor = messageFor;
        this.logger = logger;
        projects.ChecksChanged += OnChecksChanged;
    }

    /// <summary>The page is being replaced: its preview URLs stop working and the checks it ran are stopped.</summary>
    internal void EndSession()
    {
        KeyValuePair<Guid, string>[] stopping;
        lock (gate)
        {
            previews.Clear();
            stopping = running.ToArray();
        }
        foreach (var (checkId, projectId) in stopping) _ = StopAsync(projectId, checkId);
    }

    private async Task StopAsync(string projectId, Guid checkId)
    {
        try
        {
            await (await projects.GetAsync(projectId)).Checks.StopCheckAsync(checkId, LiveCheckStopReason.SessionEnded);
        }
        catch (Exception error)
        {
            // The check may have finished and been forgotten meanwhile.
            logger.LogWarning(error, "Live check {CheckId} of project {ProjectId} was not stopped at the end of the page session.", checkId, projectId);
        }
    }

    private void OnChecksChanged(object? sender, ChecksChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Controllers.Projects.Checks.ResponseDto.LiveCheckResponseDto? check = null;
        if (args.Check is { } snapshot)
        {
            string? url = null;
            lock (gate)
            {
                if (snapshot.State is LiveCheckState.Preparing or LiveCheckState.Running or LiveCheckState.Cancelling) running[snapshot.Id] = args.ProjectId;
                else running.Remove(snapshot.Id);
                if (args.Preview is { } preview)
                {
                    if (previews.TryGetValue(snapshot.Id, out var issued) && issued.Number == preview.Info.Number)
                    {
                        url = issued.Url;
                    }
                    else
                    {
                        url = resources.IssueTransient(args.ProjectId, preview.Content, preview.ContentType);
                        previews[snapshot.Id] = (preview.Info.Number, url);
                    }
                }
            }
            check = ChecksResponseMapper.Map(snapshot with { Preview = args.Preview?.Info }, url, messageFor, ChecksResponseMapper.EventDetections);
        }
        publish("checks.changed", ChecksResponseMapper.Map(args, check));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        projects.ChecksChanged -= OnChecksChanged;
    }
}
