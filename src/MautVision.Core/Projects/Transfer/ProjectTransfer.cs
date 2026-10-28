using MautVision.Core.Interfaces;

namespace MautVision.Core.Projects.Transfer;

/// <summary>Refusals shared by the export admission (Core composition) and its preparation.</summary>
internal static class ProjectTransfer
{
    /// <summary>
    /// The project has other work: <c>projects.transfer_busy</c> with Details["section"] — the area of the first active job or check
    /// (the part of its kind before the dot: images, backgrounds, classes, generation, datasets, training, checks).
    /// </summary>
    internal static CoreException Busy(string kind) => new("projects.transfer_busy", $"The project has active work ({kind}); it cannot be exported now.")
    {
        Details = new Dictionary<string, string> { ["section"] = kind.Split('.')[0] },
    };

    /// <summary>The project is being exported: nothing else of it starts until the export ends.</summary>
    internal static CoreException Active(string projectId) => new("projects.transfer_active", $"Project {projectId} is being exported.");
}
