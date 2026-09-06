# PixelPair MCP setup and troubleshooting

## Ask your agent (recommended)

```
Install https://github.com/ddunddun-labs/PixelPair and connect MCP.
```

The agent follows [`AGENTS.md`](../AGENTS.md).

## Manual setup (release zip)

1. Download the platform zip from GitHub Releases and extract it
2. Run `win-x64/PixelPair.exe` or `./linux-x64/PixelPair`
3. Install Node.js 18+
4. From the zip root:

```bash
node agent-install.mjs --client cursor
```

5. **Restart** your MCP client once

## Verify

```bash
node agent-install.mjs --check
```

After MCP connects, ask the agent to call `get_status` or `get_canvas`.

## Common issues

| Symptom | Fix |
| --- | --- |
| MCP connected but no tools | Fully restart the client |
| `ECONNREFUSED` | Start PixelPair first, or rerun `agent-install.mjs` |
| WSL Cursor + Windows PixelPair | Run Node/MCP and PixelPair in the **same OS** |
| Missing `claude`/`codex` CLI | Use `--print` and register manually |
| macOS zip | See `UNVERIFIED.txt` — not runtime-tested on real hardware yet |

## Standard install location

- Linux / macOS / WSL: `~/.pixelpair/`
- Windows: `%LOCALAPPDATA%\PixelPair\`

MCP bridge: `{install-root}/mcp/pixelpair-mcp.mjs`
