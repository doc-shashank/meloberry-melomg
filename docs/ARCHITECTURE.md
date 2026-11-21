# `melomg` Architecture

Technical layout of Clean Architecture layers, DI bindings, persistence tables, and sidecar schemas.

---

## 1. Clean Architecture Layering

| Project | Dependencies | Responsibilities |
| :--- | :--- | :--- |
| `Melomg.Domain` | None | Aggregates (`PdfDocument`, `DocumentFigure`, `FigureLabelMask`, `BoundingBox`), domain errors, ordering rules (`Exchange` / `Shift`) |
| `Melomg.Application` | `Melomg.Domain`, `Microsoft.Extensions.DependencyInjection.Abstractions` | Command/query handlers, ports (`IIdGenerator`, `IDocumentWorkspaceRepository`, `IPdfIngestionEngine`, `IOnnxRuntimeManager`, `IFigureImageExtractor`), lint use cases |
| `Melomg.Infrastructure` | `Melomg.Application`, `Microsoft.Data.Sqlite`, `Microsoft.ML.OnnxRuntime.Managed` | SQLite persistence, pure-C# + WinRT PDF renderer, `NativeOnnxRuntimeSessionHost`, `OnnxRuntimeManager`, PNG/JPEG encoders, obfuscator, cross-process `.melomg_id_clock` |
| `Melomg.Cli` | `Melomg.Application`, `Melomg.Infrastructure` (composition root only) | 23 Command descriptors, tokenizer/dispatcher, REPL loop, Source-Generated JSON (`MelomgCliJsonContext`), embedded docs |

Architectural boundaries are enforced by `Melomg.ArchitectureTests`: `Domain` has zero dependencies, `Application` has zero Infrastructure/CLI/SQLite/Console references, and CLI commands cannot call `System.Console`, `Sqlite`, or `Melomg.Infrastructure` directly.

---

## 2. Persistence & Output Artifacts

### Document SQLite Database (`Doc_<d/t>.db`)

| Table | Columns / Purpose |
| :--- | :--- |
| `DocumentMeta` | `Id`, `Title`, `SourcePdfPath`, `StoredPdfPath`, `Sha256Hash`, `ModelName`, `ConfidenceThreshold`, `IngestedAtUtc`, `ActiveFigureId`, `WorkspaceMode`, `RetainDatabase` |
| `DocumentPages` | `PageNumber`, `WidthPts`, `HeightPts`, `RotationDegrees` |
| `DocumentFigures` | `Id`, `IndexOrder`, `PageNumber`, `Category`, `Label`, `Caption`, `X`, `Y`, `Width`, `Height`, `CaptionX`, `CaptionY`, `CaptionWidth`, `CaptionHeight`, `Confidence`, `Source`, `PageFigureIndex`, `LastExtractedPath` |
| `DocumentFigureMasks` | `Id`, `FigureId`, `IndexOrder`, `X`, `Y`, `Width`, `Height`, `LabelText`, `Style`, `ColorHex` |

### AppData State (`melomg.db` / Settings DB & Files)

- `DocumentRegistry`: Tracks registered workspace paths (`listdocs` prunes stale entries, performs separator-boundary directory filtering, and tolerates corrupt `.db` files).
- `ModelSettings` & `FigureEntityIndex`: Active ONNX model path, class schema, thresholds, and `Fig_*` -> `Doc_*` lookup index.
- `.melomg_id_clock`: File-locked UTC millisecond counter shared across short-lived processes.
- `runtime/onnxruntime/win-x64/1.20.1/manifest.json`: Tracks installed runtime DLL lengths and SHA-256 digests.

### Extracted Figure Sidecar (`.meta.json` when `--meta` passed)

Contains `figureId`, `index`, `pageNumber`, `category`, `label`, `caption`, cropped PDF bounds, output `widthPx`/`heightPx`, `dpi`, `format`, `appliedMasks[]`, and `engineSource`.
