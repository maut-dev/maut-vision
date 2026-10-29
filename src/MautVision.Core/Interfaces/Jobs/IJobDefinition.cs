namespace MautVision.Core.Interfaces.Jobs;

/// <summary>
/// Contract of a job kind. The derived interface is marked with <see cref="JobKindAttribute"/>: the kind key
/// is stored in project.db, so renaming the interface does not lose saved jobs.
/// </summary>
public interface IJobDefinition<TInput>
{
}
