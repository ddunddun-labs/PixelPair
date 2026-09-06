# PixelPair — agent instructions

PixelPair is a local-first 64×64 pixel editor. Humans use the browser UI; coding agents use MCP tools on the same live canvas.

Official repository: https://github.com/ddunddun-labs/PixelPair

## Install (when the user asks)

Trigger phrases (Korean or English):

- "https://github.com/ddunddun-labs/PixelPair 설치하고 MCP 연동해줘"
- "Install PixelPair from GitHub and connect MCP"

### Steps

1. Require **Node.js 18+** on the same OS environment as the MCP client (Windows Cursor → Windows Node; WSL Cursor → WSL Node).
2. Clone the official repo (any directory is fine) **or** use an existing checkout.
3. Run the installer from the repo root:

```bash
node scripts/agent-install.mjs --client cursor
```

Replace `cursor` with `claude` or `codex` when that is the active client.

4. Tell the user to **restart the MCP client once** after a successful install.
5. Verify:

```bash
node scripts/agent-install.mjs --check
```

Ask the agent to call MCP `get_status` or `get_canvas` after restart.

6. Optional smoke test: ask the user to run PixelPair if `/health` fails, then call MCP `get_canvas`.

### Installer flags

| Flag | Purpose |
| --- | --- |
| `--client cursor\|claude\|codex` | Register MCP for the detected client (default: `cursor`) |
| `--prefer-release` | Download the latest GitHub Release binary (default) |
| `--from-source` | Build with `dotnet publish` instead of downloading |
| `--force` | Re-download / overwrite `~/.pixelpair` |
| `--print` | Print MCP JSON only; do not install |
| `--check` | Verify install, server health, and MCP paths |

Install location (standard):

- Linux / macOS / WSL: `~/.pixelpair/`
- Windows: `%LOCALAPPDATA%\PixelPair\`

MCP bridge path after install: `{install-root}/mcp/pixelpair-mcp.mjs`

### Do not

- Commit `.cursor/mcp.json`, `.mcp.json`, tokens, or local agent config to git.
- Install from unofficial forks unless the user explicitly requests it.
- Run Windows PixelPair with WSL Node/MCP (or the reverse).

## Develop / verify (maintainers)

```bash
dotnet build PixelPair.csproj -c Release
node --check mcp/pixelpair-mcp.mjs
node --check wwwroot/app.js
python3 tests/smoke_test.py   # requires PixelPair server running
```

When changing HTTP/MCP tools, update server code, `apply_operations`, MCP schema, README tool docs, and smoke tests together.

Branch workflow: `feat/...` or `fix/...` → PR → Squash and Merge. Do not push directly to `main`.

Binary release builds are usually run by the user on Windows (`dotnet publish ...`); do not assume WSL cross-compile for local verification.
