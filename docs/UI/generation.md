# Generation in the UI

The "Generation" section (`components/generation/`) has the tabs "Presets (N)" and "Runs (N)". The presets tab shows the form on the left
and the run parameters and preset cards on the right; the runs tab shows run cards. Both tabs stay mounted: the form survives switching,
and run cards receive events while hidden. Data comes from the [generation API](../Core/generation.md), without polling.

```tsx
const recipes = useGenerationRecipes(projectId);   // listRecipes + generation.changed (revisions, null — deleted)
const runs = useGenerationRuns(projectId);         // listRuns + job.changed (kind generation.run, sequence) + generation.changed.runs
const samples = useGenerationSamples(projectId);   // sample frames; a new request cancels the previous one
```

## Preset form

One form serves a new preset, an edit and a repeat of a run; the mode is `RecipeFormValues.mode`. Pure logic is in
`lib/generation/recipe-form.ts`: form values → `GenerationRecipeFormDto` (`toRecipeDto`), share rows (`variantShares`), "split evenly"
(integer shares summing to 100, the first variants get the remainder), background set compatibility. Validation before sending is
`recipeFieldError` from the API (the same rules as in the host); a `generation.invalid_recipe` response highlights the field from
`error.details.field`.

```ts
const dto = toRecipeDto(form, workspace, project.inputSize);
const invalid = formFieldError(dto);         // save and sample frames are unavailable
```

- The frame size of a new preset is the project input; an edit or a repeat uses the size of the preset or run.
- A background set of another size is shown but unavailable, with an explanation; an archived or deleted set is visible only if already
  selected.
- Shares are a field and a slider per project variant; a variant without bindings with images is shown with a hint to add images and a
  0 share. A sum other than 100 % is highlighted, and saving is unavailable.
- Repeating a run takes shares and bindings from its snapshot until the user chooses to edit variants by the current sources.

## Sample frames and the run

"Sample frames" renders up to 4 first frames of the run with the seed from the run parameters — the same pixels and boxes the run
produces. Boxes are SVG over the thumbnail, coloured by class variant (the image palette); a frame opens in the shared viewer. A sample is
marked stale when settings or seed change (`sampleKey`).

The run is `generation.start` with a new `operationId`; the run card appears from the response, and progress, result and error arrive with
`job.changed`. Indicators come from `lib/generation/run-state.ts`: the job result wins over the stored run counters.

## Opening images

"Open images" passes a navigation intent through the App screen state: `{ kind: "project", section: "images", groupId }`. The images
section selects that group on mount and moves focus to it; a transition from the sidebar carries no intent. The page address does not
change ([navigation](navigation.md)).

```tsx
<GenerationSection project={project} openImages={groupId => navigate({ kind: "project", projectId, section: "images", groupId })} />
```

## Accessibility

The deletion dialog and the viewer return focus to the opening element (`useReturnFocus`). "Edit" and "configure repeat" move focus to the
name field; saving or cancelling an edit returns it to the card's "Edit".
