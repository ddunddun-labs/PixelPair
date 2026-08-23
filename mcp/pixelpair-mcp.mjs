#!/usr/bin/env node
/**
 * PixelPair MCP — 로컬 HTTP(기본 http://127.0.0.1:17890)를 감싼다.
 * 서버가 안 떠 있으면 옆 폴더의 플랫폼별 실행 파일을 찾아 자동으로 백그라운드에 띄운다.
 */
import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const DEFAULT_URL = "http://127.0.0.1:17890";

// dist/mcp/pixelpair-mcp.mjs 옆에 dist/<rid>/PixelPair(.exe) 가 있다고 가정한다 (build-dist.sh 결과물 구조).
const BINARY_BY_PLATFORM = {
  "win32-x64": ["win-x64", "PixelPair.exe"],
  "linux-x64": ["linux-x64", "PixelPair"],
  "darwin-x64": ["osx-x64", "PixelPair"],
  "darwin-arm64": ["osx-arm64", "PixelPair"],
};

function resolveServerBinary() {
  const entry = BINARY_BY_PLATFORM[`${process.platform}-${process.arch}`];
  if (!entry) return null;
  const path = join(__dirname, "..", entry[0], entry[1]);
  return existsSync(path) ? path : null;
}

async function isServerUp() {
  try {
    const base = await resolveBaseUrl();
    const res = await fetch(base + "/health", { signal: AbortSignal.timeout(500) });
    return res.ok;
  } catch {
    return false;
  }
}

let launchPromise = null;

// 여러 도구 호출이 동시에 들어와도 실행 파일을 한 번만 띄우도록 진행 중인 시도를 공유한다.
function ensureServerRunning() {
  if (!launchPromise) {
    launchPromise = launchAndWait().finally(() => {
      launchPromise = null;
    });
  }
  return launchPromise;
}

async function launchAndWait() {
  const bin = resolveServerBinary();
  if (!bin) return false;
  try {
    const child = spawn(bin, [], { detached: true, stdio: "ignore" });
    child.unref();
  } catch {
    return false;
  }
  for (let i = 0; i < 40; i++) {
    await new Promise((r) => setTimeout(r, 250));
    if (await isServerUp()) return true;
  }
  return false;
}

function isConnectionError(err) {
  const code = err?.cause?.code || err?.code;
  if (code === "ECONNREFUSED" || code === "ENOTFOUND" || code === "ECONNRESET") return true;
  return /ECONNREFUSED|fetch failed/i.test(String(err?.message || err));
}

async function resolveBaseUrl() {
  if (process.env.PIXELPAIR_URL) return process.env.PIXELPAIR_URL.replace(/\/$/, "");
  const candidates = [
    // OS 무관 고정 경로 (Windows/Mac/Linux/WSL 전부 여기부터 확인)
    join(homedir(), ".pixelpair", "agent-url.txt"),
    // 예전 Windows 전용 경로 (구버전 서버와의 호환용)
    process.env.LOCALAPPDATA
      ? join(process.env.LOCALAPPDATA, "PixelPair", "agent-url.txt")
      : "",
    join(homedir(), "AppData", "Local", "PixelPair", "agent-url.txt"),
  ].filter(Boolean);
  for (const file of candidates) {
    try {
      const text = (await readFile(file, "utf8")).trim();
      if (text.startsWith("http")) return text.replace(/\/$/, "");
    } catch {
      /* skip */
    }
  }
  return DEFAULT_URL;
}

async function resolveAgentToken() {
  if (process.env.PIXELPAIR_TOKEN) return process.env.PIXELPAIR_TOKEN.trim();
  const candidates = [
    join(homedir(), ".pixelpair", "agent-token.txt"),
    process.env.LOCALAPPDATA
      ? join(process.env.LOCALAPPDATA, "PixelPair", "agent-token.txt")
      : "",
    join(homedir(), "AppData", "Local", "PixelPair", "agent-token.txt"),
  ].filter(Boolean);
  for (const file of candidates) {
    try {
      const token = (await readFile(file, "utf8")).trim();
      if (token) return token;
    } catch {
      /* skip */
    }
  }
  return null;
}

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + "\n");
}

