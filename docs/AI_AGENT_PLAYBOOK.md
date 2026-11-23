# AI Agent Playbook (`melomg`)

Minimal-token execution rules for extracting figures/diagrams from PDFs and feeding them into `mby`.

## Conventions

1. **Discover lazily**: Run `melomg prime` once. Query `melomg spec <topic...>` only when needed (`melomg spec extract batch`).
2. **Use Anomaly-Only Visual QA**: Run `melomg ingest <pdf> --retain-db --offline --quiet` and render ONLY `suspectPages` (`melomg render <docId> <p> --overlay`) to audit sequence gaps, duplicates, or unnumbered detections instead of rendering every page.
3. **Emit Agent Manifests**: Run `melomg extract <docId> all -o ./assets --quality high --manifest --quiet` to produce `assets/manifest.json` (`num`, `page`, `caption`, `confidence`, `path`) ready for direct insertion into `mby` Markdown/JSON.
4. **One-shot for known numbers**: When you already know the figure number from PDF text, run `melomg extract ./paper.pdf 2.1 -o ./assets --quality high` in a single call with zero workspace clutter.
5. **Default semantic filenames**: Crops save as `fig_2.1.png` (collision-safe) for clean `imagePath` wiring in `mby`.

## Recipes

### 1. Ingest -> Inspect Vocabulary -> Targeted Crop -> `mby` Build
```bash
export MBY_AGENT=1
melomg ingest ./biology_ch2.pdf --retain-db --offline
melomg extract Doc_2026... 2.1 2.4 -o ./assets --quality high
```

### 2. One-Shot Number Extraction (Ephemeral)
```bash
MBY_AGENT=1 melomg extract ./biology_ch2.pdf 2.1 -o ./assets --format jpg --quality 88
```

### 3. Batch Ingest + Patch + Extract (`$0.id#2.1`)
```bash
MBY_AGENT=1 melomg batch - << 'EOF'
[
  { "op": "ingest", "pdf": "./chapter2.pdf", "retainDb": true, "offline": true },
  { "op": "patch", "addr": "$0.id#2.1", "set": { "caption": "Fig. 2.1: Bilayer" } },
  { "op": "extract", "addr": "$0.id", "selectors": ["2.1", "2.4"], "out": "./assets", "quality": "high" }
]
EOF
```
