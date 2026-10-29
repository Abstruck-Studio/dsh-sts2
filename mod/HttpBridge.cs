using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sts2AgentBridge;

/// <summary>
/// Minimal HTTP/1.1 server over TcpListener. Avoids HttpListener, which needs a URL ACL
/// on Windows for anything but the exact "localhost" prefix.
/// </summary>
public static class HttpBridge
{
    public const int Port = 47821;

    /// <summary>桥自报的版本号（/health 用）。要和 mod/Sts2AgentBridge.json 的 version 同步改。</summary>
    public const string ModVersion = "0.3.1";

    private static TcpListener _listener;
    private static readonly CancellationTokenSource Cts = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        BridgeLog.Info($"http bridge listening on http://127.0.0.1:{Port}/");
        _ = Task.Run(AcceptLoop);
    }

    public static void Stop()
    {
        try { Cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }

    private static async Task AcceptLoop()
    {
        while (!Cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(Cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { BridgeLog.Error("accept failed", e); continue; }

            _ = Task.Run(() => Handle(client));
        }
    }

    private static async Task Handle(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var request = await ReadRequest(stream);
                if (request == null) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (status, payload) = await Dispatch(request);
                sw.Stop();

                var route = request.Path.Split('?')[0].TrimEnd('/');
                var isConsole = route.StartsWith("/console", StringComparison.Ordinal);
                if (!isConsole)
                    ConsolePage.Record(request.Method, request.Path, request.Body, status, payload, sw.ElapsedMilliseconds);

                var body = Encoding.UTF8.GetBytes(payload);
                var contentType = route == "/console"
                    ? "text/html; charset=utf-8"
                    : "application/json; charset=utf-8";
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\n" +
                    $"Content-Type: {contentType}\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (Exception e)
            {
                BridgeLog.Error("request failed", e);
            }
        }
    }

    private sealed class Request
    {
        public string Method;
        public string Path;
        public string Body;
    }

    private static async Task<Request> ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length);
            if (read <= 0) return null;
            buffer.Write(chunk, 0, read);
            var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (buffer.Length > 1024 * 1024) throw new InvalidOperationException("header too large");
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var contentLength = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Substring(15).Trim(), out contentLength);
        }

        var bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        var already = (int)buffer.Length - bodyStart;
        if (already > 0) body.Write(buffer.GetBuffer(), bodyStart, already);

        while (body.Length < contentLength)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length);
            if (read <= 0) break;
            body.Write(chunk, 0, read);
        }

        return new Request
        {
            Method = parts[0].ToUpperInvariant(),
            Path = parts[1],
            Body = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length),
        };
    }

    /// <summary>轮询到「房间变了或地图页开了」。**必须在 HTTP 线程调用**（内部用 OnMain）。</summary>
    private static async Task<bool> WaitScreenChange(string roomBefore, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await BridgePump.OnMain(ScreenApi.IsMapOpen, 2000)) return true;
                var room = await BridgePump.OnMain(ScreenApi.CurrentRoomName, 2000);
                if (!string.Equals(room, roomBefore, StringComparison.Ordinal)) return true;
            }
            catch { return false; }
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>轮询到地图页真的打开。</summary>
    private static async Task<bool> WaitMapOpen(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (await BridgePump.OnMain(ScreenApi.IsMapOpen, 2000)) return true; }
            catch { return false; }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<(int Status, string Payload)> Dispatch(Request request)
    {
        var path = request.Path.Split('?')[0].TrimEnd('/');
        if (path.Length == 0) path = "/";

        try
        {
            switch (path)
            {
                case "/health":
                    return (200, Json(new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["attached"] = BridgePump.IsAttached,
                        ["mod"] = "Sts2AgentBridge",
                        // 和 Sts2AgentBridge.json 的 version 保持一致（两处都改，别只改一处）。
                        ["version"] = ModVersion,
                    }));

                case "/state":
                    {
                        var state = await BridgePump.OnMain(GameApi.State);
                        return (200, Json(state));
                    }

                case "/act":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                            ? k.GetString()
                            : null;

                        // move 是「发起 + 轮询确认」两段式：它自己要 await 主线程读状态，
                        // 所以不能塞进 OnMain（主线程上 await 主线程任务 = 死锁）。
                        if (kind == "move")
                            return (200, Json(await GameApi.MoveAndConfirm(root)));

                        var result = await BridgePump.OnMain(() => GameApi.Act(root));
                        return (200, Json(result));
                    }

                case "/log":
                    {
                        var lines = File.Exists(BridgeLog.Path)
                            ? File.ReadAllLines(BridgeLog.Path)
                            : Array.Empty<string>();
                        var tail = new List<string>();
                        for (var i = Math.Max(0, lines.Length - 100); i < lines.Length; i++) tail.Add(lines[i]);
                        return (200, Json(new Dictionary<string, object> { ["path"] = BridgeLog.Path, ["lines"] = tail }));
                    }

                case "/ui":
                    {
                        if (request.Method == "POST")
                        {
                            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                            var root = doc.RootElement;
                            var which = root.TryGetProperty("which", out var w) ? w.GetString() : "main";
                            var value = !root.TryGetProperty("value", out var v) || v.GetBoolean();
                            var changed = await BridgePump.OnMain(() => AgentUi.Set(which, value));
                            var after = await BridgePump.OnMain(AgentUi.Snapshot);
                            return (200, Json(new Dictionary<string, object> { ["ok"] = changed, ["ui"] = after }));
                        }
                        return (200, Json(await BridgePump.OnMain(AgentUi.Snapshot)));
                    }

                // ---- 调试用：按像素/控件驱动界面。正常流程不要用（走 /request + /respond）----
                case "/click":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number)
                            return (200, Json(await BridgePump.OnMain(() => UiClicks.ClickByUid(u.GetUInt64()))));
                        if (root.TryGetProperty("text", out var t) && t.GetString() is { } wanted)
                            return (200, Json(await BridgePump.OnMain(() => UiClicks.ClickText(wanted))));
                        var idx = root.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                        return (200, Json(await BridgePump.OnMain(() => UiClicks.Click(idx))));
                    }

                case "/click_at":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(request.Body);
                        var x = doc.RootElement.GetProperty("x").GetSingle();
                        var y = doc.RootElement.GetProperty("y").GetSingle();
                        return (200, Json(await BridgePump.OnMain(() => UiClicks.ClickAt(x, y))));
                    }

                case "/map":
                    return (200, Json(await BridgePump.OnMain(DataApi.Map)));
                // 明确的「现在能进哪」：canMove 与 /map 的 available 同源（游戏自己的 MapTravel），
                // cannot 里写清「看着像候选但进不去」的原因，agent 就不用再猜坐标去试了。
                case "/map/next":
                    return (200, Json(await BridgePump.OnMain(MapApi.Next)));
                case "/start":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        var character = root.TryGetProperty("character", out var c) ? c.GetString() : null;
                        var seed = root.TryGetProperty("seed", out var s) ? s.GetString() : null;
                        return (200, Json(await BridgePump.OnMain(() => DataApi.Start(character, seed), 15000)));
                    }

                case "/attach/inspect":
                    return (200, Json(await BridgePump.OnMain(RunAttach.Inspect)));

                case "/attach":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await BridgePump.OnMain(RunAttach.AttachCurrentRun, 15000)));
                    }

                // /relics（复数）= 玩家身上全部遗物；必须单独一个 case ——
                // 写进 /relic 的 case 里是匹配不到的（switch 匹配的是 path 本身）。
                case "/relics":
                    return (200, Json(await BridgePump.OnMain(DataApi.Relics)));

                case "/relic":
                case "/potion":
                    {
                        var query = request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : string.Empty;
                        var pairs = query.Split('&').Select(part => part.Split('=', 2)).Where(p => p.Length == 2).ToList();
                        var id = pairs.Where(p => p[0] == "id").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        // 奖励/商店只给中文名时按 title 查（和 /card 一致）。
                        var title = pairs.Where(p => p[0] == "title").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        var isRelic = request.Path.StartsWith("/relic");
                        return (200, Json(await BridgePump.OnMain(() => isRelic ? DataApi.Relic(id, title) : DataApi.Potion(id, title))));
                    }

                case "/power":
                    {
                        var query = request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : string.Empty;
                        var pairs = query.Split('&').Select(part => part.Split('=', 2)).Where(p => p.Length == 2).ToList();
                        var id = pairs.Where(p => p[0] == "id").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        var title = pairs.Where(p => p[0] == "title").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        return (200, Json(await BridgePump.OnMain(() => DataApi.Power(id, title))));
                    }

                // 整副牌（/state 只给 deckSize）。客户端算防御/AOE/诅咒/升级数与删牌优先级。
                case "/deck":
                    {
                        var query = request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : string.Empty;
                        var pairs = query.Split('&').Select(part => part.Split('=', 2)).Where(p => p.Length == 2).ToList();
                        var player = pairs.Where(p => p[0] == "player")
                            .Select(p => int.TryParse(p[1], out var v) ? v : 0).FirstOrDefault();
                        return (200, Json(await BridgePump.OnMain(() => GameApi.DeckView(player))));
                    }

                case "/card":
                    {
                        var query = request.Path.Contains('?') ? request.Path[(request.Path.IndexOf('?') + 1)..] : string.Empty;
                        var pairs = query.Split('&').Select(part => part.Split('=', 2)).Where(p => p.Length == 2).ToList();
                        var id = pairs.Where(p => p[0] == "id").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        // uid 用 long：界面控件 id 是 60 位 ulong，用 int 解析会静默变成 null，
                        // 然后回一句误导的「missing id or uid」。
                        long? uid = pairs.Where(p => p[0] == "uid")
                            .Select(p => long.TryParse(p[1], out var v) ? v : (long?)null).FirstOrDefault();
                        var title = pairs.Where(p => p[0] == "title").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();
                        return (200, Json(await BridgePump.OnMain(() => DataApi.Card(id, uid, title))));
                    }

                // /choices、/choose（按索引选牌）已删除：选牌走 /request + /respond。
                case "/dump":
                    return (200, Json(await BridgePump.OnMain(Choices.Dump)));

                case "/probe":
                    return (200, Json(await BridgePump.OnMain(Choices.Probe)));

                case "/potions":
                    return (200, Json(await BridgePump.OnMain(Potions.List)));

                case "/potion/use":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;

                        // potion（推荐）= 稳定 key「标题#序号」；uid / index 只作兼容。
                        // 三个都不给就是错（别再默认槽 0 了，那会悄悄喝掉一瓶不该喝的药水）。
                        var used = await Potions.Use(
                            OptionalString(root, "potion"),
                            OptionalInt(root, "uid"),
                            OptionalInt(root, "index"),
                            OptionalInt(root, "target"));
                        return (200, Json(used));
                    }

                case "/potion/discard":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        var discarded = await Potions.Discard(
                            OptionalString(root, "potion"),
                            OptionalInt(root, "uid"),
                            OptionalInt(root, "index"));
                        return (200, Json(discarded));
                    }

                case "/acts":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        return (200, Json(Batch.Run(doc.RootElement)));
                    }

                case "/events":
                    {
                        long since = 0;
                        var q = request.Path.IndexOf("since=", StringComparison.Ordinal);
                        if (q >= 0) long.TryParse(request.Path.Substring(q + 6).Split('&')[0], out since);
                        return (200, Json(EventFeed.Since(since)));
                    }

                case "/console":
                    return (200, ConsolePage.Html);

                case "/console/log":
                    {
                        long since = 0;
                        var q = request.Path.IndexOf("since=", StringComparison.Ordinal);
                        if (q >= 0) long.TryParse(request.Path.Substring(q + 6).Split('&')[0], out since);
                        return (200, ConsolePage.Since(since));
                    }
                case "/whale/say":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                        var ms = doc.RootElement.TryGetProperty("ms", out var d) ? d.GetInt32() : 15000;
                        return (200, Json(await BridgePump.OnMain(() => WhaleUi.Say(text, ms))));
                    }

                case "/drag":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(request.Body);
                        var r = doc.RootElement;
                        var x1 = r.GetProperty("x1").GetSingle();
                        var y1 = r.GetProperty("y1").GetSingle();
                        var x2 = r.GetProperty("x2").GetSingle();
                        var y2 = r.GetProperty("y2").GetSingle();
                        return (200, Json(await BridgePump.OnMain(() => UiClicks.DragAt(x1, y1, x2, y2))));
                    }

                case "/whale/hide":
                    return (200, Json(await BridgePump.OnMain(WhaleUi.Hide)));

                case "/whale/state":
                    return (200, Json(await BridgePump.OnMain(WhaleUi.State)));

                case "/screen":
                    return (200, Json(await BridgePump.OnMain(ScreenApi.Inspect)));

                case "/request":
                    return (200, Json(await BridgePump.OnMain(RequestApi.Current)));

                case "/respond":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                            return (400, Json(new { ok = false, error = "id required（先 GET /request 拿 id）" }));
                        if (!root.TryGetProperty("picks", out var picksEl) || picksEl.ValueKind != JsonValueKind.Array)
                            return (400, Json(new { ok = false, error = "picks required（字符串数组，例如 [\"打击#2\"]）" }));

                        var rid = idEl.GetInt64();
                        var picks = new List<string>();
                        foreach (var el in picksEl.EnumerateArray())
                            if (el.ValueKind == JsonValueKind.String) picks.Add(el.GetString());

                        return (200, Json(await RequestApi.Respond(rid, picks.ToArray())));
                    }

                // ---- 调试用：按控件 uid 点一下。正常流程不要用（走 /request + /respond）----
                case "/tap":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        if (!doc.RootElement.TryGetProperty("uid", out var u) || u.ValueKind != JsonValueKind.Number)
                            return (400, Json(new { ok = false, error = "uid required" }));
                        var uid = u.GetUInt64();
                        return (200, Json(await BridgePump.OnMain(() => ScreenApi.Tap(uid))));
                    }

                case "/close-overlay":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await BridgePump.OnMain(ScreenApi.CloseOverlay)));
                    }

                case "/proceed":
                    {
                        // 有没结算的奖励时不许推进：Boss 奖励会晚 2~3 秒弹屏，这段空档里点前进
                        // 会把整批奖励跳掉（实测 2026-10-26）。force:true 只在奖励确实拿不了时用。
                        var force = false;
                        try
                        {
                            using var body = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                            force = body.RootElement.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
                        }
                        catch { }

                        if (!force && await BridgePump.OnMain(RequestApi.UnclaimedRewardsPending))
                        {
                            return (200, Json(new Dictionary<string, object>
                            {
                                ["ok"] = false,
                                ["rewardsPending"] = true,
                                ["error"] = "还有没结算的奖励（Boss 奖励有时晚 2~3 秒才弹屏）：先 sts2_request 等 rewards 出现，别用 proceed 跳过",
                                ["hint"] = "等 0.5~1 秒再 sts2_request；真拿不了就在 rewards 请求上 picks:[]（= 离开奖励）。"
                                         + "确实卡死才用 sts2_proceed {force:true}",
                            }));
                        }

                        // 两段式：先点房间自己的前进键，**真的变了**才算成功（实测商店里前进键
                        // 虽然可用、点了却不推进）。没变化就退回到顶栏地图按钮——玩家开地图就是点它，
                        // 而 `move` 只在地图页有效。
                        var roomBefore = await BridgePump.OnMain(ScreenApi.CurrentRoomName);
                        var clicked = await BridgePump.OnMain(ScreenApi.Proceed);
                        if (await WaitScreenChange(roomBefore, 2500))
                            return (200, Json(clicked));

                        var mapTry = await BridgePump.OnMain(ScreenApi.OpenMapPage);
                        var mapOpened = await WaitMapOpen(2500);
                        var dict = new Dictionary<string, object>
                        {
                            ["ok"] = mapOpened,
                            ["method"] = mapOpened ? "NTopBarMapButton.ForceClick" : "none",
                            ["proceed"] = clicked,
                            ["mapAttempt"] = mapTry,
                            ["roomBefore"] = roomBefore,
                            ["room"] = await BridgePump.OnMain(ScreenApi.CurrentRoomName),
                            ["mapOpen"] = mapOpened,
                        };
                        dict["hint"] = mapOpened
                            ? "地图页已打开：现在用 sts2_move（move 只在地图页有效）"
                            : "没打开地图页：先看 sts2_request 有没有待答请求；商店面板还开着就先 picks:[] 关掉";
                        return (200, Json(dict));
                    }

                // ---- 存档并退出 / 重新读档（重开本局）----
                // 都是「游戏自己的入口」：SaveManager.SaveRun + NGame.ReturnToMainMenu，
                // 以及主菜单的「继续游戏」按钮。它们自己要 await 主线程任务，所以不走 OnMain。
                case "/save-quit":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await RunControl.SaveQuit()));
                    }

                case "/reload":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        return (200, Json(await RunControl.Reload()));
                    }

                // ---- 调试用：按 Godot 键码发一次按键。正常流程不要用 ----
                case "/key":
                    {
                        if (request.Method != "POST") return (405, Json(new { ok = false, error = "POST required" }));
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
                        var key = doc.RootElement.TryGetProperty("code", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 69;
                        return (200, Json(await BridgePump.OnMain(() => ScreenApi.PressKey(key))));
                    }

                default:
                    return (404, Json(new { ok = false, error = $"no route {path}" }));
            }
        }
        catch (Exception e)
        {
            BridgeLog.Error($"dispatch {path} failed", e);
            return (500, Json(new { ok = false, error = e.GetType().Name + ": " + e.Message }));
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);

    /// <summary>可选整数字段：没给 / null / 不是整数，一律当"没给"（返回 null）。</summary>
    private static int? OptionalInt(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Number) return null;
        return el.TryGetInt32(out var value) ? value : null;
    }

    /// <summary>可选字符串字段：空白字符串也算"没给"。</summary>
    private static string OptionalString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var text = el.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}


