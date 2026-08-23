using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PixelPair
{
    internal sealed class AgentHttpServer : IDisposable
    {
        public const int DefaultPort = 17890;
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private static string WwwRootDir
        {
            get
            {
                string devWww = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                if (Directory.Exists(devWww)) return devWww;
                return Path.Combine(AppContext.BaseDirectory, "wwwroot");
            }
        }
        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".png"] = "image/png",
            [".ico"] = "image/x-icon",
        };

        private readonly CanvasModel _canvas;
        private readonly string _agentToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly object _sseLock = new();
        private readonly List<HttpListenerResponse> _sseClients = new();
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;

        public int Port { get; private set; }

        public AgentHttpServer(CanvasModel canvas)
        {
            _canvas = canvas;
            _canvas.CanvasChanged += BroadcastChanged;
            _canvas.StatusChanged += BroadcastStatus;
        }

        public int Start()
        {
            Exception? last = null;
            for (int port = DefaultPort; port < DefaultPort + 20; port++)
            {
                var listener = new HttpListener();
                string prefix = $"http://127.0.0.1:{port}/";
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    WriteConnectionFiles(prefix);
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => ListenLoop(_cts.Token));
                    return port;
                }
                catch (Exception ex)
                {
                    last = ex;
                    listener.Close();
                }
            }
            throw last ?? new InvalidOperationException("에이전트 포트를 열 수 없다.");
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener is { IsListening: true })
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }

                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            try
            {
                string? origin = req.Headers["Origin"];
                if (!IsAllowedBrowserOrigin(origin))
                {
                    await WriteJson(res, 403, new { ok = false, error = "외부 웹사이트에서는 PixelPair 로컬 API에 접근할 수 없습니다." });
                    return;
                }

                if (!string.IsNullOrWhiteSpace(origin))
                {
                    res.Headers["Access-Control-Allow-Origin"] = origin;
                    res.Headers["Vary"] = "Origin";
                }
                res.Headers["Access-Control-Allow-Methods"] = "GET,POST,DELETE,OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type,X-PixelPair-Token";

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
                if (path.Length == 0) path = "/";

                if (req.HttpMethod == "GET" && path == "/health")
                {
                    await WriteJson(res, 200, new { ok = true, port = Port, grid = CanvasModel.GridSize });
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/canvas.png")
                {
                    string? layersParam = req.QueryString["layers"];
                    HashSet<string>? filter = string.IsNullOrEmpty(layersParam)
                        ? null
                        : new HashSet<string>(layersParam.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                    byte[] png = _canvas.GetCanvasPng(filter);
                    res.StatusCode = 200;
                    res.ContentType = "image/png";
                    res.ContentLength64 = png.Length;
                    await res.OutputStream.WriteAsync(png);
                    res.Close();
                    return;
                }
                if (req.HttpMethod == "GET" && (path == "/export.png" || path == "/export.ico"))
                {
                    bool isIco = path == "/export.ico";
                    byte[] bytes = _canvas.GetExportBytes(isIco ? "ico" : "png");
                    res.StatusCode = 200;
                    res.ContentType = isIco ? "image/x-icon" : "image/png";
                    res.Headers["Content-Disposition"] = $"attachment; filename=icon.{(isIco ? "ico" : "png")}";
                    res.ContentLength64 = bytes.Length;
                    await res.OutputStream.WriteAsync(bytes);
                    res.Close();
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/export.pxp")
                {
                    byte[] bytes = _canvas.ExportProject();
                    res.StatusCode = 200;
                    res.ContentType = "application/json";
                    res.Headers["Content-Disposition"] = "attachment; filename=project.pxp";
                    res.ContentLength64 = bytes.Length;
                    await res.OutputStream.WriteAsync(bytes);
                    res.Close();
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/layer/export.png")
                {
                    string layerId = req.QueryString["id"] ?? "";
                    byte[] png = _canvas.ExportLayer(layerId, "png");
                    res.StatusCode = 200;
                    res.ContentType = "image/png";
                    res.Headers["Content-Disposition"] = $"attachment; filename=layer_{layerId}.png";
                    res.ContentLength64 = png.Length;
                    await res.OutputStream.WriteAsync(png);
                    res.Close();
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/project/import")
                {
                    string json = await ReadBody(req);
                    await WriteJson(res, 200, _canvas.ImportProject(json));
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/events")
                {
                    await HandleSse(res);
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/canvas")
                {
                    bool png = QueryFlag(req, "png", defaultValue: true);
                    bool pixels = QueryFlag(req, "pixels", defaultValue: false);
                    int? rx = QueryInt(req, "x");
                    int? ry = QueryInt(req, "y");
                    int? rw = QueryInt(req, "w");
                    int? rh = QueryInt(req, "h");
                    int? scale = QueryInt(req, "scale");
                    await WriteJson(res, 200, _canvas.GetCanvas(png, pixels, rx, ry, rw, rh, scale));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/selection")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    int x = GetInt(el, "x", 0);
                    int y = GetInt(el, "y", 0);
                    int w = GetInt(el, "w", 1);
                    int h = GetInt(el, "h", 1);
                    await WriteJson(res, 200, _canvas.SetSelection(x, y, w, h));
                    return;
                }
                if ((req.HttpMethod == "DELETE" && path == "/selection") || (req.HttpMethod == "POST" && path == "/selection/clear"))
                {
                    await WriteJson(res, 200, _canvas.ClearSelection());
                    return;
                }
                if (req.HttpMethod == "GET" && path == "/layers")
                {
                    await WriteJson(res, 200, _canvas.GetLayersInfo());
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/add")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string? name = GetStr(doc.RootElement, "name");
                    await WriteJson(res, 200, _canvas.AddLayer(name));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/remove")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string? id = GetStr(doc.RootElement, "id");
                    await WriteJson(res, 200, _canvas.RemoveLayer(id));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/select")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    await WriteJson(res, 200, _canvas.SelectLayer(id));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/visible")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    bool visible = doc.RootElement.TryGetProperty("visible", out var vp) && (vp.ValueKind == JsonValueKind.True || (vp.ValueKind == JsonValueKind.String && vp.GetString() == "true"));
                    await WriteJson(res, 200, _canvas.SetLayerVisible(id, visible));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/locked")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    bool locked = doc.RootElement.TryGetProperty("locked", out var lp) && (lp.ValueKind == JsonValueKind.True || (lp.ValueKind == JsonValueKind.String && lp.GetString() == "true"));
                    await WriteJson(res, 200, _canvas.SetLayerLocked(id, locked));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/merge")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    await WriteJson(res, 200, _canvas.MergeLayerDown(id));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/rename")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    string name = GetStr(doc.RootElement, "name") ?? "";
                    await WriteJson(res, 200, _canvas.RenameLayer(id, name));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/duplicate")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    await WriteJson(res, 200, _canvas.DuplicateLayer(id));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/move")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    int dir = GetInt(doc.RootElement, "direction", 1);
                    await WriteJson(res, 200, _canvas.MoveLayer(id, dir));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/layers/opacity")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string id = GetStr(doc.RootElement, "id") ?? "";
                    float opacity = doc.RootElement.TryGetProperty("opacity", out var opProp) && opProp.TryGetSingle(out float op) ? op : 1.0f;
                    await WriteJson(res, 200, _canvas.SetLayerOpacity(id, opacity));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/import")
                {
                    var imp = await ReadImportRequest(req, res);
                    if (imp == null) return;
                    await WriteJson(res, 200, _canvas.Import(imp.Value.Bytes, imp.Value.MaxColors, imp.Value.KnockoutCorners));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/pixels")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string mode = "partial";
                    if (doc.RootElement.TryGetProperty("mode", out var modeProp))
                        mode = modeProp.GetString() ?? "partial";
                    string? symmetry = doc.RootElement.TryGetProperty("symmetry", out var symProp) ? symProp.GetString() : null;
                    if (!doc.RootElement.TryGetProperty("pixels", out var pixelsProp))
                    {
                        await WriteJson(res, 400, new { ok = false, error = "pixels 배열이 필요하다." });
                        return;
                    }
                    var list = JsonSerializer.Deserialize<List<PixelInfo>>(pixelsProp.GetRawText(), JsonOpts) ?? new();
                    await WriteJson(res, 200, _canvas.SetPixels(mode, list, symmetry));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/operations")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    if (!doc.RootElement.TryGetProperty("operations", out var operations))
                    {
                        await WriteJson(res, 400, new { ok = false, error = "operations 배열이 필요하다." });
                        return;
                    }
                    await WriteJson(res, 200, _canvas.ApplyOperations(operations));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/export")
                {
                    if (!await RequireAgentToken(req, res)) return;
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    string pathOut = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    string format = doc.RootElement.TryGetProperty("format", out var f) ? f.GetString() ?? "png" : "png";
                    if (string.IsNullOrWhiteSpace(pathOut))
                    {
                        await WriteJson(res, 400, new { ok = false, error = "path가 필요하다." });
                        return;
                    }
                    await WriteJson(res, 200, _canvas.Export(pathOut, format));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/undo")
                {
                    await WriteJson(res, 200, _canvas.Undo());
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/redo")
                {
                    await WriteJson(res, 200, _canvas.Redo());
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/clear")
                {
                    await WriteJson(res, 200, _canvas.ClearCanvas());
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/flood_erase")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    int fx = GetInt(el, "x", -1);
                    int fy = GetInt(el, "y", -1);
                    int tol = GetInt(el, "tolerance", 32);
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.FloodErase(fx, fy, tol, cx, cy, cw, ch));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/recolor")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    string from = GetStr(el, "from") ?? "";
                    string to = GetStr(el, "to") ?? "";
                    int tol = GetInt(el, "tolerance", 16);
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.Recolor(from, to, tol, cx, cy, cw, ch));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/fill_rect")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.FillRect(
                        cx,
                        cy,
                        cw,
                        ch,
                        GetStr(el, "color") ?? "",
                        GetStr(el, "symmetry")));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/draw_line")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    await WriteJson(res, 200, _canvas.DrawLine(
                        GetInt(el, "x0", 0),
                        GetInt(el, "y0", 0),
                        GetInt(el, "x1", 0),
                        GetInt(el, "y1", 0),
                        GetStr(el, "color") ?? "",
                        GetBool(el, "pixel_perfect"),
                        GetStr(el, "symmetry")));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/draw_circle")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    bool fillCircle = el.TryGetProperty("fill", out var fillProp) &&
                        (fillProp.ValueKind == JsonValueKind.True ||
                         (fillProp.ValueKind == JsonValueKind.String && fillProp.GetString() == "true"));
                    int? strokeWidth = el.TryGetProperty("stroke_width", out var strokeProp) && strokeProp.TryGetInt32(out int stroke)
                        ? stroke
                        : null;
                    await WriteJson(res, 200, _canvas.DrawCircle(
                        GetInt(el, "x", 0),
                        GetInt(el, "y", 0),
                        GetInt(el, "radius", 1),
                        GetStr(el, "color") ?? "",
                        fillCircle,
                        strokeWidth,
                        GetStr(el, "symmetry")));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/draw_ellipse")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    bool fillEllipse = el.TryGetProperty("fill", out var fillProp) &&
                        (fillProp.ValueKind == JsonValueKind.True ||
                         (fillProp.ValueKind == JsonValueKind.String && fillProp.GetString() == "true"));
                    int? strokeWidth = el.TryGetProperty("stroke_width", out var strokeProp) && strokeProp.TryGetInt32(out int stroke)
                        ? stroke
                        : null;
                    await WriteJson(res, 200, _canvas.DrawEllipse(
                        GetInt(el, "x", 0),
                        GetInt(el, "y", 0),
                        GetInt(el, "rx", 1),
                        GetInt(el, "ry", 1),
                        GetStr(el, "color") ?? "",
                        fillEllipse,
                        strokeWidth,
                        GetStr(el, "symmetry")));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/draw_polygon")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    var pts = new List<(int x, int y)>();
                    if (el.TryGetProperty("points", out var pointsEl) && pointsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in pointsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                            {
                                pts.Add((item[0].GetInt32(), item[1].GetInt32()));
                            }
                            else if (item.ValueKind == JsonValueKind.Object)
                            {
                                pts.Add((GetInt(item, "x", 0), GetInt(item, "y", 0)));
                            }
                        }
                    }
                    bool fillPoly = !el.TryGetProperty("fill", out var fillProp) ||
                        fillProp.ValueKind == JsonValueKind.True ||
                        (fillProp.ValueKind == JsonValueKind.String && fillProp.GetString() == "true");
                    int? strokeWidth = el.TryGetProperty("stroke_width", out var strokeProp) && strokeProp.TryGetInt32(out int stroke)
                        ? stroke
                        : null;
                    await WriteJson(res, 200, _canvas.DrawPolygon(
                        pts,
                        GetStr(el, "color") ?? "",
                        fillPoly,
                        strokeWidth,
                        GetStr(el, "symmetry")));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/shift_rect")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    int dx = GetInt(el, "dx", 0);
                    int dy = GetInt(el, "dy", 0);
                    bool wrap = el.TryGetProperty("wrap", out var wp) && (wp.ValueKind == JsonValueKind.True || (wp.ValueKind == JsonValueKind.String && wp.GetString() == "true"));
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.ShiftRect(dx, dy, cx, cy, cw, ch, wrap));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/rotate_rect")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    int angle = GetInt(el, "angle", 90);
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.RotateRect(angle, cx, cy, cw, ch));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/flip_rect")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.FlipRect(cx, cy, cw, ch));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/clear_rect")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    ReadClip(el, out int? cx, out int? cy, out int? cw, out int? ch);
                    await WriteJson(res, 200, _canvas.ClearRect(cx, cy, cw, ch));
                    return;
                }
                if (req.HttpMethod == "POST" && path == "/round_corners")
                {
                    using var doc = JsonDocument.Parse(await ReadBody(req));
                    var el = doc.RootElement;
                    await WriteJson(res, 200, _canvas.RoundCorners(
                        GetInt(el, "x", 0),
                        GetInt(el, "y", 0),
                        GetInt(el, "w", 1),
                        GetInt(el, "h", 1),
                        GetInt(el, "radius", 4),
                        GetStr(el, "fill_color")));
                    return;
                }

                if (req.HttpMethod == "GET" && await TryServeStatic(req.Url?.AbsolutePath ?? "/", res))
                {
                    return;
                }

                await WriteJson(res, 404, new
                {
                    ok = false,
                    error = "unknown path",
                    endpoints = new[]
                    {
                        "GET /health", "GET /canvas", "GET /canvas.png", "GET /layers", "GET /events",
                        "GET /export.png", "GET /export.ico", "GET /export.pxp", "GET /layer/export.png",
                        "POST /import", "POST /project/import", "POST /pixels", "POST /operations", "POST /export",
                        "POST /undo", "POST /redo", "POST /clear", "POST /selection", "POST /selection/clear",
                        "POST /layers/add", "POST /layers/remove", "POST /layers/select", "POST /layers/visible",
                        "POST /layers/locked", "POST /layers/merge", "POST /layers/rename", "POST /layers/duplicate",
                        "POST /layers/move", "POST /layers/opacity", "POST /flood_erase", "POST /recolor",
                        "POST /fill_rect", "POST /clear_rect", "POST /flip_rect", "POST /shift_rect", "POST /rotate_rect",
                        "POST /draw_line", "POST /draw_circle", "POST /draw_ellipse", "POST /round_corners"
                    }
                });
            }
            catch (InvalidOperationException ex)
            {
                try { await WriteJson(res, 409, new { ok = false, error = ex.Message }); }
                catch { res.Abort(); }
            }
            catch (Exception ex)
            {
                try { await WriteJson(res, 500, new { ok = false, error = ex.Message }); }
                catch { res.Abort(); }
            }
        }

        private async Task HandleSse(HttpListenerResponse res)
        {
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.Headers["Cache-Control"] = "no-cache";
            res.SendChunked = true;

            lock (_sseLock) _sseClients.Add(res);
            try
            {
                var hello = Encoding.UTF8.GetBytes(": connected\n\n");
                await res.OutputStream.WriteAsync(hello);
                await res.OutputStream.FlushAsync();

                var ct = _cts?.Token ?? CancellationToken.None;
                var ping = Encoding.UTF8.GetBytes(": ping\n\n");
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(15000, ct);
                    await res.OutputStream.WriteAsync(ping, ct);
                    await res.OutputStream.FlushAsync(ct);
                }
            }
            catch
            {
                // 클라이언트 연결 종료 또는 서버 정지 — 정상적인 흐름이다.
            }
            finally
            {
                lock (_sseLock) _sseClients.Remove(res);
                try { res.Close(); } catch { }
            }
        }

        private void BroadcastChanged()
        {
            BroadcastEvent("canvas_changed", "{}");
        }

        private void BroadcastStatus(string msg)
        {
            var json = JsonSerializer.Serialize(new { message = msg });
            BroadcastEvent("status_changed", json);
        }

        private void BroadcastEvent(string eventName, string data)
        {
            List<HttpListenerResponse> clients;
            lock (_sseLock) clients = new List<HttpListenerResponse>(_sseClients);
            if (clients.Count == 0) return;

            byte[] payload = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {data}\n\n");
            foreach (var client in clients)
            {
                try
                {
                    client.OutputStream.Write(payload, 0, payload.Length);
                    client.OutputStream.Flush();
                }
                catch
                {
                    lock (_sseLock) _sseClients.Remove(client);
                }
            }
        }

        private static async Task<bool> TryServeStatic(string requestPath, HttpListenerResponse res)
        {
            if (requestPath == "/" || requestPath.Length == 0) requestPath = "/index.html";

            string combined = Path.GetFullPath(Path.Combine(WwwRootDir, requestPath.TrimStart('/')));
            string rootFull = Path.GetFullPath(WwwRootDir) + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(rootFull, StringComparison.Ordinal)) return false;
            if (!File.Exists(combined)) return false;

            byte[] bytes = await File.ReadAllBytesAsync(combined);
            string ext = Path.GetExtension(combined);
            res.StatusCode = 200;
            res.ContentType = ContentTypes.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
            return true;
        }

        private static bool QueryFlag(HttpListenerRequest req, string name, bool defaultValue)
        {
            string? v = req.QueryString[name];
            if (string.IsNullOrEmpty(v)) return defaultValue;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int? QueryInt(HttpListenerRequest req, string name)
        {
            string? v = req.QueryString[name];
            if (int.TryParse(v, out int n)) return n;
            return null;
        }

        private static int GetInt(JsonElement el, string name, int fallback)
        {
            if (!el.TryGetProperty(name, out var p)) return fallback;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n)) return n;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out n)) return n;
            return fallback;
        }

        private static string? GetStr(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
        }

        private static bool GetBool(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return false;
            return p.ValueKind == JsonValueKind.True ||
                (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out bool value) && value);
        }

        private static void ReadClip(JsonElement el, out int? x, out int? y, out int? w, out int? h)
        {
            int cw = GetInt(el, "clip_w", GetInt(el, "w", 0));
            int ch = GetInt(el, "clip_h", GetInt(el, "h", 0));
            if (cw <= 0 || ch <= 0)
            {
                x = y = w = h = null;
                return;
            }
            int cx = GetInt(el, "clip_x", int.MinValue);
            int cy = GetInt(el, "clip_y", int.MinValue);
            if (cx == int.MinValue) cx = GetInt(el, "x", 0);
            if (cy == int.MinValue) cy = GetInt(el, "y", 0);
            x = cx;
            y = cy;
            w = cw;
            h = ch;
        }

        private static async Task<string> ReadBody(HttpListenerRequest req)
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private readonly record struct ImportRequest(byte[] Bytes, int MaxColors, bool KnockoutCorners);

        private async Task<ImportRequest?> ReadImportRequest(HttpListenerRequest req, HttpListenerResponse res)
        {
            string? ctype = req.ContentType ?? "";
            if (ctype.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                await req.InputStream.CopyToAsync(ms);
                return new ImportRequest(ms.ToArray(), 0, false);
            }

            string body = await ReadBody(req);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException("import 본문이 비어 있다.");

            using var doc = JsonDocument.Parse(body);
            int maxColors = GetInt(doc.RootElement, "max_colors", GetInt(doc.RootElement, "maxColors", 0));
            bool knockout = false;
            if (doc.RootElement.TryGetProperty("knockout_corners", out var kc) ||
                doc.RootElement.TryGetProperty("knockoutCorners", out kc))
            {
                knockout = kc.ValueKind == JsonValueKind.True ||
                           (kc.ValueKind == JsonValueKind.String && kc.GetString() == "true");
            }

            byte[] bytes;
            if (doc.RootElement.TryGetProperty("image_base64", out var b64) ||
                doc.RootElement.TryGetProperty("imageBase64", out b64))
            {
                string? s = b64.GetString();
                if (string.IsNullOrEmpty(s)) throw new InvalidOperationException("image_base64가 비어 있다.");
                int comma = s.IndexOf(',');
                if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                    s = s[(comma + 1)..];
                bytes = Convert.FromBase64String(s);
            }
            else if (doc.RootElement.TryGetProperty("path", out var pathProp))
            {
                if (!await RequireAgentToken(req, res)) return null;
                string? filePath = pathProp.GetString();
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    throw new FileNotFoundException("이미지 경로를 찾을 수 없다.", filePath);
                bytes = await File.ReadAllBytesAsync(filePath);
            }
            else
                throw new InvalidOperationException("image_base64 또는 path가 필요하다.");

            return new ImportRequest(bytes, maxColors, knockout);
        }

        private static async Task WriteJson(HttpListenerResponse res, int status, object payload)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }

        private bool IsAllowedBrowserOrigin(string? origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return true; // MCP, curl, and other non-browser local clients.
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return false;
            if (uri.Port != Port) return false;
            return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasValidAgentToken(HttpListenerRequest req)
        {
            string? supplied = req.Headers["X-PixelPair-Token"];
            if (string.IsNullOrWhiteSpace(supplied)) return false;
            byte[] expected = Encoding.UTF8.GetBytes(_agentToken);
            byte[] actual = Encoding.UTF8.GetBytes(supplied);
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        private async Task<bool> RequireAgentToken(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (HasValidAgentToken(req)) return true;
            await WriteJson(res, 401, new { ok = false, error = "이 작업에는 PixelPair 로컬 에이전트 토큰이 필요합니다." });
            return false;
        }

        // OS별 경로 규칙과 무관하게 MCP가 찾을 수 있도록 URL과 비밀 토큰을 홈 디렉터리에 기록한다.
        private void WriteConnectionFiles(string url)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dir = Path.Combine(home, ".pixelpair");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "agent-url.txt"), url.TrimEnd('/') + "\n");
                string tokenPath = Path.Combine(dir, "agent-token.txt");
                File.WriteAllText(tokenPath, _agentToken + "\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _canvas.CanvasChanged -= BroadcastChanged;
            try { _cts?.Cancel(); } catch { }
            lock (_sseLock)
            {
                foreach (var client in _sseClients)
                {
                    try { client.Close(); } catch { }
                }
                _sseClients.Clear();
            }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _cts?.Dispose();
        }
    }
}
