using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelPair
{
    // 64x64 캔버스 상태 + 멀티 레이어 편집 로직 (사람/AI UI에 독립적인 순수 도메인 모델)
    public sealed class CanvasModel
    {
        public const int GridSize = 64;
        private const int MaxUndoSteps = 50;

        public struct SelectionInfo
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int W { get; set; }
            public int H { get; set; }
        }

        private struct UndoState
        {
            public List<Layer> Layers { get; set; }
            public string ActiveLayerId { get; set; }
        }

        private readonly object _pixelLock = new();
        private readonly List<Layer> _layers = new();
        private string _activeLayerId = "";
        private readonly List<UndoState> _undoStack = new();
        private readonly List<UndoState> _redoStack = new();
        private SelectionInfo? _activeSelection = null;

        public event Action? CanvasChanged;
        public event Action<string>? StatusChanged;

        public CanvasModel()
        {
            var baseLayer = new Layer("Layer 1", GridSize);
            _layers.Add(baseLayer);
            _activeLayerId = baseLayer.Id;
        }

        public object SetSelection(int x, int y, int w, int h)
        {
            int rx = Math.Clamp(x, 0, GridSize - 1);
            int ry = Math.Clamp(y, 0, GridSize - 1);
            int rw = Math.Clamp(w, 1, GridSize - rx);
            int rh = Math.Clamp(h, 1, GridSize - ry);
            lock (_pixelLock)
            {
                _activeSelection = new SelectionInfo { X = rx, Y = ry, W = rw, H = rh };
            }
            Log($"사용자 영역 선택: ({rx},{ry}) {rw}x{rh}");
            return new { ok = true, selection = _activeSelection };
        }

        public object ClearSelection()
        {
            lock (_pixelLock)
            {
                _activeSelection = null;
            }
            Log("사용자 영역 선택 해제");
            return new { ok = true, selection = (object?)null };
        }

        public object GetCanvas(bool includePng, bool includePixels, int? x, int? y, int? w, int? h, int? scale)
        {
            bool region = x is int && y is int && w is int ww && ww > 0 && h is int hh && hh > 0;
            int rx = 0, ry = 0, rw = GridSize, rh = GridSize;
            if (region)
            {
                rx = Math.Clamp(x!.Value, 0, GridSize - 1);
                ry = Math.Clamp(y!.Value, 0, GridSize - 1);
                rw = Math.Clamp(w!.Value, 1, GridSize - rx);
                rh = Math.Clamp(h!.Value, 1, GridSize - ry);
            }

            int effectiveScale = Math.Clamp(scale ?? (region ? 8 : 1), 1, 16);

            var snapshot = Snapshot();
            SelectionInfo? sel;
            List<Layer> layersCopy;
            string activeId;
            lock (_pixelLock)
            {
                sel = _activeSelection;
                layersCopy = _layers.Select(l => l.Clone()).ToList();
                activeId = _activeLayerId;
            }

            var summary = JsonSerializer.Deserialize<JsonElement>(BuildCanvasSummary(snapshot, sel, layersCopy, activeId));
            string? png = includePng
                ? Convert.ToBase64String(region
                    ? ImageEngine.RenderRegionPng(snapshot, GridSize, rx, ry, rw, rh, effectiveScale)
                    : ImageEngine.RenderPng(snapshot, GridSize, GridSize * effectiveScale))
                : null;

            string? skippedBg = null;
            int omitted = 0;
            List<PixelInfo>? pixels = null;
            if (includePixels)
            {
                var raw = ListOccupiedPixels(snapshot, rx, ry, rw, rh);
                if (region && raw.Count > 0)
                {
                    var top = raw.GroupBy(p => p.Color, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count())
                        .First();
                    if (top.Count() * 2 >= rw * rh)
                    {
                        skippedBg = top.Key;
                        raw = raw.Where(p => !p.Color.Equals(skippedBg, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                }
                const int cap = 256;
                if (raw.Count > cap)
                {
                    omitted = raw.Count - cap;
                    raw = raw.Take(cap).ToList();
                }
                pixels = raw;
            }

            return new
            {
                ok = true,
                grid = GridSize,
                selection = sel is null ? null : new { x = sel.Value.X, y = sel.Value.Y, w = sel.Value.W, h = sel.Value.H },
                region = region ? new { x = rx, y = ry, w = rw, h = rh } : null,
                skipped_bg = skippedBg,
                omitted,
                summary,
                png_base64 = png,
                pixels
            };
        }

        // ---- 레이어 관리 (Layer Management) ----

        public object GetLayersInfo()
        {
            lock (_pixelLock)
            {
                var list = _layers.Select(l => new
                {
                    id = l.Id,
                    name = l.Name,
                    visible = l.Visible,
                    locked = l.Locked,
                    opacity = Math.Round(l.Opacity, 2),
                    occupied = l.CountOccupied(),
                    png_base64 = Convert.ToBase64String(ImageEngine.RenderPng(l.Pixels, GridSize, 64))
                }).ToList();

                return new
                {
                    ok = true,
                    active_id = _activeLayerId,
                    layers = list
                };
            }
        }

        public object AddLayer(string? name = null)
        {
            SaveUndoState();
            Layer newLayer;
            lock (_pixelLock)
            {
                string layerName = string.IsNullOrWhiteSpace(name) ? $"Layer {_layers.Count + 1}" : name;
                newLayer = new Layer(layerName, GridSize);
                _layers.Add(newLayer);
                _activeLayerId = newLayer.Id;
            }
            RaiseChanged();
            Log($"레이어 추가: {newLayer.Name} ({newLayer.Id})");
            return new { ok = true, layer = new { id = newLayer.Id, name = newLayer.Name }, active_id = _activeLayerId };
        }

        public object DuplicateLayer(string id)
        {
            SaveUndoState();
            Layer duplicated;
            lock (_pixelLock)
            {
                int idx = _layers.FindIndex(l => l.Id == id);
                if (idx < 0) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };

                var src = _layers[idx];
                string newId = Guid.NewGuid().ToString("N")[..8];
                string newName = $"{src.Name} (복사본)";
                duplicated = src.Clone(newId, newName);
                _layers.Insert(idx + 1, duplicated);
                _activeLayerId = duplicated.Id;
            }
            RaiseChanged();
            Log($"레이어 복제: {duplicated.Name}");
            return new { ok = true, active_id = _activeLayerId, layer = new { id = duplicated.Id, name = duplicated.Name } };
        }

        public object MoveLayer(string id, int direction)
        {
            SaveUndoState();
            lock (_pixelLock)
            {
                int idx = _layers.FindIndex(l => l.Id == id);
                if (idx < 0) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };

                int newIdx = idx + direction;
                if (newIdx < 0 || newIdx >= _layers.Count) return new { ok = false, error = "더 이상 이동할 수 없습니다." };

                var item = _layers[idx];
                _layers.RemoveAt(idx);
                _layers.Insert(newIdx, item);
            }
            RaiseChanged();
            Log($"레이어 순서 변경: {id} -> {direction}");
            return new { ok = true, active_id = _activeLayerId };
        }

        public object SetLayerOpacity(string id, float opacity)
        {
            SaveUndoState();
            lock (_pixelLock)
            {
                var target = _layers.FirstOrDefault(l => l.Id == id);
                if (target == null) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };
                target.Opacity = Math.Clamp(opacity, 0.0f, 1.0f);
            }
            RaiseChanged();
            return new { ok = true, id, opacity };
        }

        public object RemoveLayer(string? id = null)
        {
            SaveUndoState();
            string removedName = "";
            lock (_pixelLock)
            {
                if (_layers.Count <= 1)
                    return new { ok = false, error = "최소 1개의 레이어는 유지되어야 합니다." };

                string targetId = id ?? _activeLayerId;
                var target = _layers.FirstOrDefault(l => l.Id == targetId);
                if (target == null) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };

                removedName = target.Name;
                _layers.Remove(target);
                if (_activeLayerId == targetId)
                    _activeLayerId = _layers[^1].Id;
            }
            RaiseChanged();
            Log($"레이어 삭제: {removedName}");
            return new { ok = true, active_id = _activeLayerId };
        }

        public object SelectLayer(string id)
        {
            lock (_pixelLock)
            {
                var target = _layers.FirstOrDefault(l => l.Id == id);
                if (target == null) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };
                _activeLayerId = id;
            }
            RaiseChanged();
            return new { ok = true, active_id = _activeLayerId };
        }

        public object SetLayerVisible(string id, bool visible)
        {
            SaveUndoState();
            lock (_pixelLock)
            {
                var target = _layers.FirstOrDefault(l => l.Id == id);
                if (target == null) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };
                target.Visible = visible;
            }
            RaiseChanged();
            return new { ok = true, id, visible };
        }

        public object SetLayerLocked(string id, bool locked)
        {
            lock (_pixelLock)
            {
                var target = _layers.FirstOrDefault(l => l.Id == id);
                if (target == null) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };
                target.Locked = locked;
            }
            RaiseChanged();
            return new { ok = true, id, locked };
        }

        public object MergeLayerDown(string id)
        {
            lock (_pixelLock)
            {
                int idx = _layers.FindIndex(l => l.Id == id);
                if (idx <= 0) return new { ok = false, error = "아래에 병합할 레이어가 없습니다." };

                var top = _layers[idx];
                var bottom = _layers[idx - 1];
                if (top.Locked || bottom.Locked)
                    return new { ok = false, error = "잠긴 레이어는 병합할 수 없습니다." };

                SaveUndoStateLocked();

                for (int y = 0; y < GridSize; y++)
                {
                    for (int x = 0; x < GridSize; x++)
                    {
                        Rgba32? merged = null;
                        if (bottom.Visible && bottom.Pixels[x, y] is Rgba32 bottomPixel)
                            merged = CompositeOver(merged, bottomPixel, bottom.Opacity);
                        if (top.Visible && top.Pixels[x, y] is Rgba32 topPixel)
                            merged = CompositeOver(merged, topPixel, top.Opacity);
                        bottom.Pixels[x, y] = merged;
                    }
                }

                // The pixel data now includes both layer opacities, so the merged layer must be fully opaque.
                bottom.Opacity = 1.0f;
                bottom.Visible = bottom.Visible || top.Visible;
                bottom.Locked = bottom.Locked || top.Locked;

                _layers.RemoveAt(idx);
                _activeLayerId = bottom.Id;
            }
            RaiseChanged();
            Log($"레이어 아래로 병합: {id}");
            return new { ok = true, active_id = _activeLayerId };
        }

        public object RenameLayer(string id, string name)
        {
            lock (_pixelLock)
            {
                var target = _layers.FirstOrDefault(l => l.Id == id);
                if (target == null) return new { ok = false, error = "해당 레이어를 찾을 수 없습니다." };
                target.Name = string.IsNullOrWhiteSpace(name) ? target.Name : name;
            }
            RaiseChanged();
            return new { ok = true, id, name };
        }

        // ---- 프로젝트 파일 (.pxp) 저장 / 불러오기 ----

        public byte[] ExportProject()
        {
            lock (_pixelLock)
            {
                var projectData = new
                {
                    format = "pixelpair",
                    version = 1,
                    grid = GridSize,
                    active_layer = _activeLayerId,
                    layers = _layers.Select(l => new
                    {
                        id = l.Id,
                        name = l.Name,
                        visible = l.Visible,
                        locked = l.Locked,
                        opacity = l.Opacity,
                        pixels = ListOccupiedPixels(l.Pixels, 0, 0, GridSize, GridSize)
                    }).ToList()
                };
                return JsonSerializer.SerializeToUtf8Bytes(projectData, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        public object ImportProject(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("layers", out var layersProp) || layersProp.ValueKind != JsonValueKind.Array)
                return new { ok = false, error = "유효한 .pxp 프로젝트 형식이 아닙니다." };

            SaveUndoState();
            lock (_pixelLock)
            {
                _layers.Clear();
                foreach (var lEl in layersProp.EnumerateArray())
                {
                    string id = lEl.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    string name = lEl.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "Layer" : "Layer";
                    bool visible = !lEl.TryGetProperty("visible", out var vProp) || vProp.ValueKind != JsonValueKind.False;
                    bool locked = lEl.TryGetProperty("locked", out var lkProp) && lkProp.ValueKind == JsonValueKind.True;
                    float opacity = lEl.TryGetProperty("opacity", out var opProp) && opProp.TryGetSingle(out float op) ? op : 1.0f;

                    var layer = new Layer(name, GridSize, string.IsNullOrEmpty(id) ? null : id)
                    {
                        Visible = visible,
                        Locked = locked,
                        Opacity = opacity
                    };

                    if (lEl.TryGetProperty("pixels", out var pxProp) && pxProp.ValueKind == JsonValueKind.Array)
                    {
                        var pxList = JsonSerializer.Deserialize<List<PixelInfo>>(pxProp.GetRawText()) ?? new();
                        ApplyList(layer.Pixels, pxList, replaceAll: true);
                    }
                    _layers.Add(layer);
                }

                if (_layers.Count == 0)
                    _layers.Add(new Layer("Layer 1", GridSize));

                string active = root.TryGetProperty("active_layer", out var actProp) ? actProp.GetString() ?? "" : "";
                _activeLayerId = _layers.Any(l => l.Id == active) ? active : _layers[^1].Id;
            }
            RaiseChanged();
            Log($"프로젝트 로드 완료: {_layers.Count}개 레이어");
            return new { ok = true, layers_count = _layers.Count, active_id = _activeLayerId };
        }

        // ---- 단독 레이어 내보내기 (Item Asset Export) ----

        public byte[] ExportLayer(string id, string format)
        {
            lock (_pixelLock)
            {
                var target = _layers.FirstOrDefault(l => l.Id == id) ?? GetActiveLayerLocked();
                if (string.Equals(format, "ico", StringComparison.OrdinalIgnoreCase))
                    return ImageEngine.RenderIco(target.Pixels, GridSize, 256);
                return ImageEngine.RenderPng(target.Pixels, GridSize, 512);
            }
        }

        // ---- 그리기 및 변환 도구 (Active Layer 기반) ----

        public byte[] Export(string format)
        {
            var snapshot = Snapshot();
            if (string.Equals(format, "ico", StringComparison.OrdinalIgnoreCase))
                return ImageEngine.RenderIco(snapshot, GridSize, 256);
            return ImageEngine.RenderPng(snapshot, GridSize, 512);
        }

        public byte[] GetCanvasPng() => ImageEngine.RenderPng(Snapshot(), GridSize, GridSize);
        public byte[] GetExportBytes(string format) => Export(format);
        public object SetPixels(string mode, List<PixelInfo> pixels) => ApplyPixels(mode, pixels);

        public object Export(string path, string format)
        {
            var bytes = Export(format);
            File.WriteAllBytes(path, bytes);
            Log($"내보내기: {path} ({bytes.Length}바이트)");
            return new { ok = true, path, format, size = bytes.Length };
        }

        public object Import(byte[] imageBytes, int maxColors = 0, bool knockoutCorners = false)
        {
            var pixels = ImageEngine.DecodeToPixelGrid(imageBytes, GridSize);
            if (maxColors > 0)
                pixels = ImageEngine.LimitColors(pixels, maxColors);

            SaveUndoStateForEditableLayer();
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                active.Clear();
                ApplyList(active.Pixels, pixels, replaceAll: true);
                if (knockoutCorners)
                    KnockoutCorners(active.Pixels, 16);
            }
            RaiseChanged();
            Log($"가져오기: {pixels.Count}칸");
            return new { ok = true, count = pixels.Count };
        }

        public object SetPixels(string mode, List<PixelInfo> pixels, string? symmetry = null)
        {
            SaveUndoStateForEditableLayer();
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                ApplyList(active.Pixels, pixels, string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase), symmetry);
            }
            RaiseChanged();
            return new { ok = true, count = pixels.Count };
        }

        public object ApplyPixels(string mode, List<PixelInfo> pixels, string? symmetry = null) => SetPixels(mode, pixels, symmetry);

        public object Undo()
        {
            PerformUndo();
            RaiseChanged();
            Log("Undo 실행");
            return new { ok = true, undone = true };
        }

        public object Redo()
        {
            PerformRedo();
            RaiseChanged();
            Log("Redo 실행");
            return new { ok = true, redone = true };
        }

        public object ClearCanvas()
        {
            SaveUndoStateForEditableLayer();
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                active.Clear();
            }
            RaiseChanged();
            Log("활성 레이어 비우기");
            return new { ok = true, cleared = true };
        }

        public object FloodErase(int x, int y, int tolerance = 32, int? cx = null, int? cy = null, int? cw = null, int? ch = null)
        {
            ClipRect(cx, cy, cw, ch, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                n = FloodEraseLocked(active.Pixels, x, y, tolerance, false, x0, y0, x1, y1);
            }
            RaiseChanged();
            Log($"에이전트: flood_erase ({x},{y}) {n}칸");
            return new { ok = true, count = n };
        }

        public object Recolor(string fromColor, string toColor, int tolerance = 16, int? cx = null, int? cy = null, int? cw = null, int? ch = null)
        {
            var from = ImageEngine.ParseHex(fromColor);
            if (from is null) return new { ok = false, error = "from 색상이 필요하다." };
            var to = ImageEngine.ParseHex(toColor);

            ClipRect(cx, cy, cw, ch, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        if (active.Pixels[xx, yy] is not Rgba32 c || ImageEngine.ColorDistance(c, from.Value) > tolerance) continue;
                        active.Pixels[xx, yy] = to;
                        n++;
                    }
                }
            }
            RaiseChanged();
            Log($"에이전트: recolor {fromColor}->{toColor} {n}칸");
            return new { ok = true, count = n };
        }

        public object FillRect(int? x, int? y, int? w, int? h, string color, string? symmetry = null)
        {
            ClipRect(x, y, w, h, out int x0, out int y0, out int x1, out int y1);
            var c = ImageEngine.ParseHex(color);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        n += SetPixelWithSymmetry(active.Pixels, xx, yy, c, symmetry);
                    }
                }
            }
            RaiseChanged();
            Log($"에이전트: fill_rect {n}칸" + (!string.IsNullOrEmpty(symmetry) ? $" (대칭:{symmetry})" : ""));
            return new { ok = true, count = n };
        }

        public object DrawLine(int x0, int y0, int x1, int y1, string color, bool pixelPerfect = false, string? symmetry = null)
        {
            var c = ImageEngine.ParseHex(color);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                n = DrawLineLocked(active.Pixels, x0, y0, x1, y1, c, pixelPerfect, symmetry);
            }
            RaiseChanged();
            Log($"에이전트: draw_line {n}칸" + (pixelPerfect ? " (Pixel-Perfect)" : "") + (!string.IsNullOrEmpty(symmetry) ? $" (대칭:{symmetry})" : ""));
            return new { ok = true, count = n };
        }

        public object DrawCircle(int cx, int cy, int radius, string color, bool fill, int? strokeWidth = null, string? symmetry = null)
        {
            var c = ImageEngine.ParseHex(color);
            int r = Math.Max(0, radius);
            int stroke = Math.Max(1, strokeWidth ?? 1);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                n = DrawCircleLocked(active.Pixels, cx, cy, r, c, fill, stroke, symmetry);
            }
            RaiseChanged();
            Log($"에이전트: draw_circle ({cx},{cy}) r={r} stroke={stroke} {n}칸" + (!string.IsNullOrEmpty(symmetry) ? $" (대칭:{symmetry})" : ""));
            return new { ok = true, count = n };
        }

        public object DrawEllipse(int cx, int cy, int rx, int ry, string color, bool fill, int? strokeWidth = null, string? symmetry = null)
        {
            var c = ImageEngine.ParseHex(color);
            int rrx = Math.Max(0, rx);
            int rry = Math.Max(0, ry);
            int stroke = Math.Max(1, strokeWidth ?? 1);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                n = DrawEllipseLocked(active.Pixels, cx, cy, rrx, rry, c, fill, stroke, symmetry);
            }
            RaiseChanged();
            Log($"에이전트: draw_ellipse ({cx},{cy}) rx={rrx} ry={rry} stroke={stroke} {n}칸" + (!string.IsNullOrEmpty(symmetry) ? $" (대칭:{symmetry})" : ""));
            return new { ok = true, count = n };
        }

        public object DrawPolygon(List<(int x, int y)> points, string color, bool fill = true, int? strokeWidth = null, string? symmetry = null)
        {
            var c = ImageEngine.ParseHex(color);
            int stroke = Math.Max(1, strokeWidth ?? 1);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                n = DrawPolygonLocked(active.Pixels, points, c, fill, stroke, symmetry);
            }
            RaiseChanged();
            Log($"에이전트: draw_polygon ({points.Count}점) fill={fill} {n}칸" + (!string.IsNullOrEmpty(symmetry) ? $" (대칭:{symmetry})" : ""));
            return new { ok = true, count = n };
        }

        public object FlipRect(int? x = null, int? y = null, int? w = null, int? h = null)
        {
            ClipRect(x, y, w, h, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int n = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                n = FlipRectLocked(active.Pixels, x0, y0, x1 - x0, y1 - y0);
            }
            RaiseChanged();
            Log($"에이전트: flip_rect ({x0},{y0} {x1-x0}x{y1-y0}) {n}칸");
            return new { ok = true, count = n };
        }

        public object ClearRect(int? x = null, int? y = null, int? w = null, int? h = null)
        {
            return FillRect(x, y, w, h, "");
        }

        public object ShiftRect(int dx, int dy, int? x = null, int? y = null, int? w = null, int? h = null, bool wrap = false)
        {
            ClipRect(x, y, w, h, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int moved;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                moved = ShiftRectLocked(active.Pixels, dx, dy, x0, y0, x1 - x0, y1 - y0, wrap);
            }
            RaiseChanged();
            Log($"에이전트: shift_rect dx={dx}, dy={dy} ({x0},{y0} {x1-x0}x{y1-y0}) {moved}칸 이동");
            return new { ok = true, dx, dy, moved };
        }

        public object RotateRect(int angle, int? x = null, int? y = null, int? w = null, int? h = null)
        {
            ClipRect(x, y, w, h, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int count;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                count = RotateRectLocked(active.Pixels, angle, x0, y0, x1 - x0, y1 - y0);
            }
            RaiseChanged();
            Log($"에이전트: rotate_rect angle={angle} ({x0},{y0} {x1-x0}x{y1-y0}) {count}칸");
            return new { ok = true, angle, count };
        }

        public object RoundCorners(int x, int y, int w, int h, int radius, string? fillColor = null)
        {
            var replacement = string.IsNullOrWhiteSpace(fillColor) ? null : ImageEngine.ParseHex(fillColor);

            SaveUndoStateForEditableLayer();
            int n;
            int r;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                (n, r) = RoundCornersLocked(active.Pixels, x, y, w, h, radius, replacement);
            }
            RaiseChanged();
            Log($"에이전트: round_corners r={r} fill={(replacement is null ? "transparent" : fillColor)} {n}칸");
            return new { ok = true, radius = r, count = n };
        }

        public object CenterCanvas(string? axis = "both")
        {
            SaveUndoStateForEditableLayer();
            int dx, dy;
            int minX, minY, maxX, maxY;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                (dx, dy, minX, minY, maxX, maxY) = CenterCanvasLocked(active.Pixels, axis);
            }
            RaiseChanged();
            Log($"에이전트: center_canvas axis={axis} dx={dx}, dy={dy}");
            return new { ok = true, dx, dy, min_x = minX, min_y = minY, max_x = maxX, max_y = maxY };
        }

        public object ScaleRect(int? x = null, int? y = null, int? w = null, int? h = null, double? scale = null, double? scaleX = null, double? scaleY = null, int? targetW = null, int? targetH = null, bool center = true)
        {
            SaveUndoStateForEditableLayer();
            int count, srcW, srcH, dstW, dstH;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                (count, srcW, srcH, dstW, dstH) = ScaleRectLocked(active.Pixels, x, y, w, h, scale, scaleX, scaleY, targetW, targetH, center);
            }
            RaiseChanged();
            Log($"에이전트: scale_rect {srcW}x{srcH} -> {dstW}x{dstH} ({count}픽셀)");
            return new { ok = true, count, src_w = srcW, src_h = srcH, dst_w = dstW, dst_h = dstH };
        }

        public object OutlineObject(string color, bool diagonal = false, int? x = null, int? y = null, int? w = null, int? h = null)
        {
            var outlineCol = ImageEngine.ParseHex(color) ?? new Rgba32(0, 0, 0, 255);
            ClipRect(x, y, w, h, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int count;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                count = OutlineObjectLocked(active.Pixels, outlineCol, diagonal, x0, y0, x1 - x0, y1 - y0);
            }
            RaiseChanged();
            Log($"에이전트: outline_object color={color} diag={diagonal} ({count}픽셀)");
            return new { ok = true, count, color };
        }

        public object DropShadow(int dx = 0, int dy = 4, string? color = null, string type = "object", int? x = null, int? y = null, int? w = null, int? h = null)
        {
            var shadowCol = ImageEngine.ParseHex(color ?? "#1E2022") ?? new Rgba32(30, 32, 34, 255);
            ClipRect(x, y, w, h, out int x0, out int y0, out int x1, out int y1);
            SaveUndoStateForEditableLayer();
            int count;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                count = DropShadowLocked(active.Pixels, dx, dy, shadowCol, type, x0, y0, x1 - x0, y1 - y0);
            }
            RaiseChanged();
            Log($"에이전트: drop_shadow dx={dx}, dy={dy} type={type} ({count}픽셀)");
            return new { ok = true, count, dx, dy, type };
        }

        public object ApplyOperations(JsonElement operations)
        {
            if (operations.ValueKind != JsonValueKind.Array)
                return new { ok = false, error = "operations 배열이 필요하다." };

            int applied = 0;
            lock (_pixelLock)
            {
                var active = GetEditableActiveLayerLocked();
                var beforeLayers = _layers.Select(l => l.Clone()).ToList();
                var beforeActive = _activeLayerId;
                try
                {
                    foreach (var operation in operations.EnumerateArray())
                    {
                        ApplyOperationLocked(active.Pixels, operation);
                        applied++;
                    }
                    _undoStack.Add(new UndoState { Layers = beforeLayers, ActiveLayerId = beforeActive });
                    if (_undoStack.Count > MaxUndoSteps) _undoStack.RemoveAt(0);
                    _redoStack.Clear();
                }
                catch (Exception ex) when (ex is ArgumentException or JsonException)
                {
                    _layers.Clear();
                    _layers.AddRange(beforeLayers);
                    _activeLayerId = beforeActive;
                    return new { ok = false, error = ex.Message, applied = 0 };
                }
            }

            RaiseChanged();
            Log($"에이전트: apply_operations {applied}개 작업 반영.");
            return new { ok = true, applied };
        }

        // ---- 내부 로직 ----

        private Layer GetActiveLayerLocked()
        {
            return _layers.FirstOrDefault(l => l.Id == _activeLayerId) ?? _layers[^1];
        }

        private Layer GetEditableActiveLayerLocked()
        {
            var active = GetActiveLayerLocked();
            if (active.Locked)
                throw new InvalidOperationException($"레이어 '{active.Name}'이 잠겨 있어 수정할 수 없습니다.");
            return active;
        }

        private void ApplyOperationLocked(Rgba32?[,] grid, JsonElement operation)
        {
            if (operation.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("각 operation은 객체여야 한다.");
            string op = OperationString(operation, "op") ?? throw new ArgumentException("operation.op가 필요하다.");

            string? sym = OperationString(operation, "symmetry");
            bool pixelPerfect = OperationBool(operation, "pixel_perfect");

            switch (op)
            {
                case "fill_rect":
                    FillRectLocked(grid, OperationInt(operation, "x"), OperationInt(operation, "y"), OperationInt(operation, "w", 1), OperationInt(operation, "h", 1), ImageEngine.ParseHex(OperationString(operation, "color") ?? ""), sym);
                    return;
                case "clear_rect":
                    int? cx = OperationNullableInt(operation, "x"), cy = OperationNullableInt(operation, "y"), cw = OperationNullableInt(operation, "w"), ch = OperationNullableInt(operation, "h");
                    ClipRect(cx, cy, cw, ch, out int clx0, out int cly0, out int clx1, out int cly1);
                    FillRectLocked(grid, clx0, cly0, clx1 - clx0, cly1 - cly0, null, sym);
                    return;
                case "flip_rect":
                    int? fx = OperationNullableInt(operation, "x"), fy = OperationNullableInt(operation, "y"), fw = OperationNullableInt(operation, "w"), fh = OperationNullableInt(operation, "h");
                    ClipRect(fx, fy, fw, fh, out int flx0, out int fly0, out int flx1, out int fly1);
                    FlipRectLocked(grid, flx0, fly0, flx1 - flx0, fly1 - fly0);
                    return;
                case "shift_rect":
                    int? sx = OperationNullableInt(operation, "x"), sy = OperationNullableInt(operation, "y"), sw = OperationNullableInt(operation, "w"), sh = OperationNullableInt(operation, "h");
                    ClipRect(sx, sy, sw, sh, out int shx0, out int shy0, out int shx1, out int shy1);
                    ShiftRectLocked(grid, OperationInt(operation, "dx"), OperationInt(operation, "dy"), shx0, shy0, shx1 - shx0, shy1 - shy0, OperationBool(operation, "wrap"));
                    return;
                case "rotate_rect":
                    int? rx = OperationNullableInt(operation, "x"), ry = OperationNullableInt(operation, "y"), rw = OperationNullableInt(operation, "w"), rh = OperationNullableInt(operation, "h");
                    ClipRect(rx, ry, rw, rh, out int rtx0, out int rty0, out int rtx1, out int rty1);
                    RotateRectLocked(grid, OperationInt(operation, "angle", 90), rtx0, rty0, rtx1 - rtx0, rty1 - rty0);
                    return;
                case "draw_line":
                    DrawLineLocked(grid, OperationInt(operation, "x0"), OperationInt(operation, "y0"), OperationInt(operation, "x1"), OperationInt(operation, "y1"), ImageEngine.ParseHex(OperationString(operation, "color") ?? ""), pixelPerfect, sym);
                    return;
                case "draw_circle":
                    DrawCircleLocked(grid, OperationInt(operation, "x"), OperationInt(operation, "y"), Math.Max(0, OperationInt(operation, "radius", 1)), ImageEngine.ParseHex(OperationString(operation, "color") ?? ""), OperationBool(operation, "fill"), Math.Max(1, OperationInt(operation, "stroke_width", 1)), sym);
                    return;
                case "draw_ellipse":
                    DrawEllipseLocked(grid, OperationInt(operation, "x"), OperationInt(operation, "y"), Math.Max(0, OperationInt(operation, "rx", 1)), Math.Max(0, OperationInt(operation, "ry", 1)), ImageEngine.ParseHex(OperationString(operation, "color") ?? ""), OperationBool(operation, "fill"), Math.Max(1, OperationInt(operation, "stroke_width", 1)), sym);
                    return;
                case "draw_polygon":
                    var pts = new List<(int x, int y)>();
                    if (operation.TryGetProperty("points", out var pointsEl) && pointsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in pointsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                            {
                                pts.Add((item[0].GetInt32(), item[1].GetInt32()));
                            }
                            else if (item.ValueKind == JsonValueKind.Object)
                            {
                                pts.Add((OperationInt(item, "x"), OperationInt(item, "y")));
                            }
                        }
                    }
                    DrawPolygonLocked(grid, pts, ImageEngine.ParseHex(OperationString(operation, "color") ?? ""), OperationBool(operation, "fill", true), Math.Max(1, OperationInt(operation, "stroke_width", 1)), sym);
                    return;
                case "round_corners":
                    string? fillColor = OperationString(operation, "fill_color");
                    RoundCornersLocked(grid, OperationInt(operation, "x"), OperationInt(operation, "y"), OperationInt(operation, "w", 1), OperationInt(operation, "h", 1), OperationInt(operation, "radius", 4), string.IsNullOrWhiteSpace(fillColor) ? null : ImageEngine.ParseHex(fillColor));
                    return;
                case "recolor":
                    RecolorLocked(grid, OperationString(operation, "from") ?? throw new ArgumentException("recolor.from이 필요하다."), OperationString(operation, "to") ?? "", OperationInt(operation, "tolerance", 16), operation);
                    return;
                case "set_pixels":
                    if (!operation.TryGetProperty("pixels", out var pixels) || pixels.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("set_pixels.pixels 배열이 필요하다.");
                    var list = JsonSerializer.Deserialize<List<PixelInfo>>(pixels.GetRawText()) ?? new();
                    ApplyList(grid, list, string.Equals(OperationString(operation, "mode"), "full", StringComparison.OrdinalIgnoreCase), sym);
                    return;
                case "center_canvas":
                    CenterCanvasLocked(grid, OperationString(operation, "axis") ?? "both");
                    return;
                case "scale_rect":
                    int? scx = OperationNullableInt(operation, "x"), scy = OperationNullableInt(operation, "y"), scw = OperationNullableInt(operation, "w"), sch = OperationNullableInt(operation, "h");
                    double? scale = OperationNullableDouble(operation, "scale");
                    double? scaleX = OperationNullableDouble(operation, "scale_x");
                    double? scaleY = OperationNullableDouble(operation, "scale_y");
                    int? targetW = OperationNullableInt(operation, "target_w");
                    int? targetH = OperationNullableInt(operation, "target_h");
                    bool center = OperationBool(operation, "center", true);
                    ScaleRectLocked(grid, scx, scy, scw, sch, scale, scaleX, scaleY, targetW, targetH, center);
                    return;
                case "outline_object":
                    int? ox = OperationNullableInt(operation, "x"), oy = OperationNullableInt(operation, "y"), ow = OperationNullableInt(operation, "w"), oh = OperationNullableInt(operation, "h");
                    ClipRect(ox, oy, ow, oh, out int ox0, out int oy0, out int ox1, out int oy1);
                    var oCol = ImageEngine.ParseHex(OperationString(operation, "color") ?? "#000000") ?? new Rgba32(0, 0, 0, 255);
                    OutlineObjectLocked(grid, oCol, OperationBool(operation, "diagonal"), ox0, oy0, ox1 - ox0, oy1 - oy0);
                    return;
                case "drop_shadow":
                    int? dsx = OperationNullableInt(operation, "x"), dsy = OperationNullableInt(operation, "y"), dsw = OperationNullableInt(operation, "w"), dsh = OperationNullableInt(operation, "h");
                    ClipRect(dsx, dsy, dsw, dsh, out int dsx0, out int dsy0, out int dsx1, out int dsy1);
                    var dsCol = ImageEngine.ParseHex(OperationString(operation, "color") ?? "#1E2022") ?? new Rgba32(30, 32, 34, 255);
                    DropShadowLocked(grid, OperationInt(operation, "dx", 0), OperationInt(operation, "dy", 4), dsCol, OperationString(operation, "type") ?? "object", dsx0, dsy0, dsx1 - dsx0, dsy1 - dsy0);
                    return;
                default:
                    throw new ArgumentException($"지원하지 않는 operation: {op}");
            }
        }

        private int SetPixelWithSymmetry(Rgba32?[,] grid, int x, int y, Rgba32? color, string? symmetry = null)
        {
            if (x < 0 || x >= GridSize || y < 0 || y >= GridSize) return 0;
            int count = 0;
            grid[x, y] = color;
            count++;

            bool symV = string.Equals(symmetry, "vertical", StringComparison.OrdinalIgnoreCase) || string.Equals(symmetry, "both", StringComparison.OrdinalIgnoreCase) || string.Equals(symmetry, "v", StringComparison.OrdinalIgnoreCase) || string.Equals(symmetry, "x", StringComparison.OrdinalIgnoreCase);
            bool symH = string.Equals(symmetry, "horizontal", StringComparison.OrdinalIgnoreCase) || string.Equals(symmetry, "both", StringComparison.OrdinalIgnoreCase) || string.Equals(symmetry, "h", StringComparison.OrdinalIgnoreCase) || string.Equals(symmetry, "y", StringComparison.OrdinalIgnoreCase);

            if (symV)
            {
                int mx = GridSize - 1 - x;
                if (mx != x && mx >= 0 && mx < GridSize)
                {
                    grid[mx, y] = color;
                    count++;
                }
            }
            if (symH)
            {
                int my = GridSize - 1 - y;
                if (my != y && my >= 0 && my < GridSize)
                {
                    grid[x, my] = color;
                    count++;
                }
            }
            if (symV && symH)
            {
                int mx = GridSize - 1 - x;
                int my = GridSize - 1 - y;
                if (mx >= 0 && mx < GridSize && my >= 0 && my < GridSize)
                {
                    grid[mx, my] = color;
                    count++;
                }
            }
            return count;
        }

        private static List<(int x, int y)> FilterPixelPerfect(List<(int x, int y)> path)
        {
            if (path.Count <= 2) return path;
            var result = new List<(int x, int y)> { path[0] };
            for (int i = 1; i < path.Count - 1; i++)
            {
                var prev = path[i - 1];
                var curr = path[i];
                var next = path[i + 1];
                bool isLCorner = (prev.x == curr.x && curr.y == next.y && prev.x != next.x && prev.y != next.y) ||
                                 (prev.y == curr.y && curr.x == next.x && prev.x != next.x && prev.y != next.y);
                if (!isLCorner) result.Add(curr);
            }
            result.Add(path[^1]);
            return result;
        }

        private int FillRectLocked(Rgba32?[,] grid, int x, int y, int w, int h, Rgba32? color, string? symmetry = null)
        {
            int x0 = Math.Clamp(x, 0, GridSize - 1), y0 = Math.Clamp(y, 0, GridSize - 1);
            int x1 = Math.Clamp(x + Math.Max(1, w), 1, GridSize), y1 = Math.Clamp(y + Math.Max(1, h), 1, GridSize);
            int n = 0;
            for (int yy = y0; yy < y1; yy++)
            {
                for (int xx = x0; xx < x1; xx++)
                {
                    n += SetPixelWithSymmetry(grid, xx, yy, color, symmetry);
                }
            }
            return n;
        }

        private int FlipRectLocked(Rgba32?[,] grid, int x, int y, int w, int h)
        {
            int x0 = Math.Clamp(x, 0, GridSize - 1), y0 = Math.Clamp(y, 0, GridSize - 1);
            int x1 = Math.Clamp(x0 + Math.Max(1, w), 1, GridSize), y1 = Math.Clamp(y0 + Math.Max(1, h), 1, GridSize);
            int actualW = x1 - x0;
            int n = 0;
            for (int yy = y0; yy < y1; yy++)
            {
                for (int dx = 0; dx < actualW / 2; dx++)
                {
                    int leftX = x0 + dx;
                    int rightX = x1 - 1 - dx;
                    var temp = grid[leftX, yy];
                    grid[leftX, yy] = grid[rightX, yy];
                    grid[rightX, yy] = temp;
                    n += 2;
                }
            }
            return n;
        }

        private int ShiftRectLocked(Rgba32?[,] grid, int dx, int dy, int x, int y, int w, int h, bool wrap)
        {
            int x0 = Math.Clamp(x, 0, GridSize - 1), y0 = Math.Clamp(y, 0, GridSize - 1);
            int x1 = Math.Clamp(x0 + Math.Max(1, w), 1, GridSize), y1 = Math.Clamp(y0 + Math.Max(1, h), 1, GridSize);
            int actualW = x1 - x0, actualH = y1 - y0;
            if (actualW <= 0 || actualH <= 0 || (dx == 0 && dy == 0)) return 0;

            var copy = new Rgba32?[actualW, actualH];
            int count = 0;
            for (int yy = 0; yy < actualH; yy++)
            {
                for (int xx = 0; xx < actualW; xx++)
                {
                    copy[xx, yy] = grid[x0 + xx, y0 + yy];
                    if (copy[xx, yy] != null) count++;
                }
            }

            for (int yy = 0; yy < actualH; yy++)
            {
                for (int xx = 0; xx < actualW; xx++)
                {
                    int srcX = xx - dx;
                    int srcY = yy - dy;
                    if (wrap)
                    {
                        srcX = ((srcX % actualW) + actualW) % actualW;
                        srcY = ((srcY % actualH) + actualH) % actualH;
                        grid[x0 + xx, y0 + yy] = copy[srcX, srcY];
                    }
                    else
                    {
                        if (srcX >= 0 && srcX < actualW && srcY >= 0 && srcY < actualH)
                            grid[x0 + xx, y0 + yy] = copy[srcX, srcY];
                        else
                            grid[x0 + xx, y0 + yy] = null;
                    }
                }
            }
            return count;
        }

        private int RotateRectLocked(Rgba32?[,] grid, int angle, int x, int y, int w, int h)
        {
            int x0 = Math.Clamp(x, 0, GridSize - 1), y0 = Math.Clamp(y, 0, GridSize - 1);
            int x1 = Math.Clamp(x0 + Math.Max(1, w), 1, GridSize), y1 = Math.Clamp(y0 + Math.Max(1, h), 1, GridSize);
            int actualW = x1 - x0, actualH = y1 - y0;
            if (actualW <= 0 || actualH <= 0) return 0;

            int normAngle = ((angle % 360) + 360) % 360;
            if (normAngle != 90 && normAngle != 180 && normAngle != 270) return 0;

            var copy = new Rgba32?[actualW, actualH];
            int count = 0;
            for (int yy = 0; yy < actualH; yy++)
            {
                for (int xx = 0; xx < actualW; xx++)
                {
                    copy[xx, yy] = grid[x0 + xx, y0 + yy];
                    if (copy[xx, yy] != null) count++;
                }
            }

            for (int yy = 0; yy < actualH; yy++)
            {
                for (int xx = 0; xx < actualW; xx++)
                {
                    if (normAngle == 180)
                    {
                        grid[x0 + xx, y0 + yy] = copy[actualW - 1 - xx, actualH - 1 - yy];
                    }
                    else if (normAngle == 90 && actualW == actualH)
                    {
                        grid[x0 + xx, y0 + yy] = copy[yy, actualW - 1 - xx];
                    }
                    else if (normAngle == 270 && actualW == actualH)
                    {
                        grid[x0 + xx, y0 + yy] = copy[actualH - 1 - yy, xx];
                    }
                }
            }
            return count;
        }

        private (int dx, int dy, int minX, int minY, int maxX, int maxY) CenterCanvasLocked(Rgba32?[,] grid, string? axis = "both")
        {
            int minX = GridSize, minY = GridSize, maxX = -1, maxY = -1;
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (grid[x, y] != null)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return (0, 0, 0, 0, 0, 0);
            }

            int bw = maxX - minX + 1;
            int bh = maxY - minY + 1;
            int targetX = (GridSize - bw) / 2;
            int targetY = (GridSize - bh) / 2;

            int dx = targetX - minX;
            int dy = targetY - minY;

            bool hOnly = string.Equals(axis, "horizontal", StringComparison.OrdinalIgnoreCase) || string.Equals(axis, "x", StringComparison.OrdinalIgnoreCase) || string.Equals(axis, "h", StringComparison.OrdinalIgnoreCase);
            bool vOnly = string.Equals(axis, "vertical", StringComparison.OrdinalIgnoreCase) || string.Equals(axis, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(axis, "v", StringComparison.OrdinalIgnoreCase);

            if (hOnly) dy = 0;
            if (vOnly) dx = 0;

            if (dx != 0 || dy != 0)
            {
                ShiftRectLocked(grid, dx, dy, 0, 0, GridSize, GridSize, wrap: false);
            }

            return (dx, dy, minX, minY, maxX, maxY);
        }

        private (int count, int srcW, int srcH, int dstW, int dstH) ScaleRectLocked(Rgba32?[,] grid, int? x, int? y, int? w, int? h, double? scale, double? scaleX, double? scaleY, int? targetW, int? targetH, bool center = true)
        {
            int srcX0, srcY0, srcX1, srcY1;
            if (x.HasValue && y.HasValue && w.HasValue && h.HasValue)
            {
                ClipRect(x, y, w, h, out srcX0, out srcY0, out srcX1, out srcY1);
            }
            else
            {
                int minX = GridSize, minY = GridSize, maxX = -1, maxY = -1;
                for (int yy = 0; yy < GridSize; yy++)
                {
                    for (int xx = 0; xx < GridSize; xx++)
                    {
                        if (grid[xx, yy] != null)
                        {
                            if (xx < minX) minX = xx;
                            if (xx > maxX) maxX = xx;
                            if (yy < minY) minY = yy;
                            if (yy > maxY) maxY = yy;
                        }
                    }
                }
                if (maxX < minX || maxY < minY)
                {
                    return (0, 0, 0, 0, 0);
                }
                srcX0 = minX;
                srcY0 = minY;
                srcX1 = maxX + 1;
                srcY1 = maxY + 1;
            }

            int srcW = srcX1 - srcX0;
            int srcH = srcY1 - srcY0;
            if (srcW <= 0 || srcH <= 0) return (0, 0, 0, 0, 0);

            double sx = scaleX ?? scale ?? 1.0;
            double sy = scaleY ?? scale ?? 1.0;

            int dstW = targetW ?? Math.Clamp((int)Math.Round(srcW * sx), 1, GridSize);
            int dstH = targetH ?? Math.Clamp((int)Math.Round(srcH * sy), 1, GridSize);

            var copy = new Rgba32?[srcW, srcH];
            for (int yy = 0; yy < srcH; yy++)
            {
                for (int xx = 0; xx < srcW; xx++)
                {
                    copy[xx, yy] = grid[srcX0 + xx, srcY0 + yy];
                    grid[srcX0 + xx, srcY0 + yy] = null;
                }
            }

            int dstX = center ? (GridSize - dstW) / 2 : srcX0;
            int dstY = center ? (GridSize - dstH) / 2 : srcY0;

            int count = 0;
            for (int ty = 0; ty < dstH; ty++)
            {
                int syPos = Math.Clamp((int)(ty * srcH / (double)dstH), 0, srcH - 1);
                int outY = dstY + ty;
                if (outY < 0 || outY >= GridSize) continue;

                for (int tx = 0; tx < dstW; tx++)
                {
                    int sxPos = Math.Clamp((int)(tx * srcW / (double)dstW), 0, srcW - 1);
                    int outX = dstX + tx;
                    if (outX < 0 || outX >= GridSize) continue;

                    var c = copy[sxPos, syPos];
                    if (c != null)
                    {
                        grid[outX, outY] = c;
                        count++;
                    }
                }
            }

            return (count, srcW, srcH, dstW, dstH);
        }

        private int OutlineObjectLocked(Rgba32?[,] grid, Rgba32 outlineColor, bool diagonal, int x0, int y0, int w, int h)
        {
            int x1 = Math.Min(GridSize, x0 + w);
            int y1 = Math.Min(GridSize, y0 + h);
            var toFill = new List<(int x, int y)>();

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (grid[x, y] != null) continue;

                    bool hasNeighbor = false;
                    if (x > 0 && grid[x - 1, y] != null) hasNeighbor = true;
                    else if (x < GridSize - 1 && grid[x + 1, y] != null) hasNeighbor = true;
                    else if (y > 0 && grid[x, y - 1] != null) hasNeighbor = true;
                    else if (y < GridSize - 1 && grid[x, y + 1] != null) hasNeighbor = true;

                    if (!hasNeighbor && diagonal)
                    {
                        if (x > 0 && y > 0 && grid[x - 1, y - 1] != null) hasNeighbor = true;
                        else if (x < GridSize - 1 && y > 0 && grid[x + 1, y - 1] != null) hasNeighbor = true;
                        else if (x > 0 && y < GridSize - 1 && grid[x - 1, y + 1] != null) hasNeighbor = true;
                        else if (x < GridSize - 1 && y < GridSize - 1 && grid[x + 1, y + 1] != null) hasNeighbor = true;
                    }

                    if (hasNeighbor)
                    {
                        toFill.Add((x, y));
                    }
                }
            }

            foreach (var (fx, fy) in toFill)
            {
                grid[fx, fy] = outlineColor;
            }

            return toFill.Count;
        }

        private int DropShadowLocked(Rgba32?[,] grid, int dx, int dy, Rgba32 shadowColor, string type, int x0, int y0, int w, int h)
        {
            int count = 0;
            if (string.Equals(type, "ground", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "floor", StringComparison.OrdinalIgnoreCase))
            {
                int minX = GridSize, maxX = -1, maxY = -1;
                for (int y = 0; y < GridSize; y++)
                {
                    for (int x = 0; x < GridSize; x++)
                    {
                        if (grid[x, y] != null)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX < minX || maxY < 0) return 0;

                int cx = (minX + maxX) / 2 + dx;
                int cy = Math.Min(GridSize - 1, maxY + dy);
                int rx = Math.Max(2, (maxX - minX + 1) / 2);
                int ry = Math.Max(1, rx / 4);

                count += DrawEllipseLocked(grid, cx, cy, rx, ry, shadowColor, fill: true, strokeWidth: 1);
            }
            else
            {
                var shadowPixels = new List<(int x, int y)>();
                for (int y = y0; y < Math.Min(GridSize, y0 + h); y++)
                {
                    for (int x = x0; x < Math.Min(GridSize, x0 + w); x++)
                    {
                        if (grid[x, y] != null)
                        {
                            int sx = x + dx;
                            int sy = y + dy;
                            if (sx >= 0 && sx < GridSize && sy >= 0 && sy < GridSize)
                            {
                                shadowPixels.Add((sx, sy));
                            }
                        }
                    }
                }

                foreach (var (sx, sy) in shadowPixels)
                {
                    if (grid[sx, sy] == null)
                    {
                        grid[sx, sy] = shadowColor;
                        count++;
                    }
                }
            }

            return count;
        }

        private int DrawLineLocked(Rgba32?[,] grid, int x0, int y0, int x1, int y1, Rgba32? color, bool pixelPerfect = false, string? symmetry = null)
        {
            var rawPts = Bresenham(x0, y0, x1, y1).ToList();
            var pts = pixelPerfect ? FilterPixelPerfect(rawPts) : rawPts;
            int n = 0;
            foreach (var (xx, yy) in pts)
            {
                if (xx < 0 || yy < 0 || xx >= GridSize || yy >= GridSize) continue;
                n += SetPixelWithSymmetry(grid, xx, yy, color, symmetry);
            }
            return n;
        }

        private int DrawCircleLocked(Rgba32?[,] grid, int cx, int cy, int radius, Rgba32? color, bool fill, int strokeWidth, string? symmetry = null)
        {
            int n = 0;
            int x0 = Math.Max(0, cx - radius - strokeWidth);
            int x1 = Math.Min(GridSize - 1, cx + radius + strokeWidth);
            int y0 = Math.Max(0, cy - radius - strokeWidth);
            int y1 = Math.Min(GridSize - 1, cy + radius + strokeWidth);

            int r2 = radius * radius;
            int innerR = Math.Max(0, radius - strokeWidth + 1);
            int innerR2 = innerR * innerR;

            for (int yy = y0; yy <= y1; yy++)
            {
                for (int xx = x0; xx <= x1; xx++)
                {
                    int dx = xx - cx;
                    int dy = yy - cy;
                    int dist2 = dx * dx + dy * dy;

                    bool hit = fill
                        ? dist2 <= r2 + radius
                        : (strokeWidth == 1
                            ? Math.Abs(Math.Sqrt(dist2) - radius) <= 0.75
                            : dist2 <= r2 + radius && dist2 >= innerR2 - innerR);

                    if (!hit) continue;
                    n += SetPixelWithSymmetry(grid, xx, yy, color, symmetry);
                }
            }
            return n;
        }

        private int DrawEllipseLocked(Rgba32?[,] grid, int cx, int cy, int rx, int ry, Rgba32? color, bool fill, int strokeWidth, string? symmetry = null)
        {
            if (rx <= 0 || ry <= 0) return 0;

            int n = 0;
            int x0 = Math.Max(0, cx - rx - strokeWidth);
            int x1 = Math.Min(GridSize - 1, cx + rx + strokeWidth);
            int y0 = Math.Max(0, cy - ry - strokeWidth);
            int y1 = Math.Min(GridSize - 1, cy + ry + strokeWidth);

            double rx2 = (double)rx * rx;
            double ry2 = (double)ry * ry;

            for (int yy = y0; yy <= y1; yy++)
            {
                for (int xx = x0; xx <= x1; xx++)
                {
                    double dx = xx - cx;
                    double dy = yy - cy;
                    double normDist = (dx * dx) / rx2 + (dy * dy) / ry2;

                    bool hit;
                    if (fill)
                    {
                        hit = normDist <= 1.0 + (0.5 / Math.Max(rx, ry));
                    }
                    else
                    {
                        if (strokeWidth == 1)
                        {
                            hit = Math.Abs(Math.Sqrt(normDist) - 1.0) <= (0.75 / Math.Max(rx, ry));
                        }
                        else
                        {
                            double innerRx = Math.Max(0.1, rx - strokeWidth + 1);
                            double innerRy = Math.Max(0.1, ry - strokeWidth + 1);
                            double innerNormDist = (dx * dx) / (innerRx * innerRx) + (dy * dy) / (innerRy * innerRy);
                            hit = normDist <= 1.0 + (0.5 / Math.Max(rx, ry)) && innerNormDist > 1.0 - (0.5 / Math.Max(innerRx, innerRy));
                        }
                    }

                    if (!hit) continue;
                    n += SetPixelWithSymmetry(grid, xx, yy, color, symmetry);
                }
            }
            return n;
        }

        private int DrawPolygonEdgeLocked(Rgba32?[,] grid, int x0, int y0, int x1, int y1, Rgba32? color, int strokeWidth, string? symmetry = null)
        {
            int n = 0;
            var pts = Bresenham(x0, y0, x1, y1);
            int half = Math.Max(0, (strokeWidth - 1) / 2);

            foreach (var (px, py) in pts)
            {
                if (strokeWidth <= 1)
                {
                    n += SetPixelWithSymmetry(grid, px, py, color, symmetry);
                }
                else
                {
                    for (int dy = -half; dy <= strokeWidth - 1 - half; dy++)
                    {
                        for (int dx = -half; dx <= strokeWidth - 1 - half; dx++)
                        {
                            n += SetPixelWithSymmetry(grid, px + dx, py + dy, color, symmetry);
                        }
                    }
                }
            }
            return n;
        }

        private int DrawPolygonLocked(Rgba32?[,] grid, List<(int x, int y)> points, Rgba32? color, bool fill, int strokeWidth, string? symmetry = null)
        {
            if (points == null || points.Count < 2) return 0;
            int count = 0;
            int stroke = Math.Max(1, strokeWidth);

            if (!fill || points.Count < 3)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var p0 = points[i];
                    var p1 = points[(i + 1) % points.Count];
                    count += DrawPolygonEdgeLocked(grid, p0.x, p0.y, p1.x, p1.y, color, stroke, symmetry);
                }
                return count;
            }

            int minY = GridSize, maxY = -1;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].y < minY) minY = points[i].y;
                if (points[i].y > maxY) maxY = points[i].y;
            }
            minY = Math.Max(0, minY);
            maxY = Math.Min(GridSize - 1, maxY);

            var intersections = new List<double>();
            int n = points.Count;

            for (int y = minY; y <= maxY; y++)
            {
                intersections.Clear();
                for (int i = 0; i < n; i++)
                {
                    var p1 = points[i];
                    var p2 = points[(i + 1) % n];

                    if ((p1.y <= y && p2.y > y) || (p2.y <= y && p1.y > y))
                    {
                        if (p1.y != p2.y)
                        {
                            double x = p1.x + (double)(y - p1.y) / (p2.y - p1.y) * (p2.x - p1.x);
                            intersections.Add(x);
                        }
                    }
                }

                intersections.Sort();

                for (int i = 0; i < intersections.Count - 1; i += 2)
                {
                    int xStart = (int)Math.Round(intersections[i]);
                    int xEnd = (int)Math.Round(intersections[i + 1]);

                    xStart = Math.Max(0, xStart);
                    xEnd = Math.Min(GridSize - 1, xEnd);

                    for (int x = xStart; x <= xEnd; x++)
                    {
                        count += SetPixelWithSymmetry(grid, x, y, color, symmetry);
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                var p0 = points[i];
                var p1 = points[(i + 1) % n];
                count += DrawPolygonEdgeLocked(grid, p0.x, p0.y, p1.x, p1.y, color, stroke, symmetry);
            }

            return count;
        }

        private (int count, int radius) RoundCornersLocked(Rgba32?[,] grid, int x, int y, int w, int h, int radius, Rgba32? replacement)
        {
            int x0 = Math.Clamp(x, 0, GridSize - 1), y0 = Math.Clamp(y, 0, GridSize - 1);
            int x1 = Math.Clamp(x0 + Math.Max(1, w), 1, GridSize), y1 = Math.Clamp(y0 + Math.Max(1, h), 1, GridSize);
            int r = Math.Clamp(radius, 0, Math.Min(x1 - x0, y1 - y0) / 2), n = 0;
            for (int dy = 0; dy < r; dy++) for (int dx = 0; dx < r; dx++)
            {
                double ddx = dx + 0.5 - r, ddy = dy + 0.5 - r;
                if (ddx * ddx + ddy * ddy <= (double)r * r) continue;
                foreach (var (px, py) in new[] { (x0 + dx, y0 + dy), (x1 - 1 - dx, y0 + dy), (x0 + dx, y1 - 1 - dy), (x1 - 1 - dx, y1 - 1 - dy) })
                {
                    grid[px, py] = replacement;
                    n++;
                }
            }
            return (n, r);
        }

        private void RecolorLocked(Rgba32?[,] grid, string fromHex, string toHex, int tolerance, JsonElement operation)
        {
            var from = ImageEngine.ParseHex(fromHex) ?? throw new ArgumentException("recolor.from 색이 필요하다.");
            var to = ImageEngine.ParseHex(toHex);
            int? bx = OperationNullableInt(operation, "x"), by = OperationNullableInt(operation, "y"), bw = OperationNullableInt(operation, "w"), bh = OperationNullableInt(operation, "h");
            ClipRect(bx, by, bw, bh, out int x0, out int y0, out int x1, out int y1);
            for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++)
            {
                if (grid[xx, yy] is not Rgba32 c || ImageEngine.ColorDistance(c, from) > tolerance) continue;
                grid[xx, yy] = to;
            }
        }

        private static string? OperationString(JsonElement operation, string name) => operation.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        private static int OperationInt(JsonElement operation, string name, int fallback = 0) => operation.TryGetProperty(name, out var value) && value.TryGetInt32(out int result) ? result : fallback;
        private static int? OperationNullableInt(JsonElement operation, string name) => operation.TryGetProperty(name, out var value) && value.TryGetInt32(out int result) ? result : null;
        private static double? OperationNullableDouble(JsonElement operation, string name) => operation.TryGetProperty(name, out var value) && (value.TryGetDouble(out double d) || (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out d))) ? d : null;
        private static bool OperationBool(JsonElement operation, string name, bool fallback = false) => operation.TryGetProperty(name, out var value) ? (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool result) && result) : fallback;

        private void SaveUndoState()
        {
            lock (_pixelLock)
            {
                SaveUndoStateLocked();
            }
        }

        private void SaveUndoStateForEditableLayer()
        {
            lock (_pixelLock)
            {
                GetEditableActiveLayerLocked();
                SaveUndoStateLocked();
            }
        }

        private void SaveUndoStateLocked()
        {
            _undoStack.Add(new UndoState
            {
                Layers = _layers.Select(l => l.Clone()).ToList(),
                ActiveLayerId = _activeLayerId
            });
            if (_undoStack.Count > MaxUndoSteps)
                _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        private void PerformUndo()
        {
            lock (_pixelLock)
            {
                if (_undoStack.Count == 0) return;
                _redoStack.Add(new UndoState
                {
                    Layers = _layers.Select(l => l.Clone()).ToList(),
                    ActiveLayerId = _activeLayerId
                });
                if (_redoStack.Count > MaxUndoSteps) _redoStack.RemoveAt(0);

                var prev = _undoStack[_undoStack.Count - 1];
                _undoStack.RemoveAt(_undoStack.Count - 1);
                _layers.Clear();
                _layers.AddRange(prev.Layers.Select(l => l.Clone()));
                _activeLayerId = prev.ActiveLayerId;
            }
        }

        private void PerformRedo()
        {
            lock (_pixelLock)
            {
                if (_redoStack.Count == 0) return;
                _undoStack.Add(new UndoState
                {
                    Layers = _layers.Select(l => l.Clone()).ToList(),
                    ActiveLayerId = _activeLayerId
                });
                if (_undoStack.Count > MaxUndoSteps) _undoStack.RemoveAt(0);

                var next = _redoStack[_redoStack.Count - 1];
                _redoStack.RemoveAt(_redoStack.Count - 1);
                _layers.Clear();
                _layers.AddRange(next.Layers.Select(l => l.Clone()));
                _activeLayerId = next.ActiveLayerId;
            }
        }

        public byte[] GetCanvasPng(HashSet<string>? layerFilter = null) => ImageEngine.RenderPng(Snapshot(layerFilter), GridSize, GridSize);

        private static Rgba32? CompositeOver(Rgba32? destination, Rgba32 source, float layerOpacity)
        {
            float srcA = (source.A / 255.0f) * Math.Clamp(layerOpacity, 0.0f, 1.0f);
            if (srcA <= 0.001f) return destination;

            if (destination is not Rgba32 dst || dst.A == 0)
                return new Rgba32(source.R, source.G, source.B, (byte)Math.Clamp(srcA * 255, 0, 255));

            if (srcA >= 0.999f)
                return new Rgba32(source.R, source.G, source.B, source.A);

            float dstA = dst.A / 255.0f;
            float outA = srcA + dstA * (1.0f - srcA);
            if (outA <= 0.001f) return null;

            float outR = (source.R * srcA + dst.R * dstA * (1.0f - srcA)) / outA;
            float outG = (source.G * srcA + dst.G * dstA * (1.0f - srcA)) / outA;
            float outB = (source.B * srcA + dst.B * dstA * (1.0f - srcA)) / outA;
            return new Rgba32(
                (byte)Math.Clamp(outR, 0, 255),
                (byte)Math.Clamp(outG, 0, 255),
                (byte)Math.Clamp(outB, 0, 255),
                (byte)Math.Clamp(outA * 255, 0, 255));
        }

        private Rgba32?[,] Snapshot(HashSet<string>? layerFilter = null)
        {
            lock (_pixelLock)
            {
                var composite = NewGrid();
                foreach (var layer in _layers)
                {
                    if (!layer.Visible) continue;
                    if (layerFilter != null && layerFilter.Count > 0 && !layerFilter.Contains(layer.Id)) continue;
                    float layerOp = Math.Clamp(layer.Opacity, 0.0f, 1.0f);
                    if (layerOp <= 0.001f) continue;

                    for (int y = 0; y < GridSize; y++)
                    {
                        for (int x = 0; x < GridSize; x++)
                        {
                            if (layer.Pixels[x, y] is not Rgba32 src) continue;
                            float srcA = (src.A / 255.0f) * layerOp;
                            if (srcA <= 0.001f) continue;

                            composite[x, y] = CompositeOver(composite[x, y], src, layerOp);
                        }
                    }
                }
                return composite;
            }
        }

        private void ApplyList(Rgba32?[,] grid, IEnumerable<PixelInfo> list, bool replaceAll, string? symmetry = null)
        {
            if (replaceAll) Array.Clear(grid, 0, grid.Length);
            foreach (var p in list)
            {
                if (p.X < 0 || p.X >= GridSize || p.Y < 0 || p.Y >= GridSize) continue;
                var col = string.IsNullOrEmpty(p.Color) ? null : ImageEngine.ParseHex(p.Color);
                if (string.IsNullOrEmpty(symmetry))
                {
                    grid[p.X, p.Y] = col;
                }
                else
                {
                    SetPixelWithSymmetry(grid, p.X, p.Y, col, symmetry);
                }
            }
        }

        private static string BuildCanvasSummary(Rgba32?[,] snapshot, SelectionInfo? selection, List<Layer> layers, string activeLayerId)
        {
            int minX = GridSize, minY = GridSize, maxX = -1, maxY = -1, count = 0;
            var paletteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (snapshot[x, y] is not Rgba32 c) continue;
                    count++;
                    string hex = ImageEngine.ToHex(c);
                    paletteCounts[hex] = paletteCounts.GetValueOrDefault(hex) + 1;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            var selObj = selection is null ? null : new { x = selection.Value.X, y = selection.Value.Y, w = selection.Value.W, h = selection.Value.H };
            string note = selection is null
                ? "Full pixel list omitted. Prefer mode=partial. Use the attached canvas snapshot when present."
                : $"[User Selection Active]: ({selection.Value.X}, {selection.Value.Y}, {selection.Value.W}x{selection.Value.H}). Focus modifications inside this region if requested.";

            var layerSummaries = layers.Select(l => new
            {
                id = l.Id,
                name = l.Name,
                visible = l.Visible,
                locked = l.Locked,
                opacity = Math.Round(l.Opacity, 2),
                occupied = l.CountOccupied()
            }).ToList();

            string? dominantBg = paletteCounts.Count > 0 ? paletteCounts.OrderByDescending(kv => kv.Value).First().Key : null;

            return JsonSerializer.Serialize(new
            {
                grid = $"{GridSize}x{GridSize}",
                occupied = count,
                selection = selObj,
                dominant_bg = dominantBg,
                active_layer = activeLayerId,
                layers = layerSummaries,
                bounds = new { minX, minY, maxX, maxY },
                palette = paletteCounts.Keys.OrderBy(h => h).ToList(),
                note
            });
        }

        private static List<PixelInfo> ListOccupiedPixels(Rgba32?[,] snapshot, int x0, int y0, int w, int h)
        {
            var list = new List<PixelInfo>();
            int x1 = Math.Min(GridSize, x0 + w);
            int y1 = Math.Min(GridSize, y0 + h);
            for (int y = Math.Max(0, y0); y < y1; y++)
            {
                for (int x = Math.Max(0, x0); x < x1; x++)
                {
                    if (snapshot[x, y] is not Rgba32 c) continue;
                    list.Add(new PixelInfo { X = x, Y = y, Color = ImageEngine.ToHex(c) });
                }
            }
            return list;
        }

        private void ClipRect(int? bx, int? by, int? bw, int? bh, out int x0, out int y0, out int x1, out int y1)
        {
            if (bx is null && by is null && bw is null && bh is null)
            {
                SelectionInfo? sel;
                lock (_pixelLock) sel = _activeSelection;
                if (sel is SelectionInfo s)
                {
                    bx = s.X;
                    by = s.Y;
                    bw = s.W;
                    bh = s.H;
                }
            }

            if (bx is int x && by is int y && bw is int w && w > 0 && bh is int h && h > 0)
            {
                x0 = Math.Clamp(x, 0, GridSize - 1);
                y0 = Math.Clamp(y, 0, GridSize - 1);
                x1 = Math.Clamp(x0 + w, 1, GridSize);
                y1 = Math.Clamp(y0 + h, 1, GridSize);
            }
            else
            {
                x0 = 0;
                y0 = 0;
                x1 = GridSize;
                y1 = GridSize;
            }
        }

        private int KnockoutCorners(Rgba32?[,] grid, int tolerance)
        {
            int n = 0;
            n += FloodEraseLocked(grid, 0, 0, tolerance, true, 0, 0, GridSize, GridSize);
            n += FloodEraseLocked(grid, GridSize - 1, 0, tolerance, true, 0, 0, GridSize, GridSize);
            n += FloodEraseLocked(grid, 0, GridSize - 1, tolerance, true, 0, 0, GridSize, GridSize);
            n += FloodEraseLocked(grid, GridSize - 1, GridSize - 1, tolerance, true, 0, 0, GridSize, GridSize);
            return n;
        }

        private int FloodEraseLocked(Rgba32?[,] grid, int sx, int sy, int tolerance, bool requireLight, int x0, int y0, int x1, int y1)
        {
            if (sx < x0 || sy < y0 || sx >= x1 || sy >= y1) return 0;
            if (grid[sx, sy] is not Rgba32 seed) return 0;
            if (requireLight && (seed.R + seed.G + seed.B) / 3 < 160) return 0;

            var q = new Queue<(int x, int y)>();
            var seen = new bool[GridSize, GridSize];
            q.Enqueue((sx, sy));
            int n = 0;
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                if (x < x0 || y < y0 || x >= x1 || y >= y1 || seen[x, y]) continue;
                seen[x, y] = true;
                if (grid[x, y] is not Rgba32 c) continue;
                if (ImageEngine.ColorDistance(c, seed) > tolerance) continue;

                grid[x, y] = null;
                n++;

                if (x > x0) q.Enqueue((x - 1, y));
                if (x + 1 < x1) q.Enqueue((x + 1, y));
                if (y > y0) q.Enqueue((x, y - 1));
                if (y + 1 < y1) q.Enqueue((x, y + 1));
            }
            return n;
        }

        private static IEnumerable<(int x, int y)> Bresenham(int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                yield return (x0, y0);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static Rgba32?[,] NewGrid() => new Rgba32?[GridSize, GridSize];

        private void RaiseChanged() => CanvasChanged?.Invoke();
        private void Log(string msg) => StatusChanged?.Invoke(msg);
    }
}
