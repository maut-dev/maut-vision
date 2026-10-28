# Events

Core reports changes through events. Desktop adapts them for the UI; notifications may arrive without a frontend request.

```text
Core event → Desktop/Events → Transport → WebView2 → UI subscription
```

Core knows nothing about the WebView2 message format. Events converts data to DTOs, and sending happens on the UI thread. A subscription
belongs to a concrete window or component and is removed when it ends.

A change of a C# property does not update React by itself. The UI gets the initial state through the API, then applies events; after
reconnecting it requests the current state again.

An event carries a full snapshot of the object. If the snapshot has a change counter, the UI applies only a newer one: `job.changed`
contains `sequence`, and a snapshot with a smaller `sequence` arriving after the `jobs.get` response is dropped. Frequent changes are
throttled by Core, not by Events.

```text
checks.changed { projectId, evaluations: [{ id, revision }], check: live check snapshot | null }
project.removed { projectId }
projectImport.changed { import snapshot with sequence }
```

Creating and importing a project arrive as `project.changed` with a full snapshot; deletion is `project.removed` with one id: a deleted
project has no snapshot.

A live check sends its snapshot without the journal at most 5 times per second (frames in between are merged) and state transitions
immediately; the UI reads new journal lines with a request after the last record it saw. Preview bytes are not part of the event: Events
issues a temporary resource channel URL for a new preview once and repeats it until the preview changes. When the page is replaced
(reload, window close), Events stops the live checks it saw running: nobody is left to watch them.
