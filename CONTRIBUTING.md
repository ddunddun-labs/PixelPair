# Contributing to PixelPair

Thanks for helping improve PixelPair. The project is intentionally focused on a simple human + coding-agent pixel-art workflow, so small, well-scoped changes are preferred.

## Development setup

Requirements:

- .NET 10 SDK
- Node.js for MCP integration and JavaScript checks
- Python 3 for the smoke-test harness

Run the app from source:

```bash
dotnet run
```

PixelPair starts a local server and opens the browser editor.

For MCP development, run PixelPair and the MCP process in the same OS environment: Windows PixelPair with Windows Node/MCP, or Linux/WSL PixelPair with Linux/WSL Node/MCP.

## Verify changes

Before opening a pull request, run:

```bash
dotnet build PixelPair.csproj -c Release
node --check mcp/pixelpair-mcp.mjs
node --check wwwroot/app.js
python tests/smoke_test.py
```

The smoke test expects PixelPair to already be running.

If you add or change an HTTP/MCP drawing tool, update all relevant layers together:

1. server endpoint / canvas operation;
2. `apply_operations` support when appropriate;
3. MCP tool schema and dispatch;
4. README tool documentation;
5. smoke-test coverage for important behavior and regressions.

New pixel mutations must respect active-layer locking and participate correctly in undo/redo.

## README demo workflow

The checked-in README demo can be regenerated on the currently tested Linux/WSL maintainer workflow. It additionally requires VHS, ffmpeg, and Playwright Chromium.

```bash
npm install
npx playwright install chromium
npm run demo:readme
```

Do not regenerate the demo for unrelated changes.

## Pull requests

Keep pull requests focused. For large features or changes that alter the project's product direction, open an issue first so the scope can be discussed.

A good pull request should explain:

- what changed and why;
- user-visible behavior;
- security or local-filesystem implications, if any;
- how the change was tested;
- platform-specific limitations.

## Keep private data out of the repository

Do not commit:

- API keys, tokens, passwords, or license keys;
- `.mcp.json`, `.claude/`, `.codex/`, or other local agent configuration;
- absolute local development paths or usernames;
- generated archives or temporary demo files;
- `sixlabors.lic` or a Six Labors license key.

Use GitHub noreply email for commits if you do not want a personal email address exposed in public Git history.

## License

By contributing, you agree that your contribution may be distributed under PixelPair's [MIT License](LICENSE).
