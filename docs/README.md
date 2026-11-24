# `melomg`

Stateless, AI-agent-first PDF image/diagram crop extractor complementing the Meloberry CLI (`mby`). Automatically detects each PDF's figure caption vocabulary (`Fig.` vs `Figure` vs `Diagram`), lets caller agents target figures by number alone (`#2.1`, `2.1 2.4`), and strictly outputs page-rendered PNG/JPEG crops with adjustable quality.

## Guarantees

- **Images & Diagrams Only**: Supports ONLY `figure`, `diagram`, and `chart`. Tables, borderless `booktabs` tables, formulas, and shaded/bordered callout boxes cannot be represented, stored, or extracted.
- **Crop-Only Extraction**: Never dumps raw embedded `/Image` streams; always raster-renders PDF page regions via `Windows.Data.Pdf` with configurable `--quality` and `--dpi`.
- **Stateless, Non-Interactive & Safe**: Bare `melomg` prints the `prime` grammar card and exits `0`. `rm` removes only melomg workspace artifacts (`Doc_*.db`, WAL/SHM sidecars, `.qa_overlays`, generated `page_*` / `fig_*` / `Fig_*` / `manifest.json` files, and empty directories) and never deletes source PDFs or unrelated user files.
- **Strict JSON & CLI Contracts**: `--json` keeps `stdout` strictly for valid success envelopes (`{"success":true,"data":...}`)—including `<cmd> --help --json`—and routes all errors (including failed `check --json` with `error.details`) to `stderr` (`{"success":false,"error":...}`). Cross-command flags, unknown single-dash flags, and wrong-typed `batch` JSON fields fail fast with exit `2`.

## Docs

- [COMMANDS.md](COMMANDS.md) — Consolidated 13-verb + setup command reference
- [AI_AGENT_PLAYBOOK.md](AI_AGENT_PLAYBOOK.md) — Minimal-token agent workflows & `mby` handoff
- [ONNX_AND_DETECTION.md](ONNX_AND_DETECTION.md) — ONNX Runtime, strict allowlist decoding, and table/box exclusion
- [ARCHITECTURE.md](ARCHITECTURE.md) — Clean Architecture layers, SQLite schema, and storage layout

## Entity IDs & `#` Addressing

| Pattern | Resolves To | Example |
| :--- | :--- | :--- |
| `Doc_<YYYYMMDD_HHMMSS_fff>` | PDF document workspace | `Doc_20261122_015000_001` |
| `Fig_<YYYYMMDD_HHMMSS_fff>` | Direct figure entity | `Fig_20261122_015000_002` |
| `<docId>#<num>` | Figure matched by vocabulary-agnostic number | `Doc_...#2.1` |
| `<docId>#f<n>` | 1-based figure index | `Doc_...#f3` |
| `<docId>#p<n>` | All figures on page `n` | `Doc_...#p4` |
| `<docId>#n:<raw>` | Escaped literal number | `Doc_...#n:p2` |

## Global Flags & Environment

| Flag / Env | Effect |
| :--- | :--- |
| `--json` | Single-line minified JSON on stdout/stderr (omits `null` fields) |
| `MELOMG_AGENT=1` / `MBY_AGENT=1` | Implicitly enables `--json` |
| `--app-data <dir>` / `MELOMG_APP_DATA=<dir>` | Override AppData settings/runtime/ID clock directory |
| `-o`, `--out <dir>` | Output directory |
| `--model <onnxPath>` | Override ONNX model path |
| `--quiet` / `--verbose` | Omit bulky figure lists on `ingest`/`extract`/`check` vs include full geometry |
| `--silent` | Suppress non-error human stdout output |
| `--` | End of options |

## Exit Codes

`0` Success | `1` General/IO Error | `2` Validation/JSON/Lint Error | `3` Not Found | `4` Conflict (`Figure.AmbiguousNumber`, `Project.AlreadyExists`, `Project.PdfConflict`) | `5` Unauthorized
