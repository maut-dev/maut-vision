# Project library in the UI

The library (`components/project-library.tsx`) is the first screen after startup: the project list, creation, settings, archive, export,
import and deletion. The application always starts in the library; the last opened project comes first with a "last opened" mark
(`lastProjectId` from `projects.list`, kept by Core).

```ts
const library = useProjects();   // projects.list, then project.changed and project.removed
const transfers = useProjectTransfers(library.projects, loaded, projectId => openIfInLibrary(projectId));
```

## A list driven by events

The list is read once and then changes only by events: creation, change and import arrive as `project.changed`, deletion as
`project.removed`. A response older than an already received event does not roll the revision back. An open project that disappears or is
archived returns the screen to the library — the same happens when the project is deleted elsewhere.

## Opening a project

Each opening carries a transition number `sequence`, and the last transition wins. The number grows from the clock
(`Math.max(Date.now(), last + 1)`): the host remembers the largest number, and a counter starting from zero after a page reload would lose
to it. The response of a superseded opening — success or `projects.open_superseded` — is dropped without a message.

```ts
const sequence = nextOpenSequence();
const opened = await projectsApi.open({ projectId, sequence });
if (sequence !== openSequence.current) return;   // another transition started later
```

Opening does not block navigation: the "Open" button of the project being opened shows progress, the others stay available. Any other
transition (library, base models, a section) also increments the number, and a late response no longer changes the screen. The unsaved
changes guard stays on all transitions.

## Export and import

"Export…" is available for every row, archived ones included; "Import project…" is in the header. Hints use the `title` attribute. A closed
file dialog starts nothing.

- Export is the project job `projects.export`: progress and result by `job.changed`, cancellation with `jobs.cancel`.
- Import is a background operation: progress and result by `projectImport.changed`, cancellation with `projects.cancelImport`.

Each transfer is a banner: title, file counter (bytes too for import), a hint that large projects may take minutes while other projects
stay usable, a progress bar and "Cancel"; after the outcome — the result or refusal text and "Hide". A snapshot is applied only if its
`sequence` is greater than the known one: snapshots that arrive before the start response are not lost. There is no polling.

After a page reload a running import is taken from `projects.listImports`, a running export from `jobs.listActive` of projects (projects
without prepared storage answer with a refusal and have no export). Operations finished before the reload are not shown again.

An import finished while the page watches opens the project by itself if the user is in the library and has not started anything there
(editor, dialog, opening). An import found already finished after a reload does not open the project.

## Deletion

"Delete…" opens the project deletion dialog. The dialog first reads `projects.reviewDeletion`: running jobs and the live check are listed,
and the button becomes "cancel jobs and delete" (`cancelJobs: true`). A running export makes deletion unavailable.

Deletion passes the review revision. A refusal stays in the dialog, and "check again" rereads the content:

- the revision changed — the project changed after the confirmation was opened;
- jobs appeared after the review — background jobs are running.

If some files are locked (`cleanupPending`), the library reports that the project is deleted and cleanup will be repeated at the next
start.
