# AI Agent Playbook (`melomg`)

Deterministic patterns for automating PDF figure detection, mask redaction, and extraction.

---

## 1. Execution Rules

1. **Always pass `--json`**: Parses structured envelopes (`{ "success": true, "data": ... }` on stdout; errors on stderr).
2. **Use explicit target flags to avoid REPL leakage**: `--figure` and `--doc` take priority over background session context on `extract` and `lint`.
3. **Pass `--yes` with `delete` and `runtime uninstall`**: Interactive `[y/N]` prompts write to `stderr` under `--json`, and missing `--yes` will wait for stdin or abort.
4. **Delimit literal titles/regexes starting with dashes using `--`**: Prevent argument flags from colliding with document titles or text patterns.
5. **Provision runtime once via `runtime install`**: In offline environments pass `--offline` so missing runtimes fall back directly to structural PDF detection without HTTP retries.

---

## 2. Deterministic Workflows

### Check or Provision ONNX Runtime

```bash
melomg --json --app-data ./state runtime status
melomg --json --app-data ./state runtime install --provider cpu --yes
```

### Ingest & Extract All Figures + Metadata Sidecars

```bash
melomg --json --app-data ./state "ingest ./paper.pdf ./work --title 'Neuroscience Notes' -> extract all ./figures --format png --dpi 300 --meta"
```

### Safe Dash-Prefixed Titles & Auto Label Redaction

```bash
melomg --json --app-data ./state Doc_2026... settitle -- "-- Section 4 Figures"
melomg --json --app-data ./state Fig_2026... "mask auto --match '^(Fig|Table)\\.' --style blur -> extract ./redacted --meta"
```

### Set & Inspect a Custom YOLO Model

```bash
melomg --json --app-data ./state model set ./custom_yolov8.onnx --confidence 0.40 --iou 0.45
melomg --json --app-data ./state model info
melomg --json --app-data ./state ingest ./paper.pdf ./work --model ./custom_yolov8.onnx
```

### Clean Teardown

```bash
melomg --json --yes --app-data ./state Doc_2026... delete
```
