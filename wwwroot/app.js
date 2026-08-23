const GRID = 64;
const CELL = 640 / GRID;
const PICO8 = [
  "#000000", "#1D2B53", "#7E2553", "#008751", "#AB5236", "#5F574F", "#C2C3C7", "#FFF1E8",
  "#FF004D", "#FFA300", "#FFEC27", "#00E436", "#29ADFF", "#83769C", "#FF77A8", "#FFCCAA"
];
const USER_SLOTS = 19;

const canvas = document.getElementById('grid');
const ctx = canvas.getContext('2d');
const scrollContainer = document.getElementById('canvasScrollContainer');
const statusEl = document.getElementById('status');
const curColorBox = document.getElementById('curColorBox');
const curColorHex = document.getElementById('curColorHex');

const btnPen = document.getElementById('btnPen');
const btnEraser = document.getElementById('btnEraser');
const btnUndo = document.getElementById('btnUndo');
const btnRedo = document.getElementById('btnRedo');
const btnClear = document.getElementById('btnClear');
const btnSelect = document.getElementById('btnSelect');
const btnClearSel = document.getElementById('btnClearSel');
const selInfo = document.getElementById('selInfo');
const btnAddLayer = document.getElementById('btnAddLayer');
const btnViewAll = document.getElementById('btnViewAll');
const btnViewFocus = document.getElementById('btnViewFocus');

let pixels = new Array(GRID * GRID).fill(null); // hex 문자열 또는 null(투명)
let layersData = [];
let activeLayerId = '';
let layerViewMode = 'all'; // 'all' | 'focus'
let selectedLayerIds = new Set();
let lastClickedLayerIndex = -1;

let selectedColor = '#FF004D';
let isEraser = false;
let isSelectMode = true; // 기본 모드는 영역 선택!
let isDrawing = false;
let isSelecting = false;
let selectStart = null;
let activeSelection = null; // { x, y, w, h }
let marchOffset = 0;
let animTimer = null;
let userPalette = new Array(USER_SLOTS).fill(null);
let pending = [];
let flushTimer = null;

// 대칭 & 픽셀 퍼펙트 상태
let isSymmetry = false;
let isPixelPerfect = true;

// 팬 & 줌 상태
let isSpacePressed = false;
let isPanning = false;
let panStart = { x: 0, y: 0, scrollLeft: 0, scrollTop: 0 };
let currentZoom = 1.0;
const minZoom = 0.5;
const maxZoom = 4.0;
const zoomStep = 0.25;

function idx(x, y) { return y * GRID + x; }

function drawGrid() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  for (let y = 0; y < GRID; y++) {
    for (let x = 0; x < GRID; x++) {
      const c = pixels[idx(x, y)];
      if (!c) continue;
      ctx.fillStyle = c;
      ctx.fillRect(x * CELL, y * CELL, CELL, CELL);
    }
  }

  ctx.strokeStyle = 'rgba(255,255,255,0.05)';
  ctx.lineWidth = 1;
  for (let i = 0; i <= GRID; i++) {
    ctx.beginPath(); ctx.moveTo(i * CELL, 0); ctx.lineTo(i * CELL, canvas.height); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(0, i * CELL); ctx.lineTo(canvas.width, i * CELL); ctx.stroke();
  }

  // 실시간 대칭 가이드라인 (세로 중심축 x = 32)
  if (isSymmetry) {
    ctx.save();
    ctx.strokeStyle = 'rgba(99, 102, 241, 0.75)';
    ctx.lineWidth = 2;
    ctx.setLineDash([4, 4]);
    ctx.beginPath();
    ctx.moveTo(32 * CELL, 0);
    ctx.lineTo(32 * CELL, canvas.height);
    ctx.stroke();
    ctx.restore();
  }

  // 선택 영역 하이라이트 (반투명 파랑 + 점선 테두리)
  if (activeSelection) {
    const sx = activeSelection.x * CELL;
    const sy = activeSelection.y * CELL;
    const sw = activeSelection.w * CELL;
    const sh = activeSelection.h * CELL;

    ctx.save();
    ctx.fillStyle = 'rgba(56, 189, 248, 0.22)';
    ctx.fillRect(sx, sy, sw, sh);

    ctx.strokeStyle = '#38bdf8';
    ctx.lineWidth = 2;
    ctx.setLineDash([6, 4]);
    ctx.lineDashOffset = -marchOffset;
    ctx.strokeRect(sx + 1, sy + 1, sw - 2, sh - 2);

    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 1;
    ctx.setLineDash([2, 8]);
    ctx.lineDashOffset = -marchOffset + 3;
    ctx.strokeRect(sx, sy, sw, sh);
    ctx.restore();
  }
}

