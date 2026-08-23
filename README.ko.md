# PixelPair

**코딩 에이전트와 함께 픽셀 아트를 그리세요.**

PixelPair는 사람과 MCP 기반 코딩 에이전트가 **같은 라이브 캔버스**를 함께 편집하는 작은 로컬 우선(local-first) 64×64 픽셀 편집기입니다. 브라우저에서 직접 그리고, Claude Code·Codex 또는 다른 MCP 클라이언트에게 같은 그림을 보고 수정하도록 요청할 수 있습니다.

[English](README.md)

![MCP로 로봇을 만들고 색상을 수정하는 PixelPair 협업 데모](docs/demo/pixelpair-collaboration.gif)

> 이 README 데모의 캔버스 변경은 실제 PixelPair MCP 호출로 수행됩니다. 재현성을 위해 녹화 스크립트는 결정적인 MCP 작업을 사용하고, 터미널의 자연어 문장은 사용자의 요청을 보여주는 연출용 텍스트입니다.

## 왜 PixelPair인가요?

- **사람 + AI, 하나의 캔버스** — 브라우저에서 한 수정과 에이전트가 한 수정이 같은 캔버스에 바로 반영됩니다.
- **로컬 우선** — 편집기는 `127.0.0.1`에서 동작합니다. PixelPair 자체에는 계정, 클라우드 서비스, AI API 키가 필요하지 않습니다.
- **MCP 중심** — 에이전트가 캔버스를 이미지로 보고, 그리기·영역 변환·레이어 관리·내보내기를 할 수 있습니다.
- **픽셀 작업에 맞춘 기능** — 레이어, 선택 영역, 대칭, 픽셀 퍼펙트 선, 팔레트 추출, Undo/Redo, nearest-neighbor 내보내기를 지원합니다.
- **간단한 프로젝트 파일** — 전체 레이어 스택을 하나의 `.pxp` 파일로 저장합니다.
- **크로스플랫폼** — Windows와 Linux/WSL은 실제 실행 검증을 마쳤고, macOS는 빌드는 되지만 아직 실제 기기 실행 검증이 필요합니다.

PixelPair는 의도적으로 범위를 좁게 잡고 있습니다. Aseprite를 대체하려는 프로젝트가 아니라, **사람 ↔ 코딩 에이전트의 픽셀 아트 작업 루프**를 빠르고 눈에 보이게 만드는 것이 목표입니다.

## 빠른 시작

### 1. PixelPair 실행

플랫폼에 맞는 릴리스를 받아 압축을 풀고 실행합니다.

```text
Windows       win-x64/PixelPair.exe
Linux / WSL   ./linux-x64/PixelPair
macOS x64     ./osx-x64/PixelPair
macOS arm64   ./osx-arm64/PixelPair
```

PixelPair가 로컬 서버를 시작하고 기본 브라우저에서 편집기를 엽니다. 브라우저가 자동으로 열리지 않으면 콘솔에 표시된 로컬 주소를 직접 열면 됩니다.

### 2. 코딩 에이전트 연결 (선택)

Node.js를 설치한 뒤 `mcp/pixelpair-mcp.mjs`를 사용하는 MCP 클라이언트에 등록합니다.

Claude Code:

```bash
claude mcp add --scope user pixelpair -- node /absolute/path/to/mcp/pixelpair-mcp.mjs
```

Codex:

```bash
codex mcp add pixelpair -- node /absolute/path/to/mcp/pixelpair-mcp.mjs
```

등록 후 클라이언트를 다시 시작합니다. MCP 브리지는 실행 중인 PixelPair를 찾을 수 있고, 배포판 구조에서 사용하면 필요한 경우 같은 플랫폼의 PixelPair 실행 파일을 자동으로 띄울 수 있습니다.

> PixelPair와 MCP 프로세스는 같은 OS 환경에서 실행하세요. 예를 들어 Windows PixelPair는 Windows Node/MCP와, WSL PixelPair는 WSL Node/MCP와 함께 사용합니다.

## 함께 작업하는 방식

일반적인 작업 흐름은 다음과 같습니다.

```text
사람:     브라우저에서 스케치 또는 픽셀 수정
에이전트: get_canvas → 이미지/레이어/선택 영역 확인
에이전트: MCP로 그리기 또는 영역 변환
사람:     결과를 직접 다듬기
에이전트: 다시 확인하고 이어서 수정
```

`get_canvas`는 현재 캔버스를 실제 MCP image content block으로 반환할 수 있습니다. 활성 선택 영역과 레이어 메타데이터도 함께 제공되므로 에이전트가 전체 이미지를 막연하게 다루지 않고 필요한 부분에 집중할 수 있습니다.

잠긴 레이어는 서버에서 강제되므로 브라우저 그리기와 MCP 픽셀 수정 모두 차단됩니다.

## 편집 기능