function okText(obj) {
  return {
    content: [{ type: "text", text: typeof obj === "string" ? obj : JSON.stringify(obj, null, 2) }],
  };
}

// PNG는 base64 텍스트로 욱여넣지 않고 진짜 image 콘텐츠 블록으로 보내야 클라이언트가 실제로 렌더링한다.
function okTextWithImage(obj, imageBase64) {
  const content = [{ type: "text", text: typeof obj === "string" ? obj : JSON.stringify(obj, null, 2) }];
  if (imageBase64) {
    content.push({ type: "image", data: imageBase64, mimeType: "image/png" });
  }
  return { content };
}

function errText(message) {
  return {
    content: [{ type: "text", text: message }],
    isError: true,
  };
}

function reportStartup(message) {
  process.stderr.write(`[PixelPair] ${message}\n`);
  send({
    jsonrpc: "2.0",
    method: "notifications/message",
    params: { level: "info", data: `PixelPair ${message}` },
  });
}

async function request(base, method, path, body) {
  const url = base + path;
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const token = await resolveAgentToken();
  if (token) headers["X-PixelPair-Token"] = token;
  const res = await fetch(url, {
    method,
    headers: Object.keys(headers).length ? headers : undefined,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    json = { raw: text };
  }
  if (!res.ok) {
    const msg = json.error || text || res.statusText;
    throw new Error(`PixelPair ${res.status} ${path}: ${msg}`);
  }
  return json;
}

async function api(method, path, body) {
  const base = await resolveBaseUrl();
  try {
    return await request(base, method, path, body);
  } catch (err) {
    if (!isConnectionError(err)) throw err;
    reportStartup("서버를 시작하는 중...");
    const launched = await ensureServerRunning();
    if (!launched) {
      throw new Error(
        `PixelPair 서버에 연결할 수 없고 자동 실행도 실패했다 (${err.message}). ` +
          `수동으로 서버를 먼저 실행해줘.`
      );
    }
    reportStartup("서버가 준비됐다.");
    const freshBase = await resolveBaseUrl();
    return await request(freshBase, method, path, body);
  }
}

const TOOLS = [
  {
    name: "get_canvas",
    description:
      "캔버스 상태를 본다. 기본은 전체 요약+PNG(1배, 64x64). 사용자가 UI에서 영역을 선택한 경우 summary/selection에 {x,y,w,h}가 자동으로 포함된다. x,y,w,h를 직접 주면 그 구역을 8배 PNG로 준다. 픽셀 좌표 목록은 include_pixels=true일 때만 반환하며 최대 256개다. 토큰 절약을 위해 수정 대상 구역만 확대하고, 좌표 목록은 정밀 수정이 필요할 때만 요청한다.",
    inputSchema: {
      type: "object",
      properties: {
        include_png: { type: "boolean", description: "기본 true" },
        include_pixels: { type: "boolean", description: "기본 false. 정밀 좌표 수정이 필요할 때만 true" },
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
        scale: { type: "integer", description: "PNG 확대 배율. 기본은 전체 조회 1배, 구역 조회 8배. 최대 16." },
      },
    },
  },
  {
    name: "import_png",
    description:
      "그림을 64x64 격자로 내린다(초안). path 또는 image_base64. max_colors로 팔레트를 줄이고 knockout_corners로 밝은 네 모서리를 투명으로 뚫는다.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string" },
        image_base64: { type: "string" },
        max_colors: { type: "integer", description: "예: 16. 0이면 줄이지 않음" },
        knockout_corners: { type: "boolean", description: "네 모서리 flood 투명" },
      },
    },
  },
  {
    name: "set_pixels",
    description:
      "소수의 정밀한 도트 디테일 수정에만 사용한다. 토큰 절약을 위해 넓은 영역 채우기/지우기, 직선 그리기, 색상 치환 등은 반드시 fill_rect, draw_line, recolor, flood_erase를 우선 사용할 것. symmetry='vertical'/'horizontal'/'both' 지정 시 대칭점에도 동시 반영.",
    inputSchema: {
      type: "object",
      properties: {
        mode: { type: "string", enum: ["partial", "full"] },
        symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"], description: "실시간 대칭 적용 (vertical: x=32축 좌우 대칭)" },
        pixels: {
          type: "array",
          items: {
            type: "object",
            properties: {
              x: { type: "integer" },
              y: { type: "integer" },
              color: { type: "string", description: "#RRGGBB 또는 빈 문자열(지우기)" },
            },
            required: ["x", "y", "color"],
          },
        },
      },
      required: ["pixels"],
    },
  },
  {
    name: "apply_operations",
    description:
      "복잡한 아이콘의 여러 원시 작업을 한 요청으로 묶는다. fill_rect, clear_rect, flip_rect, shift_rect, rotate_rect, draw_line, draw_circle, draw_ellipse, round_corners, recolor, set_pixels, center_canvas, scale_rect를 operations 순서대로 적용하며 Undo는 한 단계만 만든다. [원칙]: 억지로 부위별 레이어를 쪼개지 말고 캐릭터 본체는 유기적인 명암을 위해 한 레이어에 완성도 높게 그리고, 배경/특수효과처럼 기능적으로 분리될 때만 레이어를 나눈다. symmetry('vertical')와 pixel_perfect(true) 옵션을 적극 활용할 것.",
    inputSchema: {
      type: "object",
      properties: {
        operations: {
          type: "array",
          description: "각 객체의 op는 fill_rect, clear_rect, flip_rect, shift_rect, rotate_rect, draw_line, draw_circle, draw_ellipse, draw_polygon, round_corners, recolor, set_pixels, center_canvas, scale_rect 중 하나. symmetry, pixel_perfect 옵션을 지원한다.",
          items: {
            type: "object",
            properties: {
              op: { type: "string", enum: ["fill_rect", "clear_rect", "flip_rect", "shift_rect", "rotate_rect", "draw_line", "draw_circle", "draw_ellipse", "draw_polygon", "round_corners", "recolor", "set_pixels", "center_canvas", "scale_rect"] },
              x: { type: "integer" }, y: { type: "integer" }, w: { type: "integer" }, h: { type: "integer" },
              x0: { type: "integer" }, y0: { type: "integer" }, x1: { type: "integer" }, y1: { type: "integer" },
              points: { type: "array", description: "draw_polygon용 꼭짓점 배열 [[x0,y0], [x1,y1], ...]" },
              radius: { type: "integer" }, rx: { type: "integer" }, ry: { type: "integer" }, color: { type: "string" }, fill: { type: "boolean" }, stroke_width: { type: "integer", description: "fill=false일 때 테두리 두께. 2 이상이면 링을 한 번에 그린다." },
              fill_color: { type: "string", description: "round_corners가 지운 모서리를 투명 대신 이 색으로 채운다." },
              axis: { type: "string", enum: ["both", "horizontal", "vertical"], description: "center_canvas용 정렬 축" },
              scale: { type: "number", description: "scale_rect용 배율" }, scale_x: { type: "number" }, scale_y: { type: "number" }, target_w: { type: "integer" }, target_h: { type: "integer" }, center: { type: "boolean" },
              symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"], description: "실시간 대칭 (vertical: 좌우 대칭)" },
              pixel_perfect: { type: "boolean", description: "draw_line 등에서 L자형 코너 겹침 도트 자동 제거" },
              from: { type: "string" }, to: { type: "string" }, tolerance: { type: "integer" }, mode: { type: "string", enum: ["partial", "full"] },
              pixels: { type: "array", items: { type: "object", properties: { x: { type: "integer" }, y: { type: "integer" }, color: { type: "string" } }, required: ["x", "y", "color"] } },
            },
            required: ["op"],
          },
        },
      },
      required: ["operations"],
    },
  },
  {
    name: "export_icon",
    description: "현재 캔버스를 PNG 또는 ICO로 저장한다.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: "저장 경로" },
        format: { type: "string", enum: ["png", "ico"] },
      },
      required: ["path"],
    },
  },
  {
    name: "undo",
    description: "마지막 캔버스 변경을 되돌린다.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "redo",
    description: "되돌린 캔버스 변경을 다시 실행한다.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "flood_erase",
    description: "(x,y)에서 연결된 비슷한 색 영역을 투명으로 지운다(페인트통 지우개). w,h를 주면 그 박스 안에서만 적용되며 대량 지우기 시 토큰 절약에 효과적.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer", description: "선택. 있으면 (x,y)부터 박스 클립" },
        h: { type: "integer" },
        tolerance: { type: "integer", description: "RGB 합 거리. 기본 32" },
      },
      required: ["x", "y"],
    },
  },
  {
    name: "recolor",
    description: "특정 색상(from)을 다른 색(to)으로 일괄 치환한다. to가 빈 문자열이면 투명. x,y,w,h 지정 시 해당 영역 내에서만 치환되며 대량 색상 변경 시 토큰을 대폭 절약함.",
    inputSchema: {
      type: "object",
      properties: {
        from: { type: "string", description: "#RRGGBB" },
        to: { type: "string", description: "#RRGGBB 또는 빈 문자열" },
        tolerance: { type: "integer", description: "기본 16" },
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
      },
      required: ["from", "to"],
    },
  },
  {
    name: "fill_rect",
    description: "사각형 영역을 한 색으로 채우거나 지운다(color=''). 넓은 영역이나 배경 작업 시 set_pixels 대신 사용하여 토큰을 대폭 절약하는 권장 도구. symmetry로 대칭점에도 동시에 적용할 수 있다.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
        color: { type: "string" },
        symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"] },
      },
      required: ["x", "y", "w", "h", "color"],
    },
  },
  {
    name: "clear_rect",
    description: "지정된 사각형(x,y,w,h) 영역의 픽셀을 투명하게 비운다.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
      },
      required: ["x", "y", "w", "h"],
    },
  },
  {
    name: "flip_rect",
    description: "지정된 사각형(x,y,w,h) 영역의 픽셀을 좌우로 반전(horizontal flip)시킨다. x,y,w,h 생략 시 활성 선택 영역에 적용됨.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
      },
    },
  },
  {
    name: "shift_rect",
    description: "지정된 사각형(x,y,w,h 또는 전체 캔버스/선택영역)의 픽셀을 dx, dy만큼 평행 이동한다. 그림 중앙 정렬이나 미세 위치 조정 시 사용.",
    inputSchema: {
      type: "object",
      properties: {
        dx: { type: "integer", description: "X축 이동 거리 (+우측, -좌측)" },
        dy: { type: "integer", description: "Y축 이동 거리 (+하단, -상단)" },
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
        wrap: { type: "boolean", description: "화면 밖으로 나간 픽셀을 반대편으로 순환시킬지 여부 (기본 false)" },
      },
      required: ["dx", "dy"],
    },
  },
  {
    name: "rotate_rect",
    description: "지정된 사각형 영역을 90도/180도/270도 회전한다. x,y,w,h 생략 시 활성 선택 영역 또는 캔버스 전체에 적용됨.",
    inputSchema: {
      type: "object",
      properties: {
        angle: { type: "integer", description: "회전 각도 (90, 180, 270)" },
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
      },
      required: ["angle"],
    },
  },
  {
    name: "draw_line",
    description: "두 점 사이 1픽셀 직선을 긋는다. 테두리, 윤곽선, 선분을 그릴 때 set_pixels 대신 사용한다. pixel_perfect=true로 꺾임의 겹친 도트를 보정하고 symmetry로 대칭선을 함께 그릴 수 있다.",
    inputSchema: {
      type: "object",
      properties: {
        x0: { type: "integer" },
        y0: { type: "integer" },
        x1: { type: "integer" },
        y1: { type: "integer" },
        color: { type: "string" },
        pixel_perfect: { type: "boolean" },
        symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"] },
      },
      required: ["x0", "y0", "x1", "y1", "color"],
    },
  },
  {
    name: "draw_circle",
    description: "중심(x,y)과 반지름으로 원을 그린다. fill=true면 꽉 채운 원반, 기본은 1픽셀 테두리다. fill=false와 stroke_width=2 이상을 함께 주면 속이 빈 링을 한 번에 그린다. symmetry를 지원한다.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer", description: "중심 x" },
        y: { type: "integer", description: "중심 y" },
        radius: { type: "integer" },
        color: { type: "string" },
        fill: { type: "boolean", description: "기본 false(테두리만)" },
        stroke_width: { type: "integer", description: "fill=false일 때 테두리 두께. 기본 1." },
        symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"] },
      },
      required: ["x", "y", "radius", "color"],
    },
  },
  {
    name: "get_layers",
    description: "현재 캔버스의 레이어 목록과 활성 레이어 정보를 조회한다.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "remove_layer",
    description: "ID의 레이어를 삭제한다. 최소 하나의 레이어는 항상 남는다.",
    inputSchema: { type: "object", properties: { id: { type: "string", description: "레이어 ID. 생략하면 활성 레이어" } } },
  },
  {
    name: "rename_layer",
    description: "레이어 이름을 변경한다.",
    inputSchema: { type: "object", properties: { id: { type: "string" }, name: { type: "string" } }, required: ["id", "name"] },
  },
  {
    name: "duplicate_layer",
    description: "레이어와 픽셀 내용을 복제하고 복사본을 활성 레이어로 설정한다.",
    inputSchema: { type: "object", properties: { id: { type: "string" } }, required: ["id"] },
  },
  {
    name: "move_layer",
    description: "레이어 순서를 한 칸 위나 아래로 이동한다. direction은 1(위), -1(아래)이다.",
    inputSchema: { type: "object", properties: { id: { type: "string" }, direction: { type: "integer", enum: [-1, 1] } }, required: ["id", "direction"] },
  },
  {
    name: "set_layer_locked",
    description: "레이어 잠금 상태를 설정한다. 잠긴 레이어는 픽셀 편집 대상이 될 수 없다.",
    inputSchema: { type: "object", properties: { id: { type: "string" }, locked: { type: "boolean" } }, required: ["id", "locked"] },
  },
  {
    name: "set_layer_opacity",
    description: "레이어 불투명도를 0.0~1.0으로 설정한다.",
    inputSchema: { type: "object", properties: { id: { type: "string" }, opacity: { type: "number", minimum: 0, maximum: 1 } }, required: ["id", "opacity"] },
  },
  {
    name: "add_layer",
    description: "새 레이어를 최상단에 추가하고 활성 레이어로 설정한다.",
    inputSchema: {
      type: "object",
      properties: {
        name: { type: "string", description: "레이어 이름 (예: 배경, 캐릭터, 소품)" },
      },
    },
  },
  {
    name: "select_layer",
    description: "작업할 활성 레이어를 ID로 선택한다.",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "레이어 ID" },
      },
      required: ["id"],
    },
  },
  {
    name: "set_layer_visible",
    description: "특정 레이어의 가시성(보이기/숨기기)을 변경한다.",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "레이어 ID" },
        visible: { type: "boolean" },
      },
      required: ["id", "visible"],
    },
  },
  {
    name: "merge_layer_down",
    description: "특정 레이어를 바로 아래 레이어와 하나로 병합한다.",
    inputSchema: {
      type: "object",
      properties: {
        id: { type: "string", description: "병합할 상단 레이어 ID" },
      },
      required: ["id"],
    },
  },
  {
    name: "draw_ellipse",
    description: "중심(x,y)과 가로 반지름(rx), 세로 반지름(ry)으로 타원을 그린다. fill=true면 꽉 채운 타원, 기본은 1픽셀 테두리다. fill=false와 stroke_width=2 이상을 주면 두꺼운 테두리 타원을 그린다. symmetry를 지원한다.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer", description: "중심 x" },
        y: { type: "integer", description: "중심 y" },
        rx: { type: "integer", description: "가로 반지름" },
        ry: { type: "integer", description: "세로 반지름" },
        color: { type: "string" },
        fill: { type: "boolean", description: "기본 false(테두리만)" },
        stroke_width: { type: "integer", description: "fill=false일 때 테두리 두께. 기본 1." },
        symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"] },
      },
      required: ["x", "y", "rx", "ry", "color"],
    },
  },
  {
    name: "draw_polygon",
    description: "꼭짓점 배열(points)을 연결하여 임의의 다각형을 그린다. fill=true면 내부를 꽉 채우고, false면 외곽선만 그린다. 사선 스트라이프, 삼각형, 사다리꼴, 별 모양 등에 효과적이며 symmetry를 지원한다.",
    inputSchema: {
      type: "object",
      properties: {
        points: {
          type: "array",
          items: {
            type: "array",
            items: { type: "integer" },
            minItems: 2,
            maxItems: 2,
          },
          description: "[[x0,y0], [x1,y1], [x2,y2], ...] 형태의 꼭짓점 목록",
        },
        color: { type: "string" },
        fill: { type: "boolean", description: "기본 true(내부 채우기)" },
        stroke_width: { type: "integer", description: "fill=false일 때 테두리 두께. 기본 1." },
        symmetry: { type: "string", enum: ["vertical", "horizontal", "both", "none"] },
      },
      required: ["points", "color"],
    },
  },
  {
    name: "export_project",
    description: "현재의 모든 레이어를 .pxp JSON 프로젝트 데이터로 내보낸다. 세션 간 작업 보존용이다.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "import_project",
    description: "export_project로 받은 .pxp JSON 프로젝트 데이터를 복원한다. 현재 캔버스와 레이어는 대체된다.",
    inputSchema: { type: "object", properties: { project: { type: "object", description: "export_project 결과의 프로젝트 JSON" } }, required: ["project"] },
  },
  {
    name: "round_corners",
    description: "사각형(x,y,w,h)의 네 모서리를 반지름(radius)만큼 사분원으로 깎는다. 기본은 투명 처리이며, fill_color를 주면 그 색으로 채워 아래 레이어에 구멍이 나지 않게 한다.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer" },
        y: { type: "integer" },
        w: { type: "integer" },
        h: { type: "integer" },
        radius: { type: "integer", description: "예: 4~8" },
        fill_color: { type: "string", description: "선택. 투명 대신 이 색으로 모서리를 채운다." },
      },
      required: ["x", "y", "w", "h", "radius"],
    },
  },
  {
    name: "center_canvas",
    description: "현재 활성 레이어의 픽셀들을 캔버스(64x64) 정중앙에 균등하게 정렬하여 이동시킨다.",
    inputSchema: {
      type: "object",
      properties: {
        axis: {
          type: "string",
          enum: ["both", "horizontal", "vertical"],
          description: "정렬 축 (기본: both). horizontal: 좌우만 중앙 정렬, vertical: 상하만 중앙 정렬",
        },
      },
    },
  },
  {
    name: "scale_rect",
    description: "지정된 사각형 영역(또는 생략 시 전체 픽셀 바운딩 박스)을 Nearest-Neighbor 알고리즘으로 비율에 맞춰 확대/축소한다.",
    inputSchema: {
      type: "object",
      properties: {
        x: { type: "integer", description: "선택. 원본 영역 x (생략 시 바운딩 박스 자동)" },
        y: { type: "integer", description: "선택. 원본 영역 y (생략 시 바운딩 박스 자동)" },
        w: { type: "integer", description: "선택. 원본 영역 너비 (생략 시 바운딩 박스 자동)" },
        h: { type: "integer", description: "선택. 원본 영역 높이 (생략 시 바운딩 박스 자동)" },
        scale: { type: "number", description: "확대/축소 배율 (예: 1.5는 150%, 0.8은 80%)" },
        scale_x: { type: "number", description: "가로 배율 (scale 대신 가로/세로 독립 지정 시)" },
        scale_y: { type: "number", description: "세로 배율 (scale 대신 가로/세로 독립 지정 시)" },
        target_w: { type: "integer", description: "목표 너비(픽셀)" },
        target_h: { type: "integer", description: "목표 높이(픽셀)" },
        center: { type: "boolean", description: "기본 true. 스케일된 결과를 캔버스 중앙에 자동 배치" },
      },
    },
  },
];

