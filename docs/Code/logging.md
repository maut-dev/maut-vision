# Logging

The log exists to reconstruct what happened from the user's files. The user sees a short message; details, stack traces and
identifiers are in the log.

```text
Core / Desktop  → ILogger<T> → application log file
UI              → system.log (limited) → the same log
Python worker   → stderr → job log in the job folder
```

## Sources

Core writes through the `Microsoft.Extensions.Logging` abstractions and does not depend on a concrete sink; Desktop configures the
sink and levels at startup. `Trace` and `Console` are not used for diagnostics: their output is lost in the shipped application.

Desktop catches and records unhandled exceptions: `AppDomain.UnhandledException`, `Application.DispatcherUnhandledException`,
`TaskScheduler.UnobservedTaskException`, and WebView2 process failures (`CoreWebView2.ProcessFailed`). The UI sends unhandled render
errors and rejected promises to the host with `system.log` (level error or warning, message up to 2,000 characters, stack up to
8,000, at most 20 records per minute, the rest is dropped); ordinary request errors are already logged by the host.

An error on the UI thread after the main window is open shows a message and does not close the application; before the main window
opens, startup ends with a message. A failure of the WebView2 render process reloads the page; a failure of the browser process
shows a message about restarting.

Python writes diagnostics only to stderr; Core keeps it in the job log. The application log receives the worker start, exit code,
duration, error code and the last stderr lines on failure.

## Content

Each record has UTC time, level, category (the source type) and message; an exception is written in full. Related records are joined
by identifiers: `requestId` for a bridge request, `jobId` and `operationId` for a job, `projectId` for a project.

Recorded: application start and version, the work directory, startup stages with the time since process start (used by the
[performance measurement](review-and-testing.md#performance)), WebView2 and worker versions, errors with exceptions, job state
transitions, process start and exit. Not recorded: image and file contents, full request payloads, previews, tokens and other
secrets. Strings from external data are truncated to a reasonable length.

Levels: Error — the operation failed; Warning — done with a deviation or recovery; Information — significant lifecycle events;
Debug — details for development, off in the shipped build.

## Storage

Log files are in the work directory under `logs/`, one file per run with rotation by size; total size and age are limited. Logging
never blocks the UI thread, and a logging failure never crashes the application.

```text
logs/maut-vision-<yyyyMMdd-HHmmss UTC>-<pid>.log
```

One file is up to 10 MB; at most 20 files, 50 MB in total and no older than 30 days are kept; old files are removed at startup.
Records go through a background queue and are flushed when the application exits.
