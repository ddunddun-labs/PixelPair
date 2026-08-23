#!/usr/bin/env python3
"""HTTP smoke tests for PixelPair's local server.

Uses only Python's standard library so CI does not need extra packages.
"""
from __future__ import annotations

import base64
import json
import os
import queue
import subprocess
import threading
from pathlib import Path
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

HOME = Path.home()
STATE_DIR = HOME / ".pixelpair"
URL_FILE = STATE_DIR / "agent-url.txt"
TOKEN_FILE = STATE_DIR / "agent-token.txt"


def wait_for_file(path: Path, timeout: float = 20.0) -> str:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            value = path.read_text(encoding="utf-8").strip()
            if value:
                return value
        except FileNotFoundError:
            pass
        time.sleep(0.1)
    raise AssertionError(f"timed out waiting for {path}")


BASE = wait_for_file(URL_FILE).rstrip("/")
TOKEN = wait_for_file(TOKEN_FILE)


def request(method: str, path: str, *, body=None, headers=None, raw_body: bytes | None = None):
    hdrs = dict(headers or {})
    if raw_body is not None:
        data = raw_body
    elif body is not None:
        data = json.dumps(body).encode("utf-8")
        hdrs.setdefault("Content-Type", "application/json")
    else:
        data = None
    req = Request(BASE + path, data=data, headers=hdrs, method=method)
    try:
        with urlopen(req, timeout=5) as res:
            return res.status, res.headers, res.read()
    except HTTPError as exc:
        return exc.code, exc.headers, exc.read()


def json_request(method: str, path: str, *, body=None, headers=None, expected=200):
    status, response_headers, payload = request(method, path, body=body, headers=headers)
    if status != expected:
        raise AssertionError(f"{method} {path}: expected {expected}, got {status}: {payload!r}")
    data = json.loads(payload.decode("utf-8")) if payload else None
    return data, response_headers


def canvas_png() -> bytes:
    status, _, payload = request("GET", "/canvas.png")
    assert status == 200, status
    assert payload.startswith(b"\x89PNG\r\n\x1a\n")
    return payload


def canvas_pixels() -> dict[tuple[int, int], str]:
    data, _ = json_request("GET", "/canvas?png=0&pixels=1")
    return {(p["x"], p["y"]): p["color"].upper() for p in data.get("pixels") or []}


def active_layer():
    data, _ = json_request("GET", "/layers")
    by_id = {layer["id"]: layer for layer in data["layers"]}
    return data["active_id"], by_id[data["active_id"]], data["layers"]


