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

        // Boss 身份（问题 7）：光有坐标不够，构筑要提前知道是谁。
        // 数据来自 ActModel.BossEncounter / SecondBossEncounter（游戏自己的选择结果，不是猜的）。
        Dictionary<string, object> bossDto = null;
        if (boss != null)
        {
            bossDto = new Dictionary<string, object>
            {
                ["col"] = boss.Col,
                ["row"] = boss.Row,
                ["type"] = "Boss",
            };
            try
            {
                var actModel = state.Act;
                var encounter = actModel?.BossEncounter;
                if (encounter != null)
                {
                    bossDto["encounterId"] = encounter.Id?.ToString();
                    bossDto["title"] = SafeText(encounter.Title);
                    var tags = new List<object>();
                    try
                    {
                        if (encounter.Tags != null)
                            foreach (var tag in encounter.Tags) tags.Add(tag.ToString());
                    }
                    catch { }
                    if (tags.Count > 0) bossDto["tags"] = tags;

                    // 这一场 Boss 会出哪些怪（还没生成时游戏自己会返回空列表，不编）。
                    var monsters = new List<object>();
                    try
                    {
                        if (encounter.AllPossibleMonsters != null)
                            foreach (var monster in encounter.AllPossibleMonsters)
                            {
                                string name = null;
                                try { name = SafeText(monster?.Title); } catch { }
                                if (string.IsNullOrEmpty(name)) continue;
                                monsters.Add(new Dictionary<string, object>
                                {
                                    ["id"] = monster.Id?.ToString(),
                                    ["title"] = name,
                                });
                            }
                    }
                    catch { }
                    if (monsters.Count > 0) bossDto["monsters"] = monsters;
                }

                if (actModel?.HasSecondBoss == true && actModel.SecondBossEncounter != null)
                {
                    bossDto["secondBoss"] = new Dictionary<string, object>
                    {
                        ["encounterId"] = actModel.SecondBossEncounter.Id?.ToString(),
                        ["title"] = SafeText(actModel.SecondBossEncounter.Title),
                    };
                }
            }
            catch { }
        }

        return Ok(new Dictionary<string, object>
        {
            ["act"] = act,
            ["actFloor"] = state.ActFloor,
            ["totalFloor"] = state.TotalFloor,
            ["current"] = currentCoord == null ? null : new { col = currentCoord.Value.col, row = currentCoord.Value.row },
            ["rows"] = graph.Rows,
            ["cols"] = graph.Cols,
            ["boss"] = bossDto,
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

    /// <summary>
    /// Full detail for one card. Prefer <paramref name="id"/>; otherwise describe the exact hand/deck
    /// card by <paramref name="uid"/>, or resolve a Chinese <paramref name="title"/> (selection
    /// requests only expose titles, so this is the bridge from「主宰」to CARD.…).
    /// </summary>
    public static Dictionary<string, object> Card(string id, long? uid, string title = null)
    {
        // 1) id 优先：显式给了 id 就按 id 查，不和标题/uid 混。
        if (!string.IsNullOrWhiteSpace(id))
        {
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

        // 2) uid：卡牌实体 uid 是 RuntimeHelpers.GetHashCode（int）。界面控件 uid 是 60 位 ulong，
        //    两者不是一个命名空间；传错了要说清楚，而不是回一句误导的「missing id or uid」。
        if (uid is { } raw)
        {
            if (raw > int.MaxValue || raw < int.MinValue)
            {
                return Fail($"uid {raw} 看起来是界面控件 id（ulong），不是卡牌实体 uid。" +
                            "实体 uid 是 8 位整数，见 /state 的 players[0].hand[].uid 或 /deck 的 cards[].uid；" +
                            "只有标题时用 title= 查。");
            }
            var card = FindByUid((int)raw);
            if (card == null)
            {
                return Fail($"uid {raw} 不在手牌/牌库里（读档或换牌后 uid 会变，重新读 /state；" +
                            "只有标题时用 title= 查）。");
            }
            Preview(card);
            return Detail(card);
        }

        // 3) title：选牌/奖励界面只给中文名，按标题反查模型（手牌 → 牌库 → 全部卡池）。
        if (!string.IsNullOrWhiteSpace(title))
        {
            var matches = CardsByTitle(title);
            if (matches.Count == 0) return Fail($"no card titled 「{title}」（已搜手牌 / 牌库 / 全部卡池）");
            if (matches.Count == 1) return Detail(matches[0]);

            return Ok(new Dictionary<string, object>
            {
                ["ambiguous"] = true,
                ["title"] = title,
                ["note"] = "同名多张（升级版/衍生牌），挑一个 id 再查一次",
                ["matches"] = matches.Take(8).Select(c => new Dictionary<string, object>
                {
                    ["id"] = c.Id?.ToString(),
                    ["title"] = c.Title,
                    ["upgraded"] = c.IsUpgraded,
                    ["type"] = c.Type.ToString(),
                    ["cost"] = c.EnergyCost?.GetAmountToSpend() ?? c.EnergyCost?.Canonical ?? 0,
                }).ToList(),
            });
        }

        return Fail("missing id / uid / title（三种至少给一个）");
    }

    /// <summary>按中文名找牌：手牌 → 牌库 → 全部卡池（AllCards）。同名多张都返回。</summary>
    private static List<CardModel> CardsByTitle(string title)
    {
        var list = new List<CardModel>();
        var seen = new HashSet<int>();
        void Add(CardModel c)
        {
            if (c == null) return;
            if (!string.Equals((c.Title ?? "").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c))) list.Add(c);
        }

        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            foreach (var player in state?.Players ?? Enumerable.Empty<Player>())
            {
                foreach (var c in player.PlayerCombatState?.Hand?.Cards ?? Enumerable.Empty<CardModel>()) Add(c);
                foreach (var c in player.Deck?.Cards ?? Enumerable.Empty<CardModel>()) Add(c);
            }
        }
        catch { }

        if (list.Count == 0)
        {
            try { foreach (var c in ModelDb.AllCards) Add(c); } catch { }
        }
        return list;
    }

    /// <summary>遗物/药水简介：奖励和商店里选东西时必须先看这个。</summary>
    public static Dictionary<string, object> Relic(string id, string title = null)
    {
        if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var hit = ModelDb.AllRelics.FirstOrDefault(r =>
                    string.Equals((SafeText(r.Title) ?? "").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit == null) return Fail($"no relic titled 「{title}」");
                id = hit.Id?.ToString();
            }
            catch (Exception e) { return Fail(e.Message); }
        }
        if (string.IsNullOrWhiteSpace(id)) return Fail("missing relic id / title");
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

    public static Dictionary<string, object> Potion(string id, string title = null)
    {
        if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var hit = ModelDb.AllPotions.FirstOrDefault(p =>
                    string.Equals((SafeText(p.Title) ?? "").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit == null) return Fail($"no potion titled 「{title}」");
                id = hit.Id?.ToString();
            }
            catch (Exception e) { return Fail(e.Message); }
        }
        if (string.IsNullOrWhiteSpace(id)) return Fail("missing potion id / title");
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

    /// <summary>
    /// 能力 / buff 说明：`POWER.XXX → 标题 + 文案`。数据源是游戏自己的 <c>PowerModel</c>
    /// （Title / Description / SmartDescription），和 /card、/relic、/potion 同一套写法。
    /// </summary>
    public static Dictionary<string, object> Power(string id, string title = null)
    {
        try
        {
            PowerModel model = null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                model = ModelDb.GetByIdOrNull<PowerModel>(ModelId.Deserialize(id));
            }
            else if (!string.IsNullOrWhiteSpace(title))
            {
                model = ModelDb.AllPowers.FirstOrDefault(p =>
                    string.Equals((SafeText(p.Title) ?? "").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (model == null)
                return Fail(string.IsNullOrWhiteSpace(id)
                    ? $"no power titled 「{title}」"
                    : $"no power {id}");

            return Ok(new Dictionary<string, object>
            {
                ["id"] = model.Id?.ToString(),
                ["title"] = SafeText(model.Title),
                ["description"] = SafeText(model.Description) ?? SafeText(model.SmartDescription),
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    /// <summary>
    /// 玩家当前身上的全部遗物（带中文名与效果），和 /potions 同构。
    /// 以前只有单查，10 件遗物要 11 次调用。
    /// </summary>
    public static Dictionary<string, object> Relics()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null || !rm.IsInProgress) return Fail("not in run");
            var state = rm.DebugOnlyGetState();
            var player = state?.Players?.FirstOrDefault();
            if (player == null) return Fail("no players");

            var list = new List<object>();
            var index = 0;
            foreach (var relic in player.Relics)
            {
                if (relic == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["index"] = index++,
                    ["id"] = relic.Id?.ToString(),
                    ["title"] = SafeText(relic.Title),
                    ["description"] = AnyText(relic, "DynamicDescription", "Description", "EventDescription"),
                });
            }

            return Ok(new Dictionary<string, object>
            {
                ["count"] = list.Count,
                ["relics"] = list,
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

    /// <summary>
    /// 卡面文字（动态数值已展开、BBCode 已剥）。`/card` 与 `/request` 候选的 detail 共用这一套，
    /// 免得同一个模型在两条路上给出两种说法。
    /// </summary>
    internal static string CardText(CardModel card)
    {
        if (card == null) return null;
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

        if (string.IsNullOrWhiteSpace(text)) return null;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\[\]]{1,24}\]", string.Empty);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private static Dictionary<string, object> Detail(CardModel card)
    {
        var text = CardText(card);

        var dto = new Dictionary<string, object>
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
            ["isUpgraded"] = card.IsUpgraded,
        };

        // 升级后是什么样（营火锻造 / 武装这类决策要用）。克隆再升，绝不动游戏里的本体。
        var up = UpgradedCopy(card);
        if (up != null)
        {
            dto["upgraded"] = new Dictionary<string, object>
            {
                ["title"] = up.Title,
                ["text"] = CardText(up),
                ["vars"] = VarsMap(up),
            };
        }

        return Ok(dto);
    }

    /// <summary>升级后的**副本**（绝不改游戏里的那张本体）。已满级/拿不到就 null。</summary>
    internal static CardModel UpgradedCopy(CardModel card)
    {
        if (card == null) return null;
        try
        {
            if (card.IsUpgraded || card.CurrentUpgradeLevel >= card.MaxUpgradeLevel) return null;
            var clone = card.ToMutable();
            if (clone == null || ReferenceEquals(clone, card)) clone = card.CreateClone();
            if (clone == null || ReferenceEquals(clone, card)) return null;
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();
            return clone;
        }
        catch { return null; }
    }

    /// <summary>升级前后的动态数值差（只列变了的，紧凑）：{ "Damage": "6 → 9" }。</summary>
    internal static Dictionary<string, object> UpgradeDelta(CardModel card)
    {
        var up = UpgradedCopy(card);
        if (up == null) return null;
        try
        {
            var before = VarsMap(card);
            var after = VarsMap(up);
            var delta = new Dictionary<string, object>();
            foreach (var kv in after)
            {
                before.TryGetValue(kv.Key, out var old);
                if (kv.Value != old) delta[kv.Key] = $"{old} → {kv.Value}";
            }
            return delta.Count > 0 ? delta : null;
        }
        catch { return null; }
    }

    private static Dictionary<string, int> VarsMap(CardModel card)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (card?.DynamicVars != null)
                foreach (var v in card.DynamicVars.Values)
                    if (!string.IsNullOrEmpty(v.Name)) map[v.Name] = v.IntValue;
        }
        catch { }
        return map;
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


