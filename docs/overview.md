# Maut Vision architecture

Core holds the business logic. Desktop starts the application and connects Core to the React UI through WebView2. Python runs
ML work as a separate process.

```text
React → API → WebView2 → Transport → Controllers → ICore
React ← events ← WebView2 ← Transport ← Events ← Core
AI client → MCP (127.0.0.1) → Mcp tools → Controllers → ICore
Core → Python worker (training, checks) · ffmpeg (video frames)
```

## Core

- [Core and initialization](Core/architecture.md)
- [Projects](Core/projects.md), [project storage](Core/storage.md), [catalog: variants and classes](Core/catalog.md),
  [images and annotation](Core/images.md), [backgrounds](Core/backgrounds.md), [generation](Core/generation.md),
  [datasets](Core/datasets.md), [training and model versions](Core/training.md), [model checks](Core/checks.md)
- [Base models](Core/base-models.md), [jobs and long operations](Core/jobs.md), [hardware](Core/hardware.md), [errors](Core/errors.md)
- [Python worker](Python/worker.md)

## Desktop

- [Desktop host](Desktop/architecture.md), [API and transport](Desktop/transport.md), [events](Desktop/events.md),
  [resource channel](Desktop/resources.md), [MCP server for AI clients](Desktop/mcp.md)
- [Directories, packaging and installer](Desktop/appdata-and-packaging.md)

## UI

- [Navigation](UI/navigation.md), [project library](UI/library.md), [viewer, annotation and gallery](UI/images.md),
  [backgrounds](UI/backgrounds.md), [generation](UI/generation.md), [datasets](UI/datasets.md), [training](UI/training.md),
  [models](UI/models.md), [checks](UI/checks.md), [overview](UI/overview.md), [analytics](UI/analytics.md),
  [localization](UI/localization.md)

## Code

- [Code style](Code/style.md), [review and testing](Code/review-and-testing.md), [logging](Code/logging.md),
  [Git: commits, branches and releases](Code/git.md)

A Russian overview is in [ru/README.md](ru/README.md).

The documentation describes stable boundaries and rules. The exact set of methods, fields and features is defined by the source
code; the documents do not repeat it.