| 기능 | 설명 |
| --- | --- |
| 캔버스 | 투명 배경의 64×64 픽셀 캔버스, 팬·줌 |
| 드로잉 | 그리기, 지우기, 스포이드, 대칭, 픽셀 퍼펙트 선 |
| 레이어 | 추가, 삭제, 이름 변경, 복제, 이동, 숨김, 잠금, 불투명도, 아래로 병합 |
| 선택 영역 | 사람이 수정하거나 에이전트가 작업할 영역 지정 |
| 팔레트 | PICO-8 팔레트, 사용자 팔레트, 색 추출, 일괄 색상 변경 |
| 히스토리 | Undo / Redo, 최대 50단계 |
| 가져오기 | PNG, JPG, WEBP, BMP, GIF, ICO, 클립보드, 드래그앤드롭 |
| 프로젝트 | 전체 레이어 스택을 `.pxp`로 저장/불러오기 |
| 내보내기 | PNG, ICO |
| 실시간 동기화 | Server-Sent Events(SSE)로 브라우저 자동 갱신 |

## MCP 도구

PixelPair는 수천 개 픽셀을 하나씩 보내는 대신 의미 있는 단위의 도구를 제공합니다.

| 구분 | 도구 |
| --- | --- |
| 조회 | `get_canvas`, `get_layers` |
| 그리기 | `set_pixels`, `fill_rect`, `clear_rect`, `draw_line`, `draw_circle`, `draw_ellipse`, `draw_polygon`, `round_corners` |
| 변환 | `flood_erase`, `recolor`, `flip_rect`, `shift_rect`, `rotate_rect` |
| 배치 | `apply_operations` |
| 레이어 | `add_layer`, `select_layer`, `remove_layer`, `rename_layer`, `duplicate_layer`, `move_layer`, `set_layer_visible`, `set_layer_locked`, `set_layer_opacity`, `merge_layer_down` |
| 프로젝트 | `export_project`, `import_project` |
| 결과/히스토리 | `export_icon`, `undo`, `redo` |

넓은 영역은 `fill_rect`, `recolor`, 변환 도구 또는 `apply_operations`를 우선 사용하고, `set_pixels`는 소수 픽셀의 정밀 수정에 사용하는 것이 좋습니다.

## 로컬 보안 모델

PixelPair는 로컬 데스크톱 도구처럼 동작하도록 설계되었습니다.

- HTTP 서버는 `127.0.0.1`에만 바인딩합니다.
- 브라우저 요청은 wildcard CORS 대신 PixelPair 자체 origin만 허용합니다.
- 임의의 로컬 파일 경로를 읽거나 쓰는 JSON 작업에는 실행별 로컬 에이전트 토큰이 필요합니다.
- 공식 MCP 브리지는 해당 토큰을 로컬에서 찾아 자동으로 전송합니다.
- 토큰을 프롬프트, 로그 또는 공개 설정 파일에 복사해서 사용하는 방식은 권장하지 않습니다.

브라우저에서는 로컬 파일시스템 경로 권한을 주지 않고 이미지 바이트를 직접 업로드할 수 있습니다.

## 소스에서 실행

필요한 것:

- .NET 10 SDK
- MCP 연동 시 Node.js
- smoke test 실행 시 Python 3

```bash
git clone https://github.com/ddunddun-labs/PixelPair.git
cd PixelPair
dotnet run
```

기본 검증:

```bash
dotnet build PixelPair.csproj -c Release
node --check mcp/pixelpair-mcp.mjs
node --check wwwroot/app.js
python tests/smoke_test.py
```

smoke test는 PixelPair 서버가 실행 중인 상태를 전제로 합니다. localhost 보안 정책, 에이전트 토큰 기반 파일 작업, 레이어 잠금, 선택 영역 recolor, opacity를 보존하는 Merge Down, MCP export round trip을 검증합니다.

## README 데모 재현

메인테이너용으로 결정적인 데모 제작 워크플로우가 포함되어 있습니다. 브라우저 녹화에는 Playwright, 터미널 장면에는 VHS, 캔버스 변경에는 PixelPair MCP, 합성과 인코딩에는 ffmpeg를 사용합니다.

이 워크플로우는 현재 Linux/WSL에서 검증되었으며 Node.js 외에 `vhs`, `ffmpeg`, Playwright용 Chromium이 필요합니다.

```bash
npm install
npx playwright install chromium
npm run demo:readme
```

PixelPair 서버는 미리 실행되어 있어야 합니다. 명령을 실행하면 `/tmp/pixelpair-readme-demo/` 아래에 임시 MP4를 만들고, README에 체크인되는 `docs/demo/pixelpair-collaboration.gif`를 다시 생성합니다.

## 릴리스

`v*` 태그를 push하면 release workflow가 다음 플랫폼의 self-contained 패키지를 빌드합니다.

- Windows x64
- Linux x64
- macOS x64
- macOS arm64

Windows와 Linux/WSL은 실제 실행 검증을 완료했습니다. macOS 패키지는 현재 빌드는 성공하지만 실제 macOS 기기에서의 런타임 검증이 아직 필요합니다.

## 프로젝트 형식

`.pxp`는 PixelPair의 JSON 기반 프로젝트 형식입니다. 레이어 스택, 레이어 메타데이터, 활성 레이어와 픽셀 데이터를 보존하므로 납작하게 합쳐진 이미지뿐 아니라 작업 중인 프로젝트 자체를 복원할 수 있습니다.

## 기여

Issue와 Pull Request를 환영합니다. 큰 변경을 시작하기 전에는 먼저 Issue를 열어 PixelPair의 사람 + 코딩 에이전트 중심 범위와 맞는지 논의해 주세요.

## 라이선스

PixelPair 소스 코드는 [MIT License](LICENSE)로 배포됩니다. 서드파티 의존성은 각각의 라이선스를 따릅니다.
