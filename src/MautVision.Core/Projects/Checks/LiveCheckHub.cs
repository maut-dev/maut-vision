using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Jobs;
using MautVision.Core.Screen;

namespace MautVision.Core.Projects.Checks;

/// <summary>What checks of every project share: the GPU slot of the queue, the screen and the registry of running live checks.</summary>
internal sealed record CheckServices(GpuSlot Gpu, ScreenManager Screen, LiveCheckHub Hub);

/// <summary>
/// Running live checks of the application (at most one: a check holds the GPU slot). The Core composition stops a check when another
/// project is opened; the host stops it when its page session ends.
/// </summary>
internal sealed class LiveCheckHub
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, LiveCheckSession> running = new();

    /// <summary>
    /// Set once by the Core composition: refuses a new check of a project with a CoreException (the project is exported or deleted).
    /// </summary>
    internal Action<string>? Admission { get; set; }

    internal void Admit(string projectId) => Admission?.Invoke(projectId);

    internal void Add(LiveCheckSession session)
    {
        lock (gate) running.Add(session.Id, session);
    }

    internal void Remove(LiveCheckSession session)
    {
        lock (gate) running.Remove(session.Id);
    }

    internal IReadOnlyList<LiveCheckSession> Running()
    {
        lock (gate) return running.Values.ToArray();
    }

    /// <summary>Stops the checks of other projects: the user switched to projectId.</summary>
    internal void StopOutside(string projectId)
    {
        foreach (var session in Running().Where(session => session.ProjectId != projectId)) session.Stop(LiveCheckStopReason.ProjectSwitched);
    }

    /// <summary>Stops the checks of a project and waits until their workers ended (the project is deleted).</summary>
    internal async Task StopProjectAsync(string projectId, LiveCheckStopReason reason)
    {
        var sessions = Running().Where(session => session.ProjectId == projectId).ToArray();
        foreach (var session in sessions) session.Stop(reason);
        await Task.WhenAll(sessions.Select(session => session.Completion)).ConfigureAwait(false);
    }

    /// <summary>Stops every check and waits until their workers ended (the application ends).</summary>
    internal async Task StopAllAsync(LiveCheckStopReason reason)
    {
        var sessions = Running();
        foreach (var session in sessions) session.Stop(reason);
        await Task.WhenAll(sessions.Select(session => session.Completion)).ConfigureAwait(false);
    }
}