function updateSelInfo() {
  if (activeSelection) {
    selInfo.textContent = `선택: (${activeSelection.x}, ${activeSelection.y}) ${activeSelection.w}×${activeSelection.h} (Esc 해제)`;
    selInfo.classList.add('active');
    if (!animTimer) {
      animTimer = setInterval(() => {
        marchOffset = (marchOffset + 1) % 10;
        drawGrid();
      }, 100);
    }
  } else {
    selInfo.textContent = isSelectMode ? '드래그하여 영역을 지정하세요' : '그리기 모드 (Esc로 선택 모드 복귀)';
    selInfo.classList.toggle('active', isSelectMode);
    if (animTimer) {
      clearInterval(animTimer);
      animTimer = null;
    }
  }
}

async function syncSelectionToServer() {
  if (!activeSelection) {
    try {
      await fetch('/selection', { method: 'DELETE' });
    } catch {}
    return;
  }
  try {
    await fetch('/selection', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(activeSelection),
    });
  } catch (err) {
    console.error('선택 영역 전송 실패', err);
  }
}

async function clearSelection() {
  activeSelection = null;
  updateSelInfo();
  drawGrid();
  await syncSelectionToServer();
}

function rgbToHex(r, g, b) {
  return '#' + [r, g, b].map((v) => v.toString(16).padStart(2, '0')).join('').toUpperCase();
}

// 서버의 canvas.png와 레이어 메타데이터 동기화 (전체 뷰 / 포커스 뷰 지원)
async function syncFromServer() {
  try {
    let canvasUrl = '/canvas.png?t=' + Date.now();
    if (layerViewMode === 'focus' && selectedLayerIds.size > 0) {
      canvasUrl = `/canvas.png?layers=${Array.from(selectedLayerIds).join(',')}&t=${Date.now()}`;
    }

    const [canvasRes, layersRes] = await Promise.all([
      fetch(canvasUrl),
      fetch('/layers?t=' + Date.now())
    ]);

    const blob = await canvasRes.blob();
    const bitmap = await createImageBitmap(blob);
    const off = document.createElement('canvas');
    off.width = GRID;
    off.height = GRID;
    const octx = off.getContext('2d');
    octx.drawImage(bitmap, 0, 0, GRID, GRID);
    const data = octx.getImageData(0, 0, GRID, GRID).data;
    for (let y = 0; y < GRID; y++) {
      for (let x = 0; x < GRID; x++) {
        const i = (y * GRID + x) * 4;
        pixels[idx(x, y)] = data[i + 3] > 20 ? rgbToHex(data[i], data[i + 1], data[i + 2]) : null;
      }
    }
    drawGrid();

    const layersJson = await layersRes.json();
    if (layersJson.ok) {
      layersData = layersJson.layers || [];
      activeLayerId = layersJson.active_id || '';
      if (selectedLayerIds.size === 0 && activeLayerId) {
        selectedLayerIds.add(activeLayerId);
      }
      renderLayersList();
    }
  } catch (err) {
    console.error('동기화 실패', err);
  }
}

