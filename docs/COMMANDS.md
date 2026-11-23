# `melomg` Command Reference

All commands are strictly verb-first, stateless, and non-interactive. Pass `-` for `<file|->` to read JSON from `stdin`.

| Command | Syntax | Description |
| :--- | :--- | :--- |
| `prime` | `melomg prime` *(or `melomg` / `melomg --help`)* | Print compact AI-agent grammar card |
| `spec` | `melomg spec [topic...]` | Micro-specs for `extract`, `patch`, `mask`, `batch`, `vocab`, `onnx`, `errors` |
| `ingest` | `melomg ingest <pdf\|dir\|docId> [-o dir] [--project] [--refresh] [--confidence n] [--model p] [--title t] [--offline] [--retain-db] [--quiet] [--verbose]` | Ingest PDF (defaults DB to `.meloberry/melomg/` unless `-o`/`--project`); returns `vocab`, `anomalies`, `suspectPages`, and `outline` |
| `get` | `melomg get <addr> [--view outline\|full\|ids] [--page n] [--category figure\|diagram\|chart] [--min-conf n]` | Inspect document or figure (`outline` default) |
| `ls` | `melomg ls [dir]` | List discovered workspaces (scans CWD and `.meloberry/melomg/`) |
| `extract` | `melomg extract <addr\|pdf> [nums\|all...] [-o dir] [--format png\|jpg] [--dpi 72..600] [--quality q] [--padding n] [--naming number\|id] [--include-caption] [--mask-all] [--mask-match pat] [--mask-style s] [--mask-color hex] [--no-masks] [--meta] [--manifest] [--keep-overlay] [--retain-db] [--offline] [--quiet] [--verbose]` | Crop image/diagram figure(s) to PNG/JPG, optionally writing `manifest.json` |
| `patch` | `melomg patch <addr> [file\|-] [--set k=v]... [--move idx:shift\|exchange]` | Update doc `title` or figure `bbox`, `caption`, `label`, `num`, `category`, or order |
| `add` | `melomg add <docId> <p> <x> <y> <w> <h> [--category figure\|diagram\|chart] [--num n] [--caption t] [--label t] [--index i]`<br>`melomg add <docId> <file\|->` | Manually register an image, diagram, or chart box |
| `mask` | `melomg mask <figAddr> <ls\|add\|auto\|rm\|clear> [args...]` | Manage label redaction masks on a figure |
| `render` | `melomg render <docId> <page\|all> [-o dir] [--format png\|jpg] [--dpi n] [--quality q] [--overlay]` | Render full PDF pages for QA inspection |
| `rm` | `melomg rm <addr>` | Delete document workspace or figure immediately |
| `check` | `melomg check <addr> [--quiet] [--verbose]` | Verify PDF SHA-256, page bounds, extracted image headers, and sequence/duplicate anomalies |
| `batch` | `melomg batch <file\|->` | Execute JSON op array sequentially with `$0.id` / `$prev.id` interpolation |
| `model` | `melomg model <info\|set\|threshold\|reset> [args...]` | Inspect, set, tune thresholds, or reset ONNX model |
| `runtime` | `melomg runtime <status\|install\|uninstall> [--provider directml\|cpu] [--skip-model] [--keep-model] [--force]` | Manage AppData ONNX Runtime DLLs and DocLayout-YOLO weights |
| `version` | `melomg version` *(or `-v` / `--version`)* | Print CLI version and .NET runtime |
