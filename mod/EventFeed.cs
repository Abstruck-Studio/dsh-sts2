using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// Watches the visible screen from the main-thread pump and records a small event whenever
/// the screen signature changes. The live director polls GET /events instead of polling the
/// game state itself. The feed is a bounded in-memory ring; it never blocks the main thread
/// and is written from the main thread / read from HTTP threads under one lock.
/// </summary>
public static class EventFeed
{
    private const int Capacity = 100;
    private const int MaxItems = 40;
    private const ulong ThrottleMs = 400;

    private sealed class Entry
    {
        public long Seq;
        public string At;
        public string Screen;
        public string Kind;
        public string Summary;
        public List<object> Items;
        public Dictionary<string, object> Data;
    }

    /// <summary>Appends one event (used by StatePush for combat turn snapshots).</summary>
    public static void Publish(string screen, string kind, string summary, List<object> items, Dictionary<string, object> data = null)
    {
        var entry = new Entry
        {
            At = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            Screen = screen,
            Kind = kind,
            Summary = summary,
            Items = items ?? new List<object>(),
            Data = data,
        };

        lock (Gate)
        {
            entry.Seq = ++_seq;
            Buffer.Add(entry);
            if (Buffer.Count > Capacity) Buffer.RemoveAt(0);
        }
    }

    private static readonly object Gate = new();
    private static readonly List<Entry> Buffer = new(Capacity);
    private static long _seq;
    private static ulong _lastTickMs;
    private static string _lastSignature;
    private static bool _errorLogged;

    /// <summary>Called from BridgePump.Drain on the Godot main thread.</summary>
    public static void Tick()
    {
        var now = Time.GetTicksMsec();
        if (_lastTickMs != 0 && now - _lastTickMs < ThrottleMs) return;
        _lastTickMs = now;

        try
        {
            var inspect = ScreenApi.Inspect();
            if (inspect.TryGetValue("ok", out var ok) && ok is bool b && !b) return;

            var screen = inspect.TryGetValue("screen", out var sc) ? sc as string ?? "?" : "?";
            var items = inspect.TryGetValue("items", out var raw) && raw is List<object> list
                ? list
                : new List<object>();
            var count = items.Count;

            var holders = 0;
            var cardLabels = new List<string>();
            var labels = new List<string>();
            var text = screen + " ";
            foreach (var item in items)
            {
                if (item is not Dictionary<string, object> dict) continue;

                if (dict.TryGetValue("card", out var cardName) && cardName is string cn && cn.Length > 0)
                {
                    holders++;
                    if (cardLabels.Count < 8)
                        cardLabels.Add($"{cn}(uid={dict.GetValueOrDefault("uid")})");
                }
                else if (dict.TryGetValue("kind", out var k) && k as string == "holder")
                {
                    holders++;
                }

                if (dict.TryGetValue("text", out var t) && t is string s && s.Length > 0) text += s + " ";

                // Event/shop tiles are textless but carry a model label; collect them so the
                // pushed summary names the actual options instead of "N 个可点".
                var shown = dict.TryGetValue("label", out var lb) && lb is string ls && ls.Length > 0
                    ? ls
                    : dict.TryGetValue("text", out var tx) && tx is string ts ? ts : null;
                // 跳过顶部血量/金币这类小 HUD，只留真正能点的按钮
                var w = dict.TryGetValue("w", out var wv) && wv is float wf ? wf : 0f;
                var h = dict.TryGetValue("h", out var hv) && hv is float hf ? hf : 0f;
                if (shown != null && labels.Count < 6 && w >= 120f && h >= 50f)
                {
                    var uid = dict.GetValueOrDefault("uid");
                    labels.Add($"{shown}(uid={uid})");
                }
            }

            // 手牌里的牌也带 card 模型，但那不是「选牌界面」。只有真的弹出了选牌浮层才算。
            var realSelect = ScreenApi.HasCardSelectScreen();

            // 战斗结束后的奖励界面，房间类型仍是 Monster：不能当成「战斗中」过滤掉。
            var isReward = Contains(text, "金币") || Contains(text, "跳过")
                || Contains(text, "添加到你的牌组") || Contains(text, "Rewards");

            var cardCount = realSelect ? holders : 0;

            var signature = $"{screen}|{count}|{cardCount}|{(isReward ? 1 : 0)}";
            if (signature == _lastSignature) return;

            // Combat state is pushed by StatePush; only surface card-select dialogs that
            // appear mid-combat (retrieve/discard/transform) and the reward screen.
            if (InCombat() && !realSelect && !isReward) return;

            _lastSignature = signature;

            var roomType = CurrentRoomType();
            var haystack = text + " " + roomType;
            var kind = realSelect ? "card_select"
                : isReward ? "reward"
                : Contains(haystack, "商店") || Contains(haystack, "Shop") ? "shop"
                : Contains(haystack, "篝火") || Contains(haystack, "RestSite") ? "rest"
                : "screen";

            var summary = kind switch
            {
                "card_select" => $"选牌界面（{holders} 张）：{string.Join("、", cardLabels)}",
                "reward" => $"战斗奖励（先领金币/药水，再点「将一张牌添加到你的牌组」，最后点「跳过」或「前进」）：{string.Join("、", labels)}",
                "shop" => $"商店：{string.Join("、", labels)}",
                "rest" => $"篝火：{string.Join("、", labels)}",
                _ => $"{screen}：{(labels.Count > 0 ? string.Join("、", labels) : count + " 个可点，都无文本")}",
            };

            var capped = items.Count > MaxItems ? items.GetRange(0, MaxItems) : items;
            var entry = new Entry
            {
                At = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                Screen = screen,
                Kind = kind,
                Summary = summary,
                Items = capped,
            };

            lock (Gate)
            {
                entry.Seq = ++_seq;
                Buffer.Add(entry);
                if (Buffer.Count > Capacity) Buffer.RemoveAt(0);
            }

            _errorLogged = false;
        }
        catch (Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            BridgeLog.Warn("event feed tick failed: " + e.Message);
        }
    }

    /// <summary>GET /events?since=n — events with seq &gt; since.</summary>
    public static Dictionary<string, object> Since(long since)
    {
        var events = new List<object>();
        long now;
        lock (Gate)
        {
            now = _seq;
            foreach (var entry in Buffer)
            {
                if (entry.Seq <= since) continue;
                events.Add(new Dictionary<string, object>
                {
                    ["seq"] = entry.Seq,
                    ["at"] = entry.At,
                    ["screen"] = entry.Screen,
                    ["kind"] = entry.Kind,
                    ["summary"] = entry.Summary,
                    ["items"] = entry.Items,
                    ["data"] = entry.Data,
                });
            }
        }
        return new Dictionary<string, object> { ["ok"] = true, ["now"] = now, ["events"] = events };
    }

    private static bool InCombat()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null || !rm.IsInProgress) return false;
            var state = rm.DebugOnlyGetState();
            return state?.CurrentRoom is CombatRoom room && room.CombatState != null;
        }
        catch
        {
            return false;
        }
    }

    private static string CurrentRoomType()
    {
        try
        {
            var rm = RunManager.Instance;
            var state = rm?.DebugOnlyGetState();
            return state?.CurrentRoom?.RoomType.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