function renderLayersList() {
  const container = document.getElementById('layerList');
  if (!container) return;
  container.innerHTML = '';

  const reversed = [...layersData].reverse();
  reversed.forEach((layer, revIdx) => {
    const origIdx = layersData.length - 1 - revIdx;
    const isTop = origIdx === layersData.length - 1;
    const isBottom = origIdx === 0;

    const isSelected = selectedLayerIds.has(layer.id);
    const isActive = layer.id === activeLayerId;

    const item = document.createElement('div');
    item.className = 'layer-item' + (isSelected ? ' active' : '');
    if (isSelected && layerViewMode === 'focus') {
      item.style.boxShadow = '0 0 0 1px #38bdf8';
    } else {
      item.style.boxShadow = '';
    }

    const mainRow = document.createElement('div');
    mainRow.className = 'layer-main-row';

    const thumb = document.createElement('img');
    thumb.className = 'layer-thumb';
    thumb.src = layer.png_base64 ? `data:image/png;base64,${layer.png_base64}` : '';

    const info = document.createElement('div');
    info.className = 'layer-info';

    const nameEl = document.createElement('div');
    nameEl.className = 'layer-name';
    nameEl.textContent = layer.name + (isActive ? ' ✎' : '');
    nameEl.title = '더블 클릭하여 이름 변경 (Ctrl/Shift+클릭 다중 선택)';
    nameEl.addEventListener('dblclick', async (e) => {
      e.stopPropagation();
      const newName = prompt('레이어 이름 변경:', layer.name);
      if (newName && newName.trim() && newName !== layer.name) {
        await fetch('/layers/rename', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ id: layer.id, name: newName.trim() }),
        });
        await syncFromServer();
      }
    });

    const meta = document.createElement('div');
    meta.className = 'layer-meta';
    meta.innerHTML = `<span>${layer.occupied}px</span> <span>${Math.round((layer.opacity ?? 1.0) * 100)}%</span>`;

    info.appendChild(nameEl);
    info.appendChild(meta);

    const actions = document.createElement('div');
    actions.className = 'layer-actions';

    if (!isTop) {
      const btnUp = document.createElement('button');
      btnUp.className = 'layer-btn';
      btnUp.textContent = '▲';
      btnUp.title = '위로 이동';
      btnUp.addEventListener('click', async (e) => {
        e.stopPropagation();
        await fetch('/layers/move', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ id: layer.id, direction: 1 }),
        });
        await syncFromServer();
      });
      actions.appendChild(btnUp);
    }

    if (!isBottom) {
      const btnDown = document.createElement('button');
      btnDown.className = 'layer-btn';
      btnDown.textContent = '▼';
      btnDown.title = '아래로 이동';
      btnDown.addEventListener('click', async (e) => {
        e.stopPropagation();
        await fetch('/layers/move', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ id: layer.id, direction: -1 }),
        });
        await syncFromServer();
      });
      actions.appendChild(btnDown);
    }

    const btnLock = document.createElement('button');
    btnLock.className = 'layer-btn' + (layer.locked ? ' active' : ' muted');
    btnLock.textContent = layer.locked ? '🔒' : '🔓';
    btnLock.title = layer.locked ? '잠금 해제' : '레이어 잠금';
    btnLock.addEventListener('click', async (e) => {
      e.stopPropagation();
      await fetch('/layers/locked', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: layer.id, locked: !layer.locked }),
      });
      await syncFromServer();
    });
    actions.appendChild(btnLock);

    const btnVis = document.createElement('button');
    btnVis.className = 'layer-btn' + (!layer.visible ? ' muted' : '');
    btnVis.textContent = layer.visible ? '👁️' : '🚫';
    btnVis.title = layer.visible ? '숨기기' : '보이기';
    btnVis.addEventListener('click', async (e) => {
      e.stopPropagation();
      await fetch('/layers/visible', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: layer.id, visible: !layer.visible }),
      });
      await syncFromServer();
    });
    actions.appendChild(btnVis);

    mainRow.appendChild(thumb);
    mainRow.appendChild(info);
    mainRow.appendChild(actions);

    const subRow = document.createElement('div');
    subRow.className = 'layer-sub-row';

    const opControl = document.createElement('div');
    opControl.className = 'layer-opacity-control';
    opControl.innerHTML = `<span>불투명도</span>`;

    const opSlider = document.createElement('input');
    opSlider.type = 'range';
    opSlider.className = 'layer-opacity-slider';
    opSlider.min = '0';
    opSlider.max = '100';
    opSlider.value = Math.round((layer.opacity ?? 1.0) * 100).toString();
    opSlider.title = `불투명도: ${opSlider.value}%`;
    opSlider.addEventListener('click', (e) => e.stopPropagation());
    opSlider.addEventListener('change', async (e) => {
      e.stopPropagation();
      const val = parseFloat(opSlider.value) / 100.0;
      await fetch('/layers/opacity', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: layer.id, opacity: val }),
      });
      await syncFromServer();
    });
    opControl.appendChild(opSlider);

    const subActions = document.createElement('div');
    subActions.className = 'layer-actions';

    const btnDup = document.createElement('button');
    btnDup.className = 'layer-btn';
    btnDup.textContent = '📋';
    btnDup.title = '레이어 복제';
    btnDup.addEventListener('click', async (e) => {
      e.stopPropagation();
      await fetch('/layers/duplicate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: layer.id }),
      });
      await syncFromServer();
    });
    subActions.appendChild(btnDup);

    const btnSaveLayer = document.createElement('a');
    btnSaveLayer.className = 'layer-btn';
    btnSaveLayer.textContent = '💾';
    btnSaveLayer.title = '이 레이어만 단독 PNG로 저장 (아이템 에셋)';
    btnSaveLayer.href = `/layer/export.png?id=${layer.id}`;
    btnSaveLayer.download = `${layer.name || 'layer'}.png`;
    btnSaveLayer.addEventListener('click', (e) => e.stopPropagation());
    subActions.appendChild(btnSaveLayer);

    if (!isBottom) {
      const btnMerge = document.createElement('button');
      btnMerge.className = 'layer-btn';
      btnMerge.textContent = '⬇️';
      btnMerge.title = '아래 레이어와 병합';
      btnMerge.addEventListener('click', async (e) => {
        e.stopPropagation();
        if (confirm(`'${layer.name}' 레이어를 아래 레이어와 병합할까요?`)) {
          await fetch('/layers/merge', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: layer.id }),
          });
          await syncFromServer();
        }
      });
      subActions.appendChild(btnMerge);
    }

    if (layersData.length > 1) {
      const btnDel = document.createElement('button');
      btnDel.className = 'layer-btn';
      btnDel.textContent = '🗑️';
      btnDel.title = '레이어 삭제';
      btnDel.addEventListener('click', async (e) => {
        e.stopPropagation();
        if (confirm(`'${layer.name}' 레이어를 삭제할까요?`)) {
          await fetch('/layers/remove', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: layer.id }),
          });
          await syncFromServer();
        }
      });
      subActions.appendChild(btnDel);
    }

    subRow.appendChild(opControl);
    subRow.appendChild(subActions);

    item.appendChild(mainRow);
    item.appendChild(subRow);

    // 레이어 클릭 핸들러 (일반 클릭, Ctrl 다중 선택, Shift 범위 선택)
    item.addEventListener('click', async (e) => {
      const curIdx = layersData.findIndex(l => l.id === layer.id);

      if (e.ctrlKey || e.metaKey) {
        layerViewMode = 'focus';
        if (selectedLayerIds.has(layer.id)) {
          if (selectedLayerIds.size > 1) selectedLayerIds.delete(layer.id);
        } else {
          selectedLayerIds.add(layer.id);
        }
        activeLayerId = layer.id;
        lastClickedLayerIndex = curIdx;
      } else if (e.shiftKey && lastClickedLayerIndex >= 0) {
        layerViewMode = 'focus';
        const start = Math.min(lastClickedLayerIndex, curIdx);
        const end = Math.max(lastClickedLayerIndex, curIdx);
        selectedLayerIds.clear();
        for (let i = start; i <= end; i++) {
          selectedLayerIds.add(layersData[i].id);
        }
        activeLayerId = layer.id;
      } else {
        selectedLayerIds.clear();
        selectedLayerIds.add(layer.id);
        activeLayerId = layer.id;
        lastClickedLayerIndex = curIdx;
      }

      if (btnViewAll && btnViewFocus) {
        btnViewAll.classList.toggle('active', layerViewMode === 'all');
        btnViewFocus.classList.toggle('active', layerViewMode === 'focus');
      }

      await fetch('/layers/select', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: layer.id }),
      });
      await syncFromServer();
    });

    container.appendChild(item);
  });
}

