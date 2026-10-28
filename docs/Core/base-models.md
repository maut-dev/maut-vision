# Base models

`BaseModels` is responsible for the base models of the application. A consumer gets a model contract; its internal representation stays
in the implementation.

```text
consumer → BaseModels interface → hidden implementation
```

The public contract contains only the information and actions a consumer needs. File paths, the catalog format and data access details do
not become part of the external API automatically.

## Catalog and instances

The catalog pins the download address, size and SHA-256 of each model. It ships with the application: Core gets its path in
`CoreOptions.BaseModelCatalogPath` and reads it in place, and keeps the weights in `BaseModels/weights` of the work directory. The manager
loads the catalog once and creates one instance per model; listing and lookup return those instances, and installation, verification and
removal change their state. Changes are announced by the manager event `ModelChanged`.

```csharp
var model = await core.BaseModels.GetOrNullAsync("yolo26n");
core.BaseModels.ModelChanged += (_, args) => Show(args.Model.Status, args.Model.Progress);
await model!.InstallAsync(cancellationToken);
```

## Installation

Installation starts only on a user action; nothing is downloaded at application start. A repeated call for the same model joins the
running installation; an installed model is not downloaded again.

The file is downloaded to a temporary file in the weights folder and published by a rename only after its size and SHA-256 are checked. A
damaged copy is replaced only by a verified new one. Temporary files left after a crash are removed when the catalog loads. A download is
aborted if no data arrives for more than a minute; there is no overall time limit.

When the catalog loads, the state is determined by the file size without reading the content. The full hash check is
`VerifyIntegrityAsync`; the consumer calls it before using the weights, for example before passing them to Python.

An installation error is not thrown from `InstallAsync`: the model goes to Failed with an `ErrorCode` following the
[error model](errors.md). Progress events are throttled.

## API

Desktop starts the installation and returns the current state immediately; the UI receives progress and the result with the
`baseModel.changed` event and requests the list when the screen opens. Cancellation and removal are separate commands. The installation
belongs to the model, not to the request or the window.
