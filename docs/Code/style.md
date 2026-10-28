# Code style

Clarity, encapsulation, KISS and DRY matter more than the number of layers. Every object has an explicit responsibility;
dependencies point to Core contracts.

```text
Core/Interfaces/<aspect>/ — public contracts
Core/<aspect>/            — internal implementations
Desktop/Controllers/      — API adapters
```

Folders and namespaces follow the dependency levels: child managers of a project live in `Projects/<Aspect>` and
`Interfaces/Projects/<Aspect>` (in Desktop — `Controllers/Projects/<Aspect>`, in the UI — `api/projects/<aspect>`); parts shared by
all of Core live at the Core level.

C# and TypeScript/TSX use 4 spaces. C# uses file-scoped namespaces, PascalCase for types and methods, camelCase for parameters,
and the opening brace on a new line. Asynchronous methods end with `Async`; calls are awaited, never blocked with `Result` or
`Wait` on the UI thread.

New abstractions are added for a concrete responsibility. An interface does not expose internal storage or mutable collections
without need. Resource ownership and the meaning of cancellation must be clear from the contract.

Improve names and structure first; comments explain non-obvious reasons and constraints. Documentation keeps stable rules and short
examples, not lists of methods and properties.

Hot paths (pixel loops, hashing, comparison of signatures) use hardware acceleration where it pays off — `System.Numerics.Vector`,
AVX2/SSE intrinsics, hardware CRC — always with a scalar fallback that also serves as the reference in checks, and with a
measurement that justifies the change.

PowerShell scripts with non-ASCII text are saved as UTF-8 with BOM for Windows PowerShell 5.1. Startup scripts are checked in that
shell; the .NET APIs they use must exist in its runtime.

The application UI and its messages are in Russian for now; user-facing texts are chosen in Desktop by error code and in the UI
components. Identifiers, new code comments, commit messages and the main documentation are in English.