// 뷰 모드 토글 (전체 보기 vs 선택 포커스 뷰)
if (btnViewAll) {
  btnViewAll.addEventListener('click', async () => {
    layerViewMode = 'all';
    btnViewAll.classList.add('active');
    btnViewFocus.classList.remove('active');
    await syncFromServer();
  });
}

if (btnViewFocus) {
  btnViewFocus.addEventListener('click', async () => {
    layerViewMode = 'focus';
    btnViewFocus.classList.add('active');
    btnViewAll.classList.remove('active');
    if (selectedLayerIds.size === 0 && activeLayerId) {
      selectedLayerIds.add(activeLayerId);
    }
    await syncFromServer();
  });
}

if (btnAddLayer) {
  btnAddLayer.addEventListener('click', async () => {
    await fetch('/layers/add', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({}),
    });
    await syncFromServer();
  });
}

function setPixelLocal(x, y, color) {
  if (x < 0 || y < 0 || x >= GRID || y >= GRID) return;
  pixels[idx(x, y)] = color;
}

function activeLayerIsLocked() {
  return layersData.find((layer) => layer.id === activeLayerId)?.locked === true;
}

function queuePixel(x, y, color) {
  if (activeLayerIsLocked()) {
    statusEl.textContent = '현재 레이어가 잠겨 있습니다.';
    return;
  }
  setPixelLocal(x, y, color);
  pending.push({ x, y, color: color || '' });
  if (isSymmetry) {
    const mx = GRID - 1 - x;
    if (mx !== x) {
      setPixelLocal(mx, y, color);
      pending.push({ x: mx, y, color: color || '' });
    }
  }
  drawGrid();
  if (!flushTimer) flushTimer = setTimeout(flushPixels, 30);
}

