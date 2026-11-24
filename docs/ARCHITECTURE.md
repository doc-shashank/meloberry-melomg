# `melomg` Architecture

4-layer Clean Architecture layout, persistence schemas, and dependency rules.

## 1. Clean Architecture Layering

| Project | Dependencies | Responsibilities |
| :--- | :--- | :--- |
| `Melomg.Domain` | None | `PdfDocument`, `DocumentFigure` (`FigureCategory`: `Figure \| Diagram \| Chart` ONLY), `DocumentVocabulary`, `FigureNumberNormalizer`, `FigureLabelMask`, `BoundingBox`, domain errors |
| `Melomg.Application` | `Melomg.Domain`, `Microsoft.Extensions.DependencyInjection.Abstractions` | `AddressResolver` (`#`-addressing), `IngestPdfHandler`, `GetTargetHandler`, `ExtractFiguresHandler` (including temp-isolated one-shot PDF mode), `PatchTargetHandler`, mask/lint/runtime handlers |
| `Melomg.Infrastructure` | `Melomg.Application`, `Microsoft.Data.Sqlite`, `Microsoft.ML.OnnxRuntime.Managed` | SQLite persistence, WinRT PDF crop renderer (`WindowsPdfPageRenderer`), `HybridOnnxPdfIngestionEngine`, `NativeOnnxRuntimeSessionHost`, PNG/JPEG encoders, `.melomg_id_clock` |
| `Melomg.Cli` | `Melomg.Application`, `Melomg.Infrastructure` (composition root only) | Stateless verb-first CLI commands (`prime`, `spec`, `ingest`, `get`, `ls`, `extract`, `patch`, `add`, `mask`, `render`, `rm`, `check`, `batch`, `model`, `runtime`, `version`), source-generated minified `MelomgCliJsonContext` |

Enforced by `Melomg.ArchitectureTests`: `Domain` has zero dependencies and strictly allows only image/diagram categories (`Figure`, `Diagram`, `Chart`); `Application` has zero Infrastructure/CLI/SQLite/Console references; CLI commands never touch `System.Console`, `Sqlite`, or `Melomg.Infrastructure` directly.

## 2. Persistence & Output Artifacts

### Document SQLite Database (`Doc_<d/t>.db`)

- `DocumentMeta`: `Id`, `Title`, `SourcePdfPath`, `StoredPdfPath`, `Sha256Hash`, `ModelName`, `ConfidenceThreshold`, `IngestedAtUtc`, `ActiveFigureId`, `WorkspaceMode`, `RetainDatabase`, `VocabPrimaryPrefix`, `VocabPrefixesJson`, `VocabNumberingStyle`
- `DocumentPages`: `PageNumber`, `WidthPts`, `HeightPts`, `RotationDegrees`
- `DocumentFigures`: `Id`, `SortIndex`, `PageNumber`, `PageFigureIndex`, `Category`, `BboxX..H`, `Confidence`, `Caption`, `Label`, `Source`, `LastExtractedPath`, `CaptionBboxX..H`, `VocabPrefix`, `FigureNumber`, `NormalizedNumber`
- `DocumentFigureMasks`: `FigureId`, `MaskIndex`, `BboxX..H`, `LabelText`, `Style`, `ColorHex`

Legacy DB rows with removed categories (`table`, `formula`) are skipped cleanly on rehydration, and remaining figures are re-indexed contiguously (`1..N`) with cleared `FigureNumber` values preserved across reloads.

## 3. Concurrency, Cleanup & Integrity Guarantees

- **SQLite Concurrency**: Settings and workspace SQLite connections set `Default Timeout=5` and `PRAGMA busy_timeout = 5000`; write connections enable `PRAGMA journal_mode = WAL`. Directory discovery ignores inaccessible subdirectories (`IgnoreInaccessible = true`).
- **Cross-Process Monotonic IDs**: `MonotonicTimestampIdGenerator` coordinates `.melomg_id_clock` access via a per-clock Windows named `Mutex` (`Local\MelomgIdClock_<sha256>`) with bounded exclusive file-lock retries.
- **Safe Workspace Cleanup**:
  - `rm` deletes only melomg-owned artifacts (`Doc_*.db`, `-wal`/`-shm`, `.qa_overlays`, generated `page_*` / `fig_*` / `Fig_*` / `manifest.json`) and removes directories only when empty.
  - QA overlay cleanup (`QaOverlayManifestHelper`) removes only verified `*_overlay.{png,jpg}` files and only deletes empty directories strictly inside the workspace root (never external user directories).
  - Standard `ingest` and one-shot `extract --retain-db` clean up uncommitted workspace folders on failure so zero orphaned `Doc_*` directories remain.
- **Refresh Snapshot Integrity**: `ingest --refresh` updates `Sha256Hash`, `Pages`, and `IngestedAtUtc` alongside refreshed figure detections so subsequent `check` runs validate against the updated PDF.
