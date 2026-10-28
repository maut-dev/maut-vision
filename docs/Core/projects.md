# Projects

`ProjectsManager` holds the project logic. A project is a composition of child managers: each is exposed as a property of an
interface type and is an ordinary class with methods.

```csharp
public interface IProject
{
    IImagesManager Images { get; }
    IClassesManager Classes { get; }
}

internal sealed class Project : IProject
{
    internal Project(ProjectsManager owner, ProjectMetadata metadata, ProjectStore store, ILogger logger)
    {
        Images = new ImagesManager(store, logger);
        Classes = new ClassesManager(store, logger);
    }
}
```

- The project creates its child managers and gives them what they need: the [project storage](storage.md), files, the logger and
  contracts of other Core managers.
- Child managers never reference each other in both directions. A scenario that touches several managers is coordinated by a
  [job](jobs.md).
- There are no DDD layers: no aggregates, per-entity repositories, use-case or command classes, or domain event infrastructure.

Consumers work through interfaces; concrete fields and operations are defined by the code.

The placement rule is fixed: `projects/<projectId>/project.json`; material data is in `project.db` and `files/` next to it
([storage](storage.md)).

## Instance and changes

The manager keeps one instance per project; listing, getting and opening return it. After loading, the instance is the source of
truth: changes go through its methods, are written to project.json and are announced by the `ProjectChanged` event.

```csharp
var project = await core.Projects.CreateAsync(new ProjectSettings("Detector", "", "yolo26n", new InputSize(640, 640)));
await project.UpdateAsync(settings with { Name = "New name" }, expectedRevision: project.Revision);
await project.SetArchivedAsync(true);
```

Every saved change increments `Revision`. A settings change passes the revision the user saw; on mismatch it is rejected with
`projects.revision_conflict` instead of overwriting someone else's edit. An archived project cannot be changed until it is restored.

## Settings that materials depend on

A project stores the selected base model and the input size. The input size defines the size of backgrounds, generated frames and
datasets, and the model defines the initial training weights; therefore both sides of the size are multiples of the model stride. A
new or changed model must be installed; the project keeps its previous model even if its weights are later removed.

Projects depends on the BaseModels contract in one direction, to check a model and its stride. BaseModels knows nothing about
projects.

## Storage

project.json is the project descriptor for the library; materials are stored separately in the [project storage](storage.md), which
is opened only when the project is opened. project.json contains `formatVersion`. Reading rejects an unknown version and extra
fields, so a format change comes with a new version and an explicit conversion. Writing goes through a temporary file in the project
folder and a rename. A project with damaged metadata does not hide the others: the list skips it, and opening reports an error.

## Last project and the order of openings

`OpenAsync` is a switch to a project. The last started switch is remembered in `last-project.json` of the work directory and stops
live checks of other projects. A slow opening (a long schema upgrade) that finishes after a newer one prepares the project but is not
a switch: it does not become the last one and does not raise `ProjectOpened`.

```csharp
var slow = core.Projects.OpenAsync(a);           // schema upgrade
await core.Projects.OpenAsync(b);                // the last one is b
await slow;                                      // a is ready, b is still the last one
var last = await core.Projects.GetLastProjectIdAsync();
```

The application always starts in the library: the last project is only marked. Deleting a project forgets it.

## Export and import

Export is the project job `projects.export`; import is a background operation of ProjectsManager, like base model installation
(an immediate snapshot, `ImportChanged` events, cancellation, not kept between runs).

```csharp
var input = await core.Projects.PrepareExportAsync(projectId, destination, operationId);
var job = await core.Jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(input, projectId, operationId, input.ProjectName);
var import = await core.Projects.StartImportAsync(new ProjectArchiveSource(path, length, lastWriteTimeUtc), operationId);
```

The archive is a ZIP with Zip64: `manifest.json` (`format: "maut-vision-sharp"`, `formatVersion: 1`), `project.json`, a snapshot of
`project.db` (`VACUUM INTO`, without the row of the export itself) and `files/**`. Images and weights are stored uncompressed, the
database and JSON are deflated. Not included: `cache/`, `files/generation-runs/`, working folders of training and checks, and names
starting with a dot. Files with a shared file ID (dataset frames are hard links to images) are stored once; the other paths are
written to the manifest as `links` and restored as hard links on import, or as copies where links are impossible.

- **Export neither interferes nor mixes.** It is queued only when the project has no other active jobs and no live check
  (`projects.transfer_busy`, the area in details); while it runs, new jobs and checks of the project are rejected
  (`projects.transfer_active`) and file deletion of materials is postponed. The admission check of JobsManager configured by the
  Core composition does this, so the decision and the enqueue are atomic.
- **Parallel reads, sequential writes.** Several threads read small files whole into pooled buffers and compute CRC-32 (in
  hardware); one thread writes the ZIP without seeking; large files are streamed. Sizes and file IDs come from the directory
  enumeration (`FolderScan`), files are not reopened. The archive is written to a temporary `.<guid>.tmp` next to the target and
  renamed at the end.
- **Import validates the whole archive before writing:** format and version, no encryption, store and deflate methods only, no
  duplicate names or paths outside the folder, no overlapping entries or sizes deflate cannot produce, enough free space. An archive
  of an unsupported early format (the manifest has `tables`) is rejected with `projects.archive_legacy`.
- **Extraction is parallel and reversible.** Threads read the archive positionally through one handle and write to
  `projects/.import-<guid>/`; the CRC of each entry is checked. Then `quick_check` and `user_version` of the database (newer than the
  application — `projects.storage_newer`), a new id on collision, the suffix « (импорт)», « (импорт 2)» on a name collision, and one
  rename to `projects/<id>`. Cancellation and failure remove the staging folder.

## Deletion

```csharp
var review = await core.Projects.ReviewDeletionAsync(projectId);         // running jobs and the live check
var result = await core.Projects.DeleteAsync(projectId, review.Revision, cancelJobs: true);
```

Deletion cancels the jobs and the live check of the project and waits for them to stop (without `cancelJobs` while work runs —
`projects.busy`), closes the storage (releasing the connection pool), renames `project.json` to `project.deleted` — from this moment
the project is deleted — then renames the folder to `projects/.deleted-<id>-<guid>` and removes it in the background. Deletion is
refused while the project is being exported. If a file is locked, the folder or the marker stays and cleanup repeats after the next
start: `CleanUpLeftoversAsync` looks only at names in `projects/` and is called by the host after the library is shown.
