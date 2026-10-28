# Overview in the UI

The "Overview" section (`components/overview/`) is the first screen of an open project: project settings, project data, running work, the
latest models and "Your computer". The section is recreated when the project changes.

```tsx
const data = useOverview(projectId);              // overview.get + job.changed, datasets.changed, training.changed with versions
const { sample, failed } = useHardwareSample();   // hardware.sample every ~2 s after the answer, while mounted and visible
```

## Project data without polling

Project data comes with one `overview.get` and is updated only by events; there is no refresh button. A snapshot of a running job from the
list is applied in place by `sequence`; a new or finished job, changed datasets and versions are a reason to reread the overview. Events
within 200 ms are merged, one read runs at a time, and rereading keeps the number of loaded job pages.

```ts
if (known && isActive(job) && known.job.status === job.status) patch(job);   // progress in place
else schedule();                                                             // the list composition changed
```

## Running work

Active jobs are `queued` and `running` in queue order, in pages of 30. A row is `{kind} · {label}` and the state: cancelling, queued or
waiting for the GPU, the job progress text, otherwise "running · processed / total unit".

The map "kind → label, section, unit" is kept in the UI (`lib/overview/active-job-view.ts`): Core knows nothing about sections. "Open" goes to
the section of the kind with an intent: training — to "Current run" of its run (`runId`), evaluation — to "Checks → Dataset", model export —
to the version passport (the number from the "Version N" label: an export job has no version ID before its result). A kind without a
section (project export is visible in the library) is shown without "Open".

## Your computer

The only polling of the section is the hardware, and only while the section is mounted and the window is visible; a minimized window stops
the chain ([Hardware](../Core/hardware.md)). A missing value is shown as unavailable; the host names the reason for a missing GPU and for the
first CPU sample. GB with up to 2 decimals, percentages with up to 1.

A small chart of CPU and GPU load over the last ~2 minutes is built from the snapshots the page received (`lib/overview/load-history.ts`):
the history lives while the section is open, and a gap of more than 6 s between samples breaks the line.
