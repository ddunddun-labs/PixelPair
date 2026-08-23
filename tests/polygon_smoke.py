#!/usr/bin/env python3
"""Focused smoke coverage for the draw_polygon endpoint and batch operation."""
from __future__ import annotations

import json
import time
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

URL_FILE = Path.home() / ".pixelpair" / "agent-url.txt"


def wait_for_base(timeout: float = 20.0) -> str:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            value = URL_FILE.read_text(encoding="utf-8").strip()
            if value:
                return value.rstrip("/")
        except FileNotFoundError:
            pass
        time.sleep(0.1)
    raise AssertionError("timed out waiting for PixelPair agent URL")


BASE = wait_for_base()


def request(method: str, path: str, body=None, expected: int = 200):
    data = None if body is None else json.dumps(body).encode("utf-8")
    headers = {} if data is None else {"Content-Type": "application/json"}
    req = Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urlopen(req, timeout=5) as res:
            status, payload = res.status, res.read()
    except HTTPError as exc:
        status, payload = exc.code, exc.read()
    assert status == expected, f"{method} {path}: expected {expected}, got {status}: {payload!r}"
    return json.loads(payload.decode("utf-8")) if payload else None


def pixels() -> dict[tuple[int, int], str]:
    data = request("GET", "/canvas?png=0&pixels=1")
    return {(p["x"], p["y"]): p["color"].upper() for p in data.get("pixels") or []}


def main() -> None:
    # Filled polygon should paint both its boundary and interior.
    request("POST", "/clear", {})
    result = request(
        "POST",
        "/draw_polygon",
        {"points": [[10, 10], [20, 10], [20, 20], [10, 20]], "color": "#FF0000", "fill": True},
    )
    assert result["ok"] is True and result["count"] > 0
    current = pixels()
    assert current[(10, 10)] == "#FF0000"
    assert current[(15, 15)] == "#FF0000"

    # Outline-only polygon should leave the center transparent.
    request("POST", "/clear", {})
    request(
        "POST",
        "/draw_polygon",
        {"points": [[10, 10], [20, 10], [20, 20], [10, 20]], "color": "#00FF00", "fill": False},
    )
    current = pixels()
    assert current[(10, 10)] == "#00FF00"
    assert (15, 15) not in current

    # Batch operations must support the same polygon primitive.
    request("POST", "/clear", {})
    request(
        "POST",
        "/operations",
        {
            "operations": [
                {
                    "op": "draw_polygon",
                    "points": [[30, 30], [36, 30], [36, 36], [30, 36]],
                    "color": "#0000FF",
                    "fill": True,
                }
            ]
        },
    )
    assert pixels()[(33, 33)] == "#0000FF"

    # Polygon mutations must honor active-layer locking.
    layers = request("GET", "/layers")
    active_id = layers["active_id"]
    request("POST", "/layers/locked", {"id": active_id, "locked": True})
    try:
        rejected = request(
            "POST",
            "/draw_polygon",
            {"points": [[1, 1], [3, 1], [2, 3]], "color": "#FFFFFF", "fill": True},
            expected=409,
        )
        assert rejected["ok"] is False
    finally:
        request("POST", "/layers/locked", {"id": active_id, "locked": False})

    print("PixelPair polygon smoke tests passed")


if __name__ == "__main__":
    main()
