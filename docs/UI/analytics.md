# Analytics in the UI

The "Analytics" section (`components/analytics/`) compares selected model versions of the project: metrics and parameters, training curves
by epoch, and dataset evaluations. It uses the same hooks as "Models" and "Checks", without polling.

```tsx
const models = useModelVersions(projectId);          // training.changed with versions
const evaluations = useModelEvaluations(projectId);  // checks.changed, job.changed
<ModelEvaluations versions={versions} compared={selected} versionId={selected[0]?.id} … />
```

## Choosing versions

By default the 3 latest versions are selected. The choice is kept per project in `localStorage` (`maut.analytics.selection.<projectId>`):
it is a convenience of one window, not project data, so reading and writing are wrapped in try/catch, and without storage the section works
with the default choice. Deleted versions leave the selection. There is no limit on the number of versions; the version colour is
`--series-N` by its number and repeats after 8.

## Table

Rows and explanations are in `lib/analytics/analytics-view.ts`. For quality metrics the best value of a row is highlighted with
`--metric-best`, the others show the gap "−0.012"; there is no best if there are fewer than two numbers or they are equal (`bestOf`). A
column header opens the version passport in "Models". "Copy table" copies TSV of the same texts without the gaps.

## Curves

One metric for all selected versions on the shared `TrainingEpochChart`: hover, legend, `thinPoints` thinning. The metric list contains only
keys present in the history, the standard metrics first and the others alphabetically. The epoch history is read once per version
(`readEpochHistory`, `lib/training/epoch-history.ts`) while the section is open: it never changes after the version is created.

```ts
const epochs = await readEpochHistory(projectId, version.runId, () => mounted.current);
```
