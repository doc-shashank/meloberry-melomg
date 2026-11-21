# `melomg`

Reference CLI and Clean Architecture library for PDF ingestion, hybrid ONNX figure/label detection, PNG/JPEG page-crop extraction, label obfuscation, and AppData-hosted ONNX Runtime management. Built as a .NET 10 NativeAOT CLI binary.

## Documentation

| Topic (`melomg docs <topic>`) | File | Contents |
| :--- | :--- | :--- |
| `overview` | `README.md` | Boot modes, ID generation, global options, exit codes |
| `commands` | [COMMANDS.md](COMMANDS.md) | Reference for all 23 commands |
| `onnx` | [ONNX_AND_DETECTION.md](ONNX_AND_DETECTION.md) | ONNX runtime install/verification, YOLO decoding, hybrid PDF/visual fallback |
| `agent` | [AI_AGENT_PLAYBOOK.md](AI_AGENT_PLAYBOOK.md) | Non-interactive automation patterns, `--json`, `--yes`, `--` |
| `architecture` | [ARCHITECTURE.md](ARCHITECTURE.md) | Layer boundaries, schemas, storage layout |

---

## Boot Modes & Argument Handling

1. **Interactive REPL (`melomg` or `melomg repl`)**: Persistent `melomg > ` shell preserving `ActiveDocumentId` and `ActiveFigureId`.
2. **Single Command (`melomg <command> [args...]`)**: Executes one command and exits cleanly.
3. **Fluent Chain (`melomg "<cmd1> -> <cmd2> -> ..."`)**: Executes stages sequentially, carrying newly created `Doc_*` / `Fig_*` IDs and leading global options forward.

### Global Flags & Quoting

| Flag | Effect |
| :--- | :--- |
| `--doc <docID>` | Target a `Doc_<d/t>` workspace (takes precedence over active REPL context) |
| `--figure <figID>` | Target a `Fig_<d/t>` figure (takes precedence over active REPL context) |
| `--model <name\|path>` | Override active ONNX model for detection |
| `--app-data <dir>` | Override AppData directory (honored in top-level args, chains, and REPL) |
| `--yes` | Auto-confirm `[y/N]` deletion/uninstall prompts |
| `--json` | Emit JSON envelope on stdout/stderr; route interactive prompts to stderr |
| `--silent` | Suppress non-error stdout output |
| `--help` | Show command reference |
| `--` | End of options; treat all subsequent tokens as literal values |

Unknown `--*` tokens after the command verb are treated as literal positional values; place `--` before literal values matching recognized flag names.

---

## Entity IDs & Workspace Modes

IDs are formatted as UTC millisecond stamps persisted via a locked clock file (`.melomg_id_clock` inside `--app-data`) so rapid consecutive CLI runs never collide:
- `Doc_<YYYYMMDD_HHMMSS_fff>`: PDF document workspace
- `Fig_<YYYYMMDD_HHMMSS_fff>`: Detected figure/chart/table/diagram/formula

| Mode | Ingest Source Storage | Database Location |
| :--- | :--- | :--- |
| `standard` (`ingest`) | References original PDF path without copying | `<workingDir>/Doc_<d/t>/Doc_<d/t>.db` (auto-discarded on `extract` unless `--retain-db`) |
| `project` (`open`) | Moves PDF into `<projectDir>` (or `./project_<pdfName>/`) | `<projectDir>/Doc_<d/t>.db` (always retained across extractions) |

## Exit Codes

`0` Success | `1` General Error | `2` Validation / Bounds / Lint Failure | `3` Not Found | `4` Conflict | `5` Unauthorized