async function callTool(name, args) {
  args = args || {};
  switch (name) {
    case "get_canvas": {
      const png = args.include_png !== false;
      const hasRegion = [args.x, args.y, args.w, args.h].every((v) => v !== undefined && v !== null);
      const pixels = args.include_pixels === true;
      const q = new URLSearchParams({
        png: png ? "1" : "0",
        pixels: pixels ? "1" : "0",
      });
      if (hasRegion) {
        q.set("x", String(args.x));
        q.set("y", String(args.y));
        q.set("w", String(args.w));
        q.set("h", String(args.h));
      }
      if (args.scale !== undefined && args.scale !== null) {
        q.set("scale", String(args.scale));
      }
      const result = await api("GET", "/canvas?" + q.toString());
      const { png_base64, ...rest } = result;
      return okTextWithImage(rest, png_base64);
    }
    case "import_png": {
      if (!args.path && !args.image_base64) {
        return errText("path 또는 image_base64가 필요하다.");
      }
      return okText(await api("POST", "/import", {
        path: args.path,
        image_base64: args.image_base64,
        max_colors: args.max_colors ?? 0,
        knockout_corners: args.knockout_corners === true,
      }));
    }
    case "set_pixels":
      return okText(await api("POST", "/pixels", {
        mode: args.mode || "partial",
        symmetry: args.symmetry,
        pixels: args.pixels || [],
      }));
    case "apply_operations":
      return okText(await api("POST", "/operations", { operations: args.operations || [] }));
    case "export_icon":
      return okText(await api("POST", "/export", {
        path: args.path,
        format: args.format || "png",
      }));
    case "undo":
      return okText(await api("POST", "/undo", {}));
    case "redo":
      return okText(await api("POST", "/redo", {}));
    case "flood_erase":
      return okText(await api("POST", "/flood_erase", {
        x: args.x,
        y: args.y,
        w: args.w,
        h: args.h,
        tolerance: args.tolerance ?? 32,
      }));
    case "recolor":
      return okText(await api("POST", "/recolor", {
        from: args.from,
        to: args.to,
        tolerance: args.tolerance ?? 16,
        x: args.x,
        y: args.y,
        w: args.w,
        h: args.h,
      }));
    case "fill_rect":
      return okText(await api("POST", "/fill_rect", {
        x: args.x, y: args.y, w: args.w, h: args.h, color: args.color, symmetry: args.symmetry,
      }));
    case "clear_rect":
      return okText(await api("POST", "/clear_rect", {
        x: args.x, y: args.y, w: args.w, h: args.h,
      }));
    case "flip_rect":
      return okText(await api("POST", "/flip_rect", {
        x: args.x, y: args.y, w: args.w, h: args.h,
      }));
    case "shift_rect":
      return okText(await api("POST", "/shift_rect", {
        dx: args.dx, dy: args.dy, x: args.x, y: args.y, w: args.w, h: args.h, wrap: args.wrap === true,
      }));
    case "rotate_rect":
      return okText(await api("POST", "/rotate_rect", {
        angle: args.angle, x: args.x, y: args.y, w: args.w, h: args.h,
      }));
    case "draw_line":
      return okText(await api("POST", "/draw_line", {
        x0: args.x0, y0: args.y0, x1: args.x1, y1: args.y1, color: args.color,
        pixel_perfect: args.pixel_perfect === true, symmetry: args.symmetry,
      }));
    case "draw_circle":
      return okText(await api("POST", "/draw_circle", {
        x: args.x, y: args.y, radius: args.radius, color: args.color, fill: args.fill === true, stroke_width: args.stroke_width, symmetry: args.symmetry,
      }));
    case "draw_ellipse":
      return okText(await api("POST", "/draw_ellipse", {
        x: args.x, y: args.y, rx: args.rx, ry: args.ry, color: args.color, fill: args.fill === true, stroke_width: args.stroke_width, symmetry: args.symmetry,
      }));
    case "draw_polygon":
      return okText(await api("POST", "/draw_polygon", {
        points: args.points, color: args.color, fill: args.fill !== false, stroke_width: args.stroke_width, symmetry: args.symmetry,
      }));
    case "get_layers":
      return okText(await api("GET", "/layers"));
    case "add_layer":
      return okText(await api("POST", "/layers/add", { name: args.name }));
    case "select_layer":
      return okText(await api("POST", "/layers/select", { id: args.id }));
    case "set_layer_visible":
      return okText(await api("POST", "/layers/visible", { id: args.id, visible: args.visible }));
    case "remove_layer":
      return okText(await api("POST", "/layers/remove", { id: args.id }));
    case "rename_layer":
      return okText(await api("POST", "/layers/rename", { id: args.id, name: args.name }));
    case "duplicate_layer":
      return okText(await api("POST", "/layers/duplicate", { id: args.id }));
    case "move_layer":
      return okText(await api("POST", "/layers/move", { id: args.id, direction: args.direction }));
    case "set_layer_locked":
      return okText(await api("POST", "/layers/locked", { id: args.id, locked: args.locked }));
    case "set_layer_opacity":
      return okText(await api("POST", "/layers/opacity", { id: args.id, opacity: args.opacity }));
    case "merge_layer_down":
      return okText(await api("POST", "/layers/merge", { id: args.id }));
    case "export_project":
      return okText(await api("GET", "/export.pxp"));
    case "import_project":
      return okText(await api("POST", "/project/import", args.project));
    case "round_corners":
      return okText(await api("POST", "/round_corners", {
        x: args.x, y: args.y, w: args.w, h: args.h, radius: args.radius, fill_color: args.fill_color,
      }));
    case "center_canvas":
      return okText(await api("POST", "/center_canvas", {
        axis: args.axis,
      }));
    case "scale_rect":
      return okText(await api("POST", "/scale_rect", {
        x: args.x, y: args.y, w: args.w, h: args.h,
        scale: args.scale, scale_x: args.scale_x, scale_y: args.scale_y,
        target_w: args.target_w, target_h: args.target_h,
        center: args.center,
      }));
    default:
      return errText(`unknown tool: ${name}`);
  }
}