async function flushPixels() {
  flushTimer = null;
  if (pending.length === 0) return;
  const batch = pending;
  pending = [];
  try {
    const res = await fetch('/pixels', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode: 'partial', pixels: batch }),
    });
    if (!res.ok) {
      const error = await res.json().catch(() => ({}));
      statusEl.textContent = error.error || `픽셀 전송 실패 (${res.status})`;
      await syncFromServer();
    }
  } catch (err) {
    console.error('픽셀 전송 실패', err);
    await syncFromServer();
  }
}

function cellFromEvent(e) {
  const rect = canvas.getBoundingClientRect();
  return {
    x: Math.floor((e.clientX - rect.left) / (rect.width / GRID)),
    y: Math.floor((e.clientY - rect.top) / (rect.height / GRID)),
  };
}

function updateColorDisplay() {
  curColorBox.style.background = selectedColor;
  curColorHex.textContent = selectedColor;
}

function setToolMode(mode) {
  isEraser = mode === 'eraser';
  isSelectMode = mode === 'select';
  btnPen.classList.toggle('active', mode === 'pen');
  btnEraser.classList.toggle('active', mode === 'eraser');
  btnSelect.classList.toggle('active', mode === 'select');
  renderPaletteSelection();
  updateSelInfo();
}

function selectColor(hex) {
  selectedColor = hex;
  updateColorDisplay();
}

function paintAt(e, button) {
  const { x, y } = cellFromEvent(e);
  if (x < 0 || y < 0 || x >= GRID || y >= GRID) return;

  if (button === 2) queuePixel(x, y, null);
  else queuePixel(x, y, isEraser ? null : selectedColor);
}

canvas.addEventListener('contextmenu', (e) => e.preventDefault());

// 마우스 다운 핸들러 (스페이스바 패닝 / 스포이드 / 선택 / 그리기)
canvas.addEventListener('mousedown', (e) => {
  if (isSpacePressed || e.button === 1) {
    isPanning = true;
    panStart = {
      x: e.clientX,
      y: e.clientY,
      scrollLeft: scrollContainer ? scrollContainer.scrollLeft : 0,
      scrollTop: scrollContainer ? scrollContainer.scrollTop : 0
    };
    canvas.style.cursor = 'grabbing';
    e.preventDefault();
    return;
  }

  if (e.altKey && e.button === 0) {
    const { x, y } = cellFromEvent(e);
    if (x >= 0 && y >= 0 && x < GRID && y < GRID) {
      const c = pixels[idx(x, y)];
      if (c) {
        selectColor(c);
        if (!userPalette.includes(c)) {
          const empty = userPalette.indexOf(null);
          if (empty !== -1) {
            userPalette[empty] = c;
            renderUserPalette();
          }
        }
        statusEl.textContent = `스포이드 색상 추출: ${c}`;
      }
    }
    e.preventDefault();
    return;
  }

  if (isSelectMode) {
    isSelecting = true;
    selectStart = cellFromEvent(e);
    activeSelection = {
      x: Math.max(0, Math.min(GRID - 1, selectStart.x)),
      y: Math.max(0, Math.min(GRID - 1, selectStart.y)),
      w: 1,
      h: 1,
    };
    updateSelInfo();
    drawGrid();
  } else {
    isDrawing = true;
    paintAt(e, e.button);
  }
});

