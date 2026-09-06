# Changelog

All notable user-facing changes to PixelPair will be documented here.

## [0.1.3] - 2026-09-06

### Added

- Added agent-driven install flow via [`AGENTS.md`](AGENTS.md) and `scripts/agent-install.mjs` for Cursor, Claude Code, and Codex.
- Added MCP `get_status` tool and startup connection diagnostics in `pixelpair-mcp.mjs`.
- Added MCP setup docs (`docs/MCP_SETUP.md`, `docs/MCP_SETUP.ko.md`) and `QUICKSTART.txt` to release packages.

### Changed

- Release zips now bundle onboarding files (`agent-install.mjs`, `AGENTS.md`, `docs/`, `QUICKSTART.txt`) alongside platform binaries and the MCP bridge.

## [0.1.2] - 2026-08-24

### Added

- Added multi-resolution ICO export containing 16, 24, 32, 48, 64, 128, and 256 px icon frames in a single `.ico` file.
- Added nearest-neighbor downscaling for pixel-perfect icon rendering across non-native canvas dimensions.
- Added `outline_object` and `drop_shadow` MCP transform tools with atomic `apply_operations` integration.
- Added live 16px, 32px, and 48px mini icon previews to the web UI.
- Added automated multi-resolution ICO structure and PNG chunk integrity tests to `tests/smoke_test.py`.

## [0.1.1] - 2026-08-23

### Added

- Added `center_canvas` MCP tool to automatically center drawing contents within the 64×64 canvas.
- Added `scale_rect` MCP tool to rescale drawings/regions using nearest-neighbor interpolation with automatic bounding-box support.
- Added `center_canvas` and `scale_rect` support to atomic `apply_operations` batching.
- Added regression tests for `center_canvas`, `scale_rect`, and Base64 image imports in `tests/smoke_test.py`.

### Fixed

- Fixed browser drag-and-drop / image imports to allow Base64 JSON payloads without requiring local agent tokens.

## [0.1.0] - 2026-08-23

### Added

- Local-first 64×64 browser pixel editor with live MCP collaboration.
- Layer management, selection, palettes, symmetry, pixel-perfect lines, undo/redo, and `.pxp` project files.
- Semantic MCP drawing and transform tools, including batched operations and polygon drawing.
- PNG/ICO import and export, plus cross-platform self-contained release builds.
- Reproducible README collaboration demo workflow.

### Security

- Local HTTP server binds to `127.0.0.1`.
- Same-origin browser policy blocks untrusted websites from driving the localhost API.
- Path-based import/export operations require a per-run local agent token.
- Locked layers reject pixel mutations through both browser and MCP paths.

### Fixed

- Selection-aware recolor routing.
- Merge Down now preserves the rendered result when layer opacity is involved.
- Windows portability of the smoke-test MCP subprocess handling.
- `draw_polygon` now honors `stroke_width`, with regression coverage for thick outlines.

[0.1.3]: https://github.com/ddunddun-labs/PixelPair/releases/tag/v0.1.3
[0.1.2]: https://github.com/ddunddun-labs/PixelPair/releases/tag/v0.1.2
[0.1.1]: https://github.com/ddunddun-labs/PixelPair/releases/tag/v0.1.1
[0.1.0]: https://github.com/ddunddun-labs/PixelPair/releases/tag/v0.1.0
