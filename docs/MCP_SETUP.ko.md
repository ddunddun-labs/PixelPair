# PixelPair MCP 설치 · 문제 해결

## 에이전트에게 설치 요청 (권장)

```
https://github.com/ddunddun-labs/PixelPair 설치하고 MCP 연동해줘
```

에이전트는 [`AGENTS.md`](../AGENTS.md)를 따릅니다.

## 수동 설치 (릴리스 zip)

1. GitHub Releases에서 플랫폼 zip을 받아 압축 해제
2. `win-x64/PixelPair.exe` 또는 `./linux-x64/PixelPair` 실행
3. Node.js 18+ 설치
4. zip 루트에서:

```bash
node agent-install.mjs --client cursor
```

5. Cursor(또는 사용 중인 MCP 클라이언트) **한 번 재시작**

## 연결 확인

```bash
node agent-install.mjs --check
```

MCP가 연결된 뒤 에이전트에게 `get_status` 또는 `get_canvas` 호출을 요청하세요.

## 자주 나는 문제

| 증상 | 조치 |
| --- | --- |
| MCP 연결됐는데 도구가 안 보임 | 클라이언트 완전 재시작 |
| `ECONNREFUSED` | PixelPair 실행 파일을 먼저 실행하거나 `agent-install.mjs` 재실행 |
| WSL Cursor + Windows PixelPair | **같은 OS**에서 Node/MCP와 PixelPair 실행 |
| `claude`/`codex` CLI 없음 | `--print`로 JSON 출력 후 수동 등록 |
| macOS zip | `UNVERIFIED.txt` 참고 — 실기 검증 전 |

## 표준 설치 경로

- Linux / macOS / WSL: `~/.pixelpair/`
- Windows: `%LOCALAPPDATA%\PixelPair\`

MCP 브리지: `{설치 경로}/mcp/pixelpair-mcp.mjs`