window.addEventListener('mousemove', (e) => {
  if (isPanning && scrollContainer) {
    const dx = e.clientX - panStart.x;
    const dy = e.clientY - panStart.y;
    scrollContainer.scrollLeft = panStart.scrollLeft - dx;
    scrollContainer.scrollTop = panStart.scrollTop - dy;
    return;
  }

  if (isSelecting && selectStart) {
    const cur = cellFromEvent(e);
    const x0 = Math.max(0, Math.min(GRID - 1, Math.min(selectStart.x, cur.x)));
    const y0 = Math.max(0, Math.min(GRID - 1, Math.min(selectStart.y, cur.y)));
    const x1 = Math.max(0, Math.min(GRID - 1, Math.max(selectStart.x, cur.x)));
    const y1 = Math.max(0, Math.min(GRID - 1, Math.max(selectStart.y, cur.y)));
    activeSelection = { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
    updateSelInfo();
    drawGrid();
  } else if (isDrawing) {
    paintAt(e, e.buttons === 2 ? 2 : 0);
  }
});

window.addEventListener('mouseup', async () => {
  if (isPanning) {
    isPanning = false;
    canvas.style.cursor = isSpacePressed ? 'grab' : 'crosshair';
  }
  if (isSelecting) {
    isSelecting = false;
    selectStart = null;
    await syncSelectionToServer();
  }
  isDrawing = false;
});

// 전역 단축키 핸들러 (Esc, Ctrl+S, Ctrl+Z, Ctrl+Y, Space)
window.addEventListener('keydown', async (e) => {
  if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;

  if (e.key === 'Escape') {
    e.preventDefault();
    await clearSelection();
    setToolMode('select');
    return;
  }

  if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
    e.preventDefault();
    const a = document.createElement('a');
    a.href = '/export.pxp';
    a.download = 'project.pxp';
    a.click();
    statusEl.textContent = '프로젝트(.pxp) 저장 완료';
    return;
  }

  if ((e.ctrlKey || e.metaKey) && !e.shiftKey && (e.key === 'z' || e.key === 'Z')) {
    e.preventDefault();
    await fetch('/undo', { method: 'POST' });
    await syncFromServer();
    return;
  }

  if ((e.ctrlKey || e.metaKey) && ((e.key === 'y' || e.key === 'Y') || (e.shiftKey && (e.key === 'z' || e.key === 'Z')))) {
    e.preventDefault();
    await fetch('/redo', { method: 'POST' });
    await syncFromServer();
    return;
  }

  if (e.code === 'Space') {
    e.preventDefault();
    if (!isSpacePressed) {
      isSpacePressed = true;
      canvas.style.cursor = 'grab';
    }
    return;
  }
});

window.addEventListener('keyup', (e) => {
  if (e.code === 'Space') {
    e.preventDefault();
    isSpacePressed = false;
    isPanning = false;
    canvas.style.cursor = 'crosshair';
  }
});

const btnSymmetry = document.getElementById('btnSymmetry');
if (btnSymmetry) {
  btnSymmetry.addEventListener('click', () => {
    isSymmetry = !isSymmetry;
    btnSymmetry.classList.toggle('active', isSymmetry);
    drawGrid();
    appendLog(isSymmetry ? '🪞 실시간 대칭 그리기 활성화 (세로축)' : '🪞 대칭 그리기 비활성화', 'system');
  });
}

const btnPixelPerfect = document.getElementById('btnPixelPerfect');
if (btnPixelPerfect) {
  btnPixelPerfect.addEventListener('click', () => {
    isPixelPerfect = !isPixelPerfect;
    btnPixelPerfect.classList.toggle('active', isPixelPerfect);
    appendLog(isPixelPerfect ? '✨ Pixel-Perfect 선 보정 활성화' : '✨ Pixel-Perfect 선 보정 비활성화', 'system');
  });
}

const btnExtractPalette = document.getElementById('btnExtractPalette');
if (btnExtractPalette) {
  btnExtractPalette.addEventListener('click', () => {
    const counts = {};
    for (let i = 0; i < GRID * GRID; i++) {
      const c = pixels[i];
      if (c) counts[c] = (counts[c] || 0) + 1;
    }
    const sorted = Object.keys(counts).sort((a, b) => counts[b] - counts[a]);
    userPalette = new Array(USER_SLOTS).fill(null);
    for (let i = 0; i < Math.min(USER_SLOTS, sorted.length); i++) {
      userPalette[i] = sorted[i];
    }
    try {
      localStorage.setItem('pixelpair_user_palette', JSON.stringify(userPalette));
    } catch {}
    renderUserPalette();
    appendLog(`🎨 캔버스 주요 색상 ${Math.min(USER_SLOTS, sorted.length)}개 추출 완료`, 'system');
  });
}

btnPen.addEventListener('click', () => setToolMode('pen'));
btnEraser.addEventListener('click', () => setToolMode('eraser'));
btnSelect.addEventListener('click', () => setToolMode('select'));
btnClearSel.addEventListener('click', clearSelection);

