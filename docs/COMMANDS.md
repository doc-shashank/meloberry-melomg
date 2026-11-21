# `melomg` Command Reference

Complete reference for the 23 registered CLI commands.

---

## 1. PDF Ingestion & Workspaces

| Command | Syntax | Notes |
| :--- | :--- | :--- |
| `ingest` | `ingest <pdfPath> [workingDir] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline] [--retain-db]` | Parses PDF structure (inherited `/Rotate`, `/CropBox`, `/ObjStm` bounds, octal escapes), detects figures/captions, writes `Doc_<d/t>/Doc_<d/t>.db` (auto-discarded on `extract` unless `--retain-db`) |
| `open` | `open <pdfPath\|projectDir> [projectDir] [--confidence <0.05..0.99>] [--model <onnxPath>] [--title <title>] [--offline]` | Opens a PDF as a persistent project (moving PDF into `[projectDir]` or `./project_<PDF name>/`, rolling back if DB creation fails) or re-activates an existing project directory |
| `usedoc` | `[<docID>] usedoc [docID]` | Activates an existing `Doc_<d/t>` workspace (also accepts `--doc <docID>`) |
| `listdocs` | `listdocs [directory]` | Lists registered workspaces and discovers portable folders (prunes missing DB entries; separator-safe; tolerates corrupt/unrelated `.db` files) |
| `info` | `[<docID>] info` | Displays PDF metadata, SHA-256, page dimensions, ONNX model used, and figure breakdown |
| `settitle` | `[<docID>] settitle <title>` | Updates workspace title |
| `reanalyze` | `[<docID>] reanalyze [--confidence <0.05..0.99>] [--model <onnxPath>] [--offline]` | Re-runs Hybrid ONNX detection on the stored PDF and refreshes detected figures |

---

## 2. Figure Inspection & Refinement

| Command | Syntax | Notes |
| :--- | :--- | :--- |
| `figures` | `[<docID>] figures [--page <n>] [--category <type>] [--min-confidence <0..1>]` | Lists detected/manual figures (`Figure`, `Chart`, `Diagram`, `Table`, `Formula`) |
| `inspect` | `[<figID>] inspect` | Prints geometry, normalized box, caption association, saved masks, and detected text labels |
| `addfigure` | `[<docID>] addfigure <page> <x> <y> <width> <height> [--category <cat>] [--caption <text>] [--label <text>] [index]` | Manually registers a figure box (in PDF points) at `[index]` or appends |
| `setbbox` | `[<figID>] setbbox <x> <y> <width> <height>` | Updates figure bounding box (in PDF points) within page dimensions |
| `setcaption` | `[<figID>] setcaption <text>` | Overrides or assigns figure caption |
| `setlabel` | `[<figID>] setlabel <label>` | Sets or updates short label (e.g., `Figure 2.1`) |
| `setcategory` | `[<figID>] setcategory <figure\|chart\|diagram\|table\|formula>` | Sets figure classification category |
| `setindex` | `[<figID>] setindex <number> <exchange\|shift>` | Reorders 1-based figure index via swap or shift |
| `mask` | `[<figID>] mask <list\|add\|auto\|remove\|clear> [args...]` | `list`, `add <x> <y> <w> <h> [--label <text>] [--style <solid\|blur\|pixelate\|whiteout\|crosshatch>] [--color <#RRGGBB>]`, `auto [--match <patterns>] [--style ...] [--color ...]`, `remove <maskIndex>`, `clear` |

---

## 3. Extraction & Page Rendering

| Command | Syntax | Notes |
| :--- | :--- | :--- |
| `extract` | `[<docID\|figID>] extract [all\|<index>\|<figID>\|page <n>] [outputDir] [--format <png\|jpg>] [--dpi <72..600>] [--quality <1..100\|low\|medium\|high\|max>] [--padding <0..100>] [--include-caption] [--obfuscate-labels] [--obfuscate-label <patterns>] [--obfuscate-style <solid\|blur\|pixelate\|whiteout\|crosshatch>] [--obfuscate-color <#RRGGBB>] [--no-masks] [--meta] [--keep-overlay] [--retain-db]` | Extracts figure crops as PNG/JPEG, supports literal substrings, globs (`*`, `?`), or regexes (250 ms timeout) on `--obfuscate-label`/`--match`, validates PNG/JPEG magic bytes, writes optional `.meta.json`, and cleans up tracked QA overlays unless `--keep-overlay` |
| `render` | `[<docID>] render <pageNum\|all> [outputDir] [--format <png\|jpg>] [--dpi <72..600>] [--quality <1..100\|low\|medium\|high\|max>] [--overlay]` | Renders full PDF pages (`page_001.png` or `page_001_overlay.png` with blue figure boxes, emerald dashed captions, crimson hatched masks) |

---

## 4. ONNX Models, Runtime & Diagnostics

| Command | Syntax | Notes |
| :--- | :--- | :--- |
| `model` | `model <info\|set\|threshold\|reset> [args...]` | `info`, `set <onnxPath> [--confidence <0.05..0.99>] [--iou <0.10..0.95>]`, `threshold <confidence> [iou]`, `reset` |
| `runtime` | `runtime <status\|install\|uninstall> [--provider <directml\|cpu>] [--skip-model] [--keep-model] [--force] [--yes]` | Inspects, installs/rolls back with SHA-256 + length + PE checks, or uninstalls AppData ONNX Runtime DLLs |
| `delete` | `<docID\|figID> delete [--yes]` | Prints impact `WARNING` to `stderr`, prompts `[y/N]` (`stderr` under `--json`), deletes entity (confined to dedicated workspace dirs) and shifts trailing indices |
| `lint` | `[<docID\|figID>] lint` | Verifies stored PDF SHA-256 integrity, bounding boxes, and extracted image headers |
| `docs` / `help` | `docs [topic] [--all]` / `help [topic]` | Displays embedded Markdown guides (`overview`, `commands`, `onnx`, `agent`, `architecture`) or command reference |
