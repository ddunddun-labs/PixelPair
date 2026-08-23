# Changelog

All notable user-facing changes to PixelPair will be documented here.

The project has not published its first public release yet.

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

[0.1.0]: https://github.com/ddunddun-labs/PixelPair/releases/tag/v0.1.0