btnUndo.addEventListener('click', async () => {
  await fetch('/undo', { method: 'POST' });
  await syncFromServer();
});

btnRedo.addEventListener('click', async () => {
  await fetch('/redo', { method: 'POST' });
  await syncFromServer();
});

btnClear.addEventListener('click', async () => {
  if (confirm('현재 활성 레이어를 비우시겠습니까?')) {
    await fetch('/clear', { method: 'POST' });
    await syncFromServer();
  }
});

function renderPaletteSelection() {
  document.querySelectorAll('.swatch').forEach((el) => {
    el.classList.toggle('selected', !isEraser && !isSelectMode && el.dataset.color === selectedColor);
  });
}

function renderPico8() {
  const container = document.getElementById('pico8');
  container.innerHTML = '';
  PICO8.forEach((hex) => {
    const sw = document.createElement('div');
    sw.className = 'swatch';
    sw.style.background = hex;
    sw.dataset.color = hex;
    sw.title = hex;
    sw.addEventListener('click', () => {
      selectColor(hex);
      setToolMode('pen');
    });
    container.appendChild(sw);
  });
}

function renderUserPalette() {
  const container = document.getElementById('userPalette');
  container.innerHTML = '';
  userPalette.forEach((hex, i) => {
    const sw = document.createElement('div');
    sw.className = 'swatch' + (hex ? '' : ' empty');
    if (hex) {
      sw.style.background = hex;
      sw.dataset.color = hex;
      sw.title = `${hex} (클릭: 선택 / 우클릭: 일괄 색상 교체)`;
      sw.addEventListener('click', () => {
        selectColor(hex);
        setToolMode('pen');
      });
      sw.addEventListener('contextmenu', async (e) => {
        e.preventDefault();
        const toHex = prompt(`현재 레이어에서 [${hex}] 색상을 어떤 색상으로 일괄 교체(Recolor)할까요?`, selectedColor);
        if (toHex && toHex.trim() && toHex.trim() !== hex) {
          const res = await fetch('/recolor', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ from: hex, to: toHex.trim(), tolerance: 16 }),
          });
          const data = await res.json();
          if (data.ok) {
            appendLog(`🎨 리컬러 완료: ${data.count}칸 교체됨 (${hex} ➔ ${toHex.trim()})`, 'agent');
            await syncFromServer();
          }
        }
      });
    } else {
      sw.title = '빈 슬롯 (Alt+캔버스 클릭으로 스포이드 자동 저장)';
    }
    container.appendChild(sw);
  });
}

async function loadFileContent(file) {
  if (file.name.endsWith('.pxp') || file.name.endsWith('.json')) {
    statusEl.textContent = '프로젝트(.pxp) 로드 중...';
    const text = await file.text();
    try {
      const res = await fetch('/project/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: text,
      });
      const data = await res.json();
      if (data.ok) {
        statusEl.textContent = `프로젝트 로드 완료 (${data.layers_count}개 레이어)`;
        await syncFromServer();
      } else {
        statusEl.textContent = `프로젝트 로드 실패: ${data.error || '오류'}`;
      }
    } catch (err) {
      statusEl.textContent = '프로젝트 로드 중 오류 발생';
      console.error(err);
    }
    return;
  }

  statusEl.textContent = '이미지 분석 및 가져오기 중...';
  const reader = new FileReader();
  reader.onload = async () => {
    const b64 = reader.result.split(',')[1];
    try {
      const res = await fetch('/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ image_base64: b64, knockout_corners: false }),
      });
      const data = await res.json();
      if (data.ok) {
        statusEl.textContent = `가져오기 완료 (${data.count}칸)`;
        await syncFromServer();
      } else {
        statusEl.textContent = `가져오기 실패: ${data.error || '오류'}`;
      }
    } catch (err) {
      statusEl.textContent = '가져오기 중 오류 발생';
      console.error(err);
    }
  };
  reader.readAsDataURL(file);
}

const fileImport = document.getElementById('fileImport');
fileImport.addEventListener('change', async (e) => {
  const file = e.target.files[0];
  if (!file) return;
  await loadFileContent(file);
  fileImport.value = '';
});

const dragOverlay = document.getElementById('dragOverlay');
let dragCounter = 0;

window.addEventListener('dragenter', (e) => {
  e.preventDefault();
  dragCounter++;
  if (dragOverlay) dragOverlay.style.display = 'flex';
});