async function onMessage(msg) {
  if (msg.method === "initialize") {
    send({
      jsonrpc: "2.0",
      id: msg.id,
      result: {
        protocolVersion: msg.params?.protocolVersion || "2024-11-05",
        serverInfo: { name: "pixelpair", version: "1.0.0" },
        capabilities: { tools: {} },
      },
    });
    return;
  }
  if (msg.method === "notifications/initialized" || msg.method === "notifications/cancelled") {
    return;
  }
  if (msg.method === "ping") {
    send({ jsonrpc: "2.0", id: msg.id, result: {} });
    return;
  }
  if (msg.method === "tools/list") {
    send({ jsonrpc: "2.0", id: msg.id, result: { tools: TOOLS } });
    return;
  }
  if (msg.method === "tools/call") {
    try {
      const result = await callTool(msg.params?.name, msg.params?.arguments);
      send({ jsonrpc: "2.0", id: msg.id, result });
    } catch (e) {
      send({
        jsonrpc: "2.0",
        id: msg.id,
        result: errText(e instanceof Error ? e.message : String(e)),
      });
    }
    return;
  }
  if (msg.id !== undefined) {
    send({
      jsonrpc: "2.0",
      id: msg.id,
      error: { code: -32601, message: `Method not found: ${msg.method}` },
    });
  }
}

const rl = createInterface({ input: process.stdin });
rl.on("line", (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  let msg;
  try {
    msg = JSON.parse(trimmed);
  } catch {
    return;
  }
  onMessage(msg).catch((e) => {
    if (msg?.id !== undefined) {
      send({
        jsonrpc: "2.0",
        id: msg.id,
        error: { code: -32000, message: String(e) },
      });
    }
  });
});
