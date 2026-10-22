# TIA Portal MCP Server

Connect Siemens **TIA Portal V20** to any MCP-compatible AI assistant (DeepSeek Harness, Claude Desktop, Claude Code, VS Code, Cursor, Codex…). Import SCL blocks, manage PLC tag tables, compile, export blocks, and download to a real PLC — all from natural language.

A single, self-contained executable: `bin/TiaMcpServer.exe` (~66 KB). No installers, no Python, no Node — just Windows' built-in .NET Framework 4.8 and a licensed TIA Portal V20 with Openness.

📖 [中文说明](README.zh-CN.md) · 🎬 [演示](docs/DEMO.zh-CN.md) · 📦 [下载 exe](../../releases/latest)

---

## Features

- **Import programs** — paste SCL source and it generates FB/FC/OB/DB/UDT blocks (Chinese identifiers and comments are preserved).
- **Manage tag tables** — bulk create/update PLC tags (`tia_upsert_tags`); tables are created automatically.
- **Compile** with per-block error/warning details and file paths.
- **Read back / export** any block as SCL or XML.
- **Projects** — open/create/save/close, list devices and blocks, add a PLC from the hardware catalog.
- **Download to PLC** — guarded by an explicit `confirm=true` flag (stops the CPU).
- **Diagnostics** — connection state, process/session/group info, stale-portal cleanup.

## Why HTTP instead of stdio?

MCP clients that spawn child processes (notably DeepSeek Harness) run those children inside a sandbox that can see only a handful of processes — it can never reach TIA Portal. So the server runs **independently** on the host and clients connect over **Streamable HTTP**:

```
TIA Portal V20 ──Openness──> TiaMcpServer.exe (http://127.0.0.1:8756/mcp) <── any MCP client
```

## Requirements

- Windows 10/11 (uses .NET Framework 4.8, already built in)
- Siemens TIA Portal **V20** with the **Openness** component installed
- Your Windows account must be in the **`Siemens TIA Openness`** group (add it, then log off/on)
- The exe must run **as administrator** (it self-elevates via UAC when double-clicked)
- Put the exe in a **writable folder** (it writes a log and its own temp workspace next to itself)

## Quick start

1. Double-click `bin\TiaMcpServer.exe`, approve the UAC prompt.
   A console shows `listening on http://127.0.0.1:8756/mcp`.
2. Point your MCP client at the endpoint. Example for DeepSeek Harness
   (`~/.dsh/profiles/desktop/cordis.patch.yml`):

```yaml
- insert:
    - id: mcp-tia
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: tia
        transport: streamable-http
        url: http://127.0.0.1:8756/mcp
        toolCallTimeoutMs: 950000
        failOnStartupError: false
```

For Claude Desktop, add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "tia": { "type": "http", "url": "http://127.0.0.1:8756/mcp" }
  }
}
```

3. Ask your assistant, for example: *"Open project D:\Projects\Line1\Line1.ap20, import this SCL into PLC_1, and compile."*

### Autostart at logon (Windows)

- `install.cmd` — creates a Startup-folder shortcut with the *run as administrator* flag.
- `uninstall.cmd` — removes it.

At logon Windows shows one UAC prompt; approve it and the server keeps running hidden in the background.

## Command line

| Command | Purpose |
|---|---|
| `TiaMcpServer.exe` | Start the HTTP server on port 8756 (default) |
| `TiaMcpServer.exe --port 9000` | Use a different port |
| `TiaMcpServer.exe --status` | Print connection state as JSON |
| `TiaMcpServer.exe --selftest [dir]` | End-to-end self test (creates a demo project) |
| `TiaMcpServer.exe --help` | Show help |

## Tools

| Tool | What it does |
|---|---|
| `tia_status`, `tia_env` | connection state / diagnostics |
| `tia_open_project`, `tia_create_project`, `tia_close_project`, `tia_save_project` | projects |
| `tia_list_devices`, `tia_list_blocks` | devices and blocks |
| `tia_import_scl` | **import SCL** → generate blocks (auto-saves) |
| `tia_export_block` | export a block as SCL or XML |
| `tia_delete_block` | delete a block and save |
| `tia_compile` | compile, with per-message errors/warnings |
| `tia_list_tag_tables`, `tia_read_tags`, `tia_upsert_tags` | tag tables / **bulk import** |
| `tia_download` | download to a real PLC (needs `confirm=true`) |
| `tia_add_plc`, `tia_launch_ui`, `tia_kill_portals` | add device / launch UI / clear stale portals |

## Building from source

No SDK required — it compiles with the .NET Framework compiler that ships with Windows:

```
build.cmd
```

`build.cmd`/`build.ps1` locate `Siemens.Engineering.dll` from the TIA Openness installation
automatically (registry → `TiaPortalLocation` → common paths).

## Architecture / source map

| File | Responsibility |
|---|---|
| `Program.cs` | entry point, CLI, UAC self-elevation, temp redirection, assembly resolution |
| `Protocol.cs` | MCP JSON-RPC, tool schema + dispatch |
| `Engine.cs` | all Openness calls |
| `HttpTransport.cs` | localhost HTTP/1.1 (keep-alive, byte-accurate body parsing) |
| `TiaLocator.cs` | locate the TIA Portal installation |
| `Json.cs`, `AssemblyInfo.cs` | JSON helpers / version info |

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Connection to TiaPortal failed.` (after ~8 s) | Not elevated. Run as administrator. |
| `EngineeringSecurityException: This process is not permitted to connect to TIA-Portal` | Launching from Task Scheduler (its token fails Openness' group check). Use manual/UAC start or the autostart shortcut, not a scheduled task. |
| `%TEMP%\<guid>\System` access denied at startup | Security software blocks elevated writes to the user temp. The server already redirects its temp to `bin\runtime-temp`; if it persists, move the exe to a writable folder. |
| `fetch failed` from the MCP client on Chinese paths | Fixed in this build; upgrade. |
| Openness sees no processes | You must be in the `Siemens TIA Openness` group and re-logon after joining. |

Logs are written next to the exe (`tia-mcp.log`).

## License

MIT — see [LICENSE](LICENSE).