window.addEventListener('dragleave', (e) => {
  e.preventDefault();
  dragCounter--;
  if (dragCounter <= 0 && dragOverlay) {
    dragOverlay.style.display = 'none';
    dragCounter = 0;
  }
});

window.addEventListener('dragover', (e) => {
  e.preventDefault();
});

window.addEventListener('drop', async (e) => {
  e.preventDefault();
  dragCounter = 0;
  if (dragOverlay) dragOverlay.style.display = 'none';

  if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length > 0) {
    const file = e.dataTransfer.files[0];
    await loadFileContent(file);
  }
});

window.addEventListener('paste', async (e) => {
  if (!e.clipboardData || !e.clipboardData.items) return;
  for (const item of e.clipboardData.items) {
    if (item.type.indexOf('image') !== -1) {
      const file = item.getAsFile();
      if (file) {
        await loadFileContent(file);
        break;
      }
    }
  }
});

const zoomLabel = document.getElementById('zoomLabel');
const btnZoomIn = document.getElementById('btnZoomIn');
const btnZoomOut = document.getElementById('btnZoomOut');
const btnZoomReset = document.getElementById('btnZoomReset');

function setZoom(newZoom) {
  currentZoom = Math.min(maxZoom, Math.max(minZoom, Math.round(newZoom * 100) / 100));
  const displaySize = Math.round(640 * currentZoom);
  canvas.style.width = displaySize + 'px';
  canvas.style.height = displaySize + 'px';
  if (zoomLabel) zoomLabel.textContent = `${Math.round(currentZoom * 100)}%`;

  if (scrollContainer) {
    if (currentZoom <= 1.0) {
      scrollContainer.style.overflow = 'hidden';
      scrollContainer.scrollLeft = 0;
      scrollContainer.scrollTop = 0;
    } else {
      scrollContainer.style.overflow = 'auto';
    }
  }
}

if (btnZoomIn) btnZoomIn.addEventListener('click', () => setZoom(currentZoom + zoomStep));
if (btnZoomOut) btnZoomOut.addEventListener('click', () => setZoom(currentZoom - zoomStep));
if (btnZoomReset) btnZoomReset.addEventListener('click', () => setZoom(1.0));

window.addEventListener('wheel', (e) => {
  const isOverCanvas = e.target === canvas || (e.target.closest && e.target.closest('#canvasScrollContainer'));
  if (e.ctrlKey || isOverCanvas) {
    e.preventDefault();
    const delta = e.deltaY < 0 ? zoomStep : -zoomStep;
    setZoom(currentZoom + delta);
  }
}, { passive: false });

const colorPickerTrigger = document.getElementById('btnColorPickerTrigger');
const nativeColorPicker = document.getElementById('nativeColorPicker');

if (colorPickerTrigger && nativeColorPicker) {
  colorPickerTrigger.addEventListener('click', () => {
    nativeColorPicker.value = selectedColor.startsWith('#') && selectedColor.length === 7 ? selectedColor : '#ff004d';
    nativeColorPicker.click();
  });

  nativeColorPicker.addEventListener('input', (e) => {
    selectColor(e.target.value.toUpperCase());
    setToolMode('pen');
  });
}

const activityLog = document.getElementById('activityLog');

function appendLog(msg, type = 'agent') {
  if (!activityLog) return;
  const now = new Date();
  const timeStr = now.toTimeString().split(' ')[0];
  activityLog.textContent = `[${timeStr}] ${msg}`;
}

function connectSSE() {
  const es = new EventSource('/events');
  es.onmessage = () => {
    syncFromServer();
  };
  es.addEventListener('canvas_changed', () => {
    syncFromServer();
  });
  es.addEventListener('status_changed', (e) => {
    try {
      const data = JSON.parse(e.data);
      const msg = data.message || '대기 중';
      appendLog(msg, 'agent');
    } catch {
      appendLog(e.data, 'agent');
    }
  });
  es.onopen = () => {
    statusEl.textContent = '준비 완료 (AI 연결됨)';
    appendLog('AI 에이전트와 실시간 SSE 연결 완료', 'system');
  };
  es.onerror = () => {
    statusEl.textContent = '연결 끊김, 재연결 중...';
  };
}

renderPico8();
renderUserPalette();
updateColorDisplay();
setZoom(1.0);
setToolMode('select');
syncFromServer();
connectSSE();
