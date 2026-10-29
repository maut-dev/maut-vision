# Errors

An error the user must hear about has a stable code. The code is part of the contract between Core, Desktop, UI and Python; the
text belongs to the UI catalogs and may change, the code may not.

```text
Core: exception with a code and details → Desktop: { code, details } → UI: text of the code in the current language, reaction by code
Python: result.error.code → Core: a Core code
```

## Codes

A code has the form `<area>.<reason>` in lowercase snake_case: `projects.not_found`, `projects.name_invalid`,
`base_models.not_installed`, `jobs.gpu_busy`. The area matches the Core block. Renaming or removing a code is an incompatible change
of C# and TypeScript.

The `bridge.` prefix is reserved for Transport: a malformed message, an unknown method, a request cancelled when the window closes.
Domain errors never use `bridge.` and never turn into a generic `bridge.validation`.

## Core

Core throws an exception with a code and, when the UI needs something to rely on, details — strings by key — for situations the user
can understand or fix: not found, invalid data, state conflict, resource busy, damaged data.

```csharp
throw new CoreException("generation.invalid_recipe", "The object scale is above 0.")
{
    Details = new Dictionary<string, string> { ["field"] = "scale" },
};
```

`ArgumentException` and `InvalidOperationException` mean a bug in the calling code and are handled as internal errors. The message of
a Core exception is written for the log, not for the user. Details contain only serializable values that are safe to show:
identifiers, names, limits. Absolute paths, stack traces and file contents never go into details.

## Desktop and UI

Desktop turns an exception into a response `{ code, details }` and adds no text. The UI chooses the text by code from its catalogs
([localization](../UI/localization.md)), substitutes the details (`{{datasetWidth}}`) and reacts to the code, not to the text: for
example, on a state conflict it requests fresh data. A code without a text in the catalogs shows the generic text.

```ts
catch (cause) {
    setError(errorMessage(cause));   // errorText(cause.code, cause.details) for a BridgeError
}
```

Every code Core can throw has a text in `ui/src/locales/<language>/errors.json`; `npm run check-locales` finds the codes in the C#
sources and fails when one has no text.

An unexpected exception gives `bridge.internal_error`. The full exception, the method and the requestId are written to the
[log](../Code/logging.md); the stack never goes into the response or the UI.

## Long operations and Python

A job error is not thrown from the enqueue request: the job ends with status failed and a stored error `{ code, message }`, which the
UI receives in snapshots and events and translates by code; the stored message is for the log and is shown only for a code the catalogs
do not know. A training run or check that failed in the worker also carries
`workerMessage` for its `*.worker_<code>` codes: the worker's message key with values and its English message as the fallback
([worker](../Python/worker.md#errors-and-warnings)). Worker error codes (`dataset_invalid`, `out_of_memory`, `architecture_mismatch`) are
translated by Core into its own codes; raw stderr is never shown to the user and stays in the job log.
