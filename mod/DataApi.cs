using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2AgentBridge;

/// <summary>Screen-free gameplay: start a run and read the map straight from the models.</summary>
public static class DataApi
{
    /// <summary>
    /// <c>GET /map</c>：整张图（节点 + 父子连边 + visited）+ 现在能走的节点。
    ///
    /// `available` 和 move 用同一套判定（<see cref="GameApi.TravelablePoints"/>，照抄游戏的
    /// <c>NMapScreen.RecalculateTravelability</c>）：visited 为空 → 只有起点；最后一行 → Boss；
    /// 其余 → <c>MapTravel.GetTravelablePointsFrom</c>。连边 / 行列数 / Boss 坐标的来源见
    /// <see cref="MapApi"/> 的注释（MetaPriv + MetaIl 核过）。
    ///
    /// 只读：这里不移动、不写 visited。
    /// </summary>
    public static Dictionary<string, object> Map()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state?.Map == null) return Fail("no active run");

        var graph = MapApi.Build(state);
        if (graph == null) return Fail("no active run");

        var current = state.CurrentMapPoint;
        var currentCoord = current?.coord;

        var reachablePoints = GameApi.TravelablePoints(state, current);
        var reachable = new HashSet<MapCoord>(reachablePoints.Select(p => p.coord));

        // available：游戏自己的可走集合，额外带方向 edge（start / up / left / right）。
        var available = new List<object>();
        foreach (var point in reachablePoints)
        {
            var node = graph.At(point.coord);
            var bossLike = node != null && MapApi.IsBossLike(graph, node);
            string type;
            try { type = point.PointType.ToString(); } catch { type = "Unknown"; }
            available.Add(new Dictionary<string, object>
            {
                ["col"] = point.coord.col,
                ["row"] = point.coord.row,
                ["type"] = node?.Type ?? type,
                ["edge"] = MapApi.EdgeName(currentCoord, point.coord, bossLike),
            });
        }

        // points：整张图（Grid 里的点 + Start / Boss / SecondBoss 三个特殊点），每个都带
        // children / parents（游戏自己的 MapPoint.Children 属性 + MapPoint.parents 公有字段）。
        var points = new List<object>();
        foreach (var node in graph.All)
        {
            var children = new List<object>();
            foreach (var child in node.Children)
                children.Add(new Dictionary<string, object> { ["col"] = child.Col, ["row"] = child.Row });

            var parents = new List<object>();
            foreach (var parent in node.Parents)
                parents.Add(new Dictionary<string, object> { ["col"] = parent.Col, ["row"] = parent.Row });

            var dto = new Dictionary<string, object>
            {
                ["col"] = node.Col,
                ["row"] = node.Row,
                ["type"] = node.Type,
                ["visited"] = node.Visited,
                ["reachable"] = reachable.Contains(new MapCoord(node.Col, node.Row)),
                ["children"] = children,
                ["parents"] = parents,
            };
            if (node.Special) dto["special"] = true;
            points.Add(dto);
        }

        string act = null;
        try { act = state.Act?.Id?.ToString(); } catch { }

        var boss = graph.Boss;
        return Ok(new Dictionary<string, object>
        {
            ["act"] = act,
            ["actFloor"] = state.ActFloor,
            ["totalFloor"] = state.TotalFloor,
            ["current"] = currentCoord == null ? null : new { col = currentCoord.Value.col, row = currentCoord.Value.row },
            ["rows"] = graph.Rows,
            ["cols"] = graph.Cols,
            ["boss"] = boss == null ? null : new { col = boss.Col, row = boss.Row, type = "Boss" },
            ["available"] = available,
            ["points"] = points,
            ["legend"] = MapApi.Legend(),
        });
    }

    public static Dictionary<string, object> Start(string characterId, string seed)
    {
        var rm = RunManager.Instance;
        if (rm == null) return Fail("RunManager unavailable");
        if (rm.IsInProgress) return Fail("a run is already in progress");

        // 实测结论：这条路永远开不出能玩的局。`RunManager.Launch()` 只建对局模型，
        // 不生成地图、不切场景（地图要么 rows=0，要么 CurrentRoom=null），结果是一个
        // 「inRun:true 但什么都干不了」的幽灵局；而且它会把后续所有通道一起封死——
        // menu 语义协议不接管（selector 被 RunManager 认为在局里）、/start 被 in-progress 拒、
        // /save-quit 因为 CurrentRoom=null 拒绝、/reload 又要求先回主菜单。
        // 唯一恢复办法是重启游戏。所以这里直接拒绝，连对局对象都不要造。
        return Fail(
            "/start 已禁用：它只会造出「inRun=true 但地图没生成」的幽灵局，并且封死后续所有通道。" +
            "开新局一律走菜单语义协议：GET /request → kind:\"menu\"，按 stage 依次回答 " +
            "main（单人模式）→ mode（标准模式）→ character（角色）→ ascension（进阶档）。");
    }


    /// <summary>Launch() only builds the RunState; generate the act map and enter its first node.</summary>
    private static async Task AdvanceIntoMap(RunManager rm)
    {
        try
        {
            await rm.GenerateMap();

            var map = rm.DebugOnlyGetState()?.Map;
            var start = map?.StartingMapPoint;
            if (start == null)
            {
                BridgeLog.Warn("GenerateMap produced no starting point");
                return;
            }

            await rm.EnterMapCoord(start.coord);
            BridgeLog.Info($"entered first map node ({start.coord.col},{start.coord.row})");
        }
        catch (Exception e)
        {
            BridgeLog.Error("advance into map failed", e);
        }
    }

    /// <summary>Full detail for one card. Pass uid to describe the exact hand card the agent is looking at.</summary>
    public static Dictionary<string, object> Card(string id, int? uid)
    {
        if (uid is { } hash)
        {
            var card = FindByUid(hash);
            if (card == null) return Fail($"no card with uid {hash} in hand or deck");
            Preview(card);
            return Detail(card);
        }

        if (string.IsNullOrWhiteSpace(id)) return Fail("missing id or uid");

        try
        {
            var found = ModelDb.GetByIdOrNull<CardModel>(ModelId.Deserialize(id));
            if (found == null) return Fail($"no card {id}");
            return Detail(found);
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>遗物/药水简介：奖励和商店里选东西时必须先看这个。</summary>
    public static Dictionary<string, object> Relic(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Fail("missing relic id");
        try
        {
            var model = ModelDb.GetByIdOrNull<RelicModel>(ModelId.Deserialize(id));
            if (model == null) return Fail($"no relic {id}");
            return Ok(new Dictionary<string, object>
            {
                ["id"] = model.Id?.ToString(),
                ["title"] = SafeText(model.Title),
                ["description"] = AnyText(model, "DynamicDescription", "Description", "EventDescription"),
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static Dictionary<string, object> Potion(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Fail("missing potion id");
        try
        {
            var model = ModelDb.GetByIdOrNull<PotionModel>(ModelId.Deserialize(id));
            if (model == null) return Fail($"no potion {id}");
            return Ok(new Dictionary<string, object>
            {
                ["id"] = model.Id?.ToString(),
                ["title"] = SafeText(model.Title),
                ["description"] = AnyText(model, "DynamicDescription", "Description"),
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    /// <summary>按属性名依次尝试取本地化文本（不同模型暴露的属性名不一样）。</summary>
    internal static string AnyText(object model, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            try
            {
                var prop = model.GetType().GetProperty(
                    name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop?.GetValue(model) is MegaCrit.Sts2.Core.Localization.LocString loc)
                {
                    var text = SafeText(loc);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// LocString → 人话（给 /data 的 title / description 用）。
    ///
    /// 取不到真实文案就返回 null：**绝不能**退回 <c>LocString.ToString()</c>，它是
    /// "LocString table &lt;表&gt; entry &lt;key&gt;" 这种调试用的内部表示，发出去等于把 key 漏给 agent
    /// （事件里 PROCEED.description 这种「表里有 key、文案是空串」的条目就会变成这种假文案）。
    /// </summary>
    internal static string SafeText(MegaCrit.Sts2.Core.Localization.LocString loc)
    {
        if (loc == null) return null;
        string raw = null;
        try { raw = loc.GetFormattedText(); } catch { }
        // GetFormattedText 自身在格式化失败时会退到 raw 文本，这里再兜一次（空/异常时）。
        if (string.IsNullOrWhiteSpace(raw)) { try { raw = loc.GetRawText(); } catch { } }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("LocString table ", System.StringComparison.Ordinal)
            && raw.Contains(" entry ", System.StringComparison.Ordinal))
            return null;
        // 描述里带 BBCode（[blue]2[/blue]），剥掉标签只留文字。
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\[[^\[\]]{1,24}\]", string.Empty);
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        return raw;
    }

    private static CardModel FindByUid(int uid)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state?.Players == null) return null;

        foreach (var player in state.Players)
        {
            var hand = player.PlayerCombatState?.Hand?.Cards;
            if (hand != null)
                foreach (var card in hand)
                    if (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(card) == uid) return card;

            var deck = player.Deck?.Cards;
            if (deck != null)
                foreach (var card in deck)
                    if (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(card) == uid) return card;
        }
        return null;
    }

    /// <summary>Ask the running game to show the card on screen, so the user can see what is being inspected.</summary>
    private static void Preview(CardModel card)
    {
        try
        {
            var type = typeof(MegaCrit.Sts2.Core.Commands.CardCmd);
            foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (method.Name != "Preview") continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 3 || parameters[0].ParameterType != typeof(CardModel)) continue;
                var styleType = parameters[2].ParameterType;
                if (!styleType.IsEnum) continue;

                foreach (var value in Enum.GetValues(styleType))
                {
                    try
                    {
                        method.Invoke(null, new object[] { card, 2.5f, value });
                        BridgeLog.Info($"previewed card {card.Title} with style {value}");
                        return;
                    }
                    catch (Exception inner)
                    {
                        BridgeLog.Warn($"preview style {value} failed: {(inner.InnerException ?? inner).Message}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("card preview failed: " + e.Message);
        }
    }

    private static Dictionary<string, object> Detail(CardModel card)
    {
        string text = null;
        try
        {
            text = card.Description?.GetFormattedText();
            if (text != null && card.DynamicVars != null)
            {
                foreach (var variable in card.DynamicVars.Values)
                {
                    var value = variable.IntValue.ToString();
                    text = Regex.Replace(text, "\\{" + Regex.Escape(variable.Name) + ":[^}]*\\}", value);
                    text = Regex.Replace(text, "\\{" + Regex.Escape(variable.Name) + "\\}", value);
                }
            }
        }
        catch { }

        return Ok(new Dictionary<string, object>
        {
            ["id"] = card.Id?.ToString(),
            ["uid"] = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(card),
            ["title"] = card.Title,
            ["cost"] = card.EnergyCost?.GetAmountToSpend() ?? card.EnergyCost?.Canonical ?? 0,
            ["text"] = text,
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            ["target"] = card.TargetType.ToString(),
            ["keywords"] = card.Keywords?.Select(k => k.ToString()).ToList(),
        });
    }

    private static Dictionary<string, object> Ok(Dictionary<string, object> payload)
    {
        var d = new Dictionary<string, object> { ["ok"] = true };
        foreach (var kv in payload) d[kv.Key] = kv.Value;
        return d;
    }

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}


