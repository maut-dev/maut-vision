namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Stable key of a job kind, for example <c>images.import</c>: lowercase, segments separated by dots.
/// The key is stored in the job; changing it is an incompatible change (old jobs get status Blocked).
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class JobKindAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