def main() -> None:
    # Server health and browser-origin policy.
    health, _ = json_request("GET", "/health")
    assert health["ok"] is True and health["grid"] == 64

    status, _, _ = request("GET", "/health", headers={"Origin": "https://evil.example"})
    assert status == 403, f"external browser origin should be blocked, got {status}"

    status, headers, _ = request(
        "OPTIONS",
        "/pixels",
        headers={"Origin": BASE, "Access-Control-Request-Method": "POST"},
    )
    assert status == 204, status
    assert headers.get("Access-Control-Allow-Origin") == BASE
    assert headers.get("Access-Control-Allow-Origin") != "*"

    # A locked active layer must reject both human/UI and MCP-style pixel edits.
    active_id, _, _ = active_layer()
    json_request("POST", "/clear", body={})
    json_request("POST", "/pixels", body={"mode": "partial", "pixels": [{"x": 1, "y": 1, "color": "#FF0000"}]})
    before_locked_edit = canvas_png()
    json_request("POST", "/layers/locked", body={"id": active_id, "locked": True})

    rejected, _ = json_request(
        "POST",
        "/pixels",
        body={"mode": "partial", "pixels": [{"x": 2, "y": 2, "color": "#00FF00"}]},
        expected=409,
    )
    assert rejected["ok"] is False and "잠" in rejected["error"]
    assert canvas_png() == before_locked_edit, "locked-layer edit changed the canvas"
    json_request("POST", "/layers/locked", body={"id": active_id, "locked": False})

    # Selection-aware recolor: only the selected region should change.
    json_request("POST", "/clear", body={})
    json_request(
        "POST",
        "/pixels",
        body={
            "mode": "partial",
            "pixels": [
                {"x": 1, "y": 1, "color": "#FF0000"},
                {"x": 10, "y": 10, "color": "#FF0000"},
            ],
        },
    )
    json_request("POST", "/selection", body={"x": 0, "y": 0, "w": 5, "h": 5})
    recolor, _ = json_request("POST", "/recolor", body={"from": "#FF0000", "to": "#00FF00", "tolerance": 0})
    assert recolor["count"] == 1, recolor
    pixels = canvas_pixels()
    assert pixels[(1, 1)] == "#00FF00", pixels
    assert pixels[(10, 10)] == "#FF0000", pixels
    json_request("POST", "/selection/clear", body={})

    # Merge Down must preserve the rendered result when layer opacity is involved.
    json_request("POST", "/clear", body={})
    json_request("POST", "/pixels", body={"mode": "partial", "pixels": [{"x": 5, "y": 5, "color": "#0000FF"}]})
    added, _ = json_request("POST", "/layers/add", body={"name": "Top"})
    top_id = added["layer"]["id"]
    json_request("POST", "/pixels", body={"mode": "partial", "pixels": [{"x": 5, "y": 5, "color": "#FF0000"}]})
    json_request("POST", "/layers/opacity", body={"id": top_id, "opacity": 0.5})
    before_merge = canvas_png()
    merged, _ = json_request("POST", "/layers/merge", body={"id": top_id})
    assert merged["ok"] is True
    after_merge = canvas_png()
    assert before_merge == after_merge, "merge-down changed the rendered image"

    # draw_polygon stroke_width regression coverage (issue #4)
    json_request("POST", "/clear", body={})
    json_request(
        "POST",
        "/draw_polygon",
        body={
            "points": [[10, 10], [50, 10], [30, 45]],
            "color": "#38BDF8",
            "fill": False,
            "stroke_width": 1,
        },
    )
    thin_count = len(canvas_pixels())

    json_request("POST", "/clear", body={})
    json_request(
        "POST",
        "/draw_polygon",
        body={
            "points": [[10, 10], [50, 10], [30, 45]],
            "color": "#38BDF8",
            "fill": False,
            "stroke_width": 3,
        },
    )
    thick_count = len(canvas_pixels())
    assert thick_count > thin_count, f"stroke_width=3 ({thick_count}px) should produce more pixels than stroke_width=1 ({thin_count}px)"

    # center_canvas test: draw off-center box and center it
    json_request("POST", "/clear", body={})
    json_request("POST", "/fill_rect", body={"x": 0, "y": 0, "w": 4, "h": 4, "color": "#38BDF8"})
    res_center, _ = json_request("POST", "/center_canvas", body={"axis": "both"})
    assert res_center["ok"] is True
    assert res_center["dx"] == (64 - 4) // 2
    assert res_center["dy"] == (64 - 4) // 2
    centered_pixels = canvas_pixels()
    assert len(centered_pixels) == 16
    assert (30, 30) in centered_pixels

    # scale_rect test: 2x2 box scaled 2x -> 4x4 (16px)
    json_request("POST", "/clear", body={})
    json_request("POST", "/fill_rect", body={"x": 0, "y": 0, "w": 2, "h": 2, "color": "#FF004D"})
    res_scale, _ = json_request("POST", "/scale_rect", body={"scale": 2.0, "center": True})
    assert res_scale["ok"] is True
    assert res_scale["dst_w"] == 4
    assert res_scale["dst_h"] == 4
    scaled_pixels = canvas_pixels()
    assert len(scaled_pixels) == 16

    # apply_operations with center_canvas and scale_rect
    json_request("POST", "/clear", body={})
    res_ops, _ = json_request("POST", "/operations", body={
        "operations": [
            {"op": "fill_rect", "x": 0, "y": 0, "w": 4, "h": 4, "color": "#38BDF8"},
            {"op": "scale_rect", "scale": 2.0, "center": True},
            {"op": "center_canvas", "axis": "both"}
        ]
    })
    assert res_ops["ok"] is True
    assert len(canvas_pixels()) == 64

    # Filesystem path operations are agent-only and require the per-process local token.
    export_path = Path(tempfile.gettempdir()) / f"pixelpair-smoke-{os.getpid()}.png"
    try:
        unauthorized, _ = json_request(
            "POST",
            "/export",
            body={"path": str(export_path), "format": "png"},
            expected=401,
        )
        assert unauthorized["ok"] is False

        auth_headers = {"X-PixelPair-Token": TOKEN}
        exported, _ = json_request(
            "POST",
            "/export",
            body={"path": str(export_path), "format": "png"},
            headers=auth_headers,
        )
        assert exported["ok"] is True and export_path.exists()

        json_request(
            "POST",
            "/import",
            body={"path": str(export_path), "max_colors": 0, "knockout_corners": False},
            expected=401,
        )
        imported, _ = json_request(
            "POST",
            "/import",
            body={"path": str(export_path), "max_colors": 0, "knockout_corners": False},
            headers=auth_headers,
        )
        assert imported["ok"] is True

        # The browser UI uploads raw image bytes or base64 JSON and must not need the agent token.
        status, _, payload = request(
            "POST",
            "/import",
            raw_body=export_path.read_bytes(),
            headers={"Content-Type": "image/png", "Origin": BASE},
        )
        assert status == 200, payload

        b64_str = base64.b64encode(export_path.read_bytes()).decode("ascii")
        json_imported, _ = json_request(
            "POST",
            "/import",
            body={"image_base64": b64_str, "knockout_corners": False},
        )
        assert json_imported["ok"] is True

        # The MCP bridge must discover the token file automatically for path-based tools.
        mcp_export = Path(tempfile.gettempdir()) / f"pixelpair-mcp-smoke-{os.getpid()}.png"
        mcp = subprocess.Popen(
            ["node", "mcp/pixelpair-mcp.mjs"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            cwd=Path(__file__).resolve().parent.parent,
        )
        try:
            assert mcp.stdin is not None and mcp.stdout is not None
            stdout_lines: queue.Queue[str] = queue.Queue()

            def pump_stdout() -> None:
                assert mcp.stdout is not None
                for line in mcp.stdout:
                    stdout_lines.put(line)

            threading.Thread(target=pump_stdout, daemon=True).start()
            message = {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/call",
                "params": {
                    "name": "export_icon",
                    "arguments": {"path": str(mcp_export), "format": "png"},
                },
            }
            mcp.stdin.write(json.dumps(message) + "\n")
            mcp.stdin.flush()
            deadline = time.time() + 10
            response = None
            while time.time() < deadline:
                try:
                    line = stdout_lines.get(timeout=min(0.5, deadline - time.time()))
                except queue.Empty:
                    if mcp.poll() is not None:
                        break
                    continue
                if not line:
                    break
                candidate = json.loads(line)
                if candidate.get("id") == 1:
                    response = candidate
                    break
            assert response is not None, "MCP export_icon did not respond"
            assert "result" in response and not response["result"].get("isError"), response
            assert mcp_export.exists(), "MCP export_icon did not create the file"
        finally:
            mcp.terminate()
            try:
                mcp.wait(timeout=2)
            except subprocess.TimeoutExpired:
                mcp.kill()
            mcp_export.unlink(missing_ok=True)

        status, _, _ = request(
            "POST",
            "/export",
            body={"path": str(export_path), "format": "png"},
            headers={"Origin": "https://evil.example", "X-PixelPair-Token": TOKEN},
        )
        assert status == 403, "browser-origin check must run before agent-token authorization"
    finally:
        export_path.unlink(missing_ok=True)

    print("PixelPair smoke tests passed")


if __name__ == "__main__":
    try:
        main()
    except URLError as exc:
        raise AssertionError(f"PixelPair server request failed: {exc}") from exc
