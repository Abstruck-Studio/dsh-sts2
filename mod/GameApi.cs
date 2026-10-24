using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>Reads game state and injects player actions. All members must be called on the main thread.</summary>
public static class GameApi
{
    private static readonly AutoSlayer Slayer = new();

    // ---------------------------------------------------------------- state

    public static Dictionary<string, object> State()
    {
        var d = new Dictionary<string, object>();
        var rm = RunManager.Instance;

        d["agentMode"] = AgentUi.AgentMode;

        if (rm == null || !rm.IsInProgress)
        {
            d["inRun"] = false;
            d["autoSlay"] = AutoSlayer.IsActive;
            return d;
        }

        var s = rm.DebugOnlyGetState();
        if (s == null)
        {
            d["inRun"] = false;
            d["note"] = "RunManager in progress but State is null";
            return d;
        }

        d["inRun"] = true;
        d["autoSlay"] = AutoSlayer.IsActive;
        d["singleplayer"] = rm.IsSingleplayerOrFakeMultiplayer;
        d["act"] = s.Act?.Id?.ToString();
        d["actFloor"] = s.ActFloor;
        d["totalFloor"] = s.TotalFloor;
        d["ascension"] = s.AscensionLevel;
        d["gameMode"] = s.GameMode.ToString();
        d["gameOver"] = s.IsGameOver;
        d["room"] = s.CurrentRoom?.RoomType.ToString();
        // 动作队列阶段：PlayPhase=可以出牌；NotPlayPhase=在等玩家选择/敌人行动；EndTurnPhaseOne=回合过渡。
        try
        {
            d["combatState"] = RunManager.Instance?.ActionQueueSynchronizer?.CombatState.ToString();
        }
        catch { }
        d["currentCoord"] = Coord(s.CurrentMapCoord);

        var players = new List<object>();
        for (var i = 0; i < s.Players.Count; i++) players.Add(PlayerDto(s.Players[i], s, i));
        d["players"] = players;

        if (s.CurrentRoom is CombatRoom room && room.CombatState is { } cs)
        {
            d["combat"] = Combat(cs, players.Count);
        }

        return d;
    }

    private static Dictionary<string, object> Combat(CombatState cs, int playerCount)
    {
        var d = new Dictionary<string, object>
        {
            ["round"] = cs.RoundNumber,
            ["side"] = cs.CurrentSide.ToString(),
            ["encounter"] = cs.Encounter?.Id?.ToString(),
        };

        var enemies = new List<object>();
        foreach (var c in cs.Enemies) enemies.Add(CreatureDto(c));
        d["enemies"] = enemies;

        var hittable = new List<object>();
        foreach (var c in cs.HittableEnemies) hittable.Add(CreatureDto(c));
        d["hittable"] = hittable;

        // 我方召唤物（亡魂契约师的奥斯提、各种召唤）——以前完全没输出，
        // agent 看不到自己场上还有个东西，伤害和格挡都会算错。
        try
        {
            var allies = new List<object>();
            var alliesRaw = cs.Allies;
            if (alliesRaw != null)
            {
                foreach (var c in alliesRaw)
                {
                    if (c == null) continue;
                    var isPlayer = false;
                    try { isPlayer = c.Player != null; } catch { }
                    if (isPlayer) continue;   // Allies 里含玩家自己，只留真召唤物
                    var dto = CreatureDto(c);
                    try { dto["isSummon"] = true; } catch { }
                    allies.Add(dto);
                }
            }
            d["summons"] = allies;
        }
        catch { d["summons"] = new List<object>(); }

        return d;
    }

    private static Dictionary<string, object> PlayerDto(Player p, RunState s, int index)
    {
        var d = new Dictionary<string, object>
        {
            ["index"] = index,
            ["name"] = p.Character?.Id?.ToString(),
            ["netId"] = p.NetId,
            ["gold"] = p.Gold,
            ["hp"] = p.Creature?.CurrentHp ?? 0,
            ["maxHp"] = p.Creature?.MaxHp ?? 0,
            ["block"] = p.Creature?.Block ?? 0,
            ["powers"] = p.Creature?.Powers?.Select(pw => new Dictionary<string, object>
            {
                ["id"] = pw.Id?.ToString(),
                ["amount"] = pw.Amount,
            }).ToList<object>(),
            ["freeAttacks"] = p.Creature?.Powers?
                .Where(pw => pw.Id?.ToString()?.Contains("FREE_ATTACK") == true)
                .Sum(pw => pw.Amount) ?? 0,
            ["potions"] = p.PotionSlots?.Where(x => x != null).Select(x => x.Id?.ToString()).ToList(),
            ["relics"] = p.Relics.Select(r => r.Id.ToString()).ToList(),
            ["deckSize"] = p.Deck?.Cards?.Count ?? 0,
            // 不在战斗里没有手牌，这个字段恒为 true，让调用方不用判空。
            ["handComplete"] = true,
        };

        var pcs = p.PlayerCombatState;
        if (pcs != null)
        {
            d["energy"] = pcs.Energy;
            d["maxEnergy"] = pcs.MaxEnergy;
            d["stars"] = pcs.Stars;
            d["turn"] = pcs.TurnNumber;
            d["drawPile"] = pcs.DrawPile?.Cards?.Count ?? 0;
            d["discardPile"] = pcs.DiscardPile?.Cards?.Count ?? 0;
            d["exhaustPile"] = pcs.ExhaustPile?.Cards?.Count ?? 0;
            d["hand"] = pcs.Hand?.Cards?.Select(CardDto).ToList<object>() ?? new List<object>();
            // 手牌是模型层的真值，这里不做任何截断；但发牌动画没播完时模型里就是只有 1~3 张，
            // 所以另外给一个「这手牌还在变吗」的低成本判定（见 HandComplete）。
            d["handComplete"] = HandComplete(index);
        }

        return d;
    }

    private static Dictionary<string, object> CardDto(CardModel c)
    {
        var dto = new Dictionary<string, object>
        {
            ["id"] = c.Id?.ToString(),
            ["uid"] = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c),
            ["title"] = c.Title,
            ["upgraded"] = c.IsUpgraded,
            ["cost"] = EffectiveCost(c),
            ["baseCost"] = c.EnergyCost?.Canonical ?? 0,
            ["vars"] = VarsOf(c),
            ["costX"] = c.EnergyCost?.CostsX ?? false,
            ["starCost"] = c.CurrentStarCost,
            ["type"] = c.Type.ToString(),
            ["rarity"] = c.Rarity.ToString(),
            ["target"] = c.TargetType.ToString(),
            ["keywords"] = c.Keywords?.Select(k => k.ToString()).ToList(),
        };

        // 这张牌现在能不能打：用游戏自己的统一入口 CardModel.CanPlay(out UnplayableReason, out AbstractModel)，
        // 不再让 agent 靠「点一下试试」去猜（费用以外的条件它看不见，会一直重试同一张牌）。
        var playable = Playability(c);
        dto["playable"] = playable.Playable;
        if (!playable.Playable)
        {
            dto["unplayableReason"] = playable.Reason;
            dto["unplayableReasonCode"] = playable.Code;
            if (!string.IsNullOrEmpty(playable.Source)) dto["unplayableSource"] = playable.Source;
        }

        return dto;
    }

    /// <summary>
    /// 一张牌的「能不能打」现场。<see cref="Reason"/> 是给人看的一句话（只说游戏真的给出的原因，
    /// 分不清就写「条件不满足」），<see cref="Code"/> 是游戏 <c>UnplayableReason</c> 枚举自己的名字
    /// （多个原因时是 "A, B"），<see cref="Source"/> 是游戏指出的「是谁拦的」（卡/遗物/能力，可能为空）。
    /// </summary>
    internal sealed class CardPlayability
    {
        public bool Playable;
        public string Reason;
        public string Code;
        public string Source;

        /// <summary>牌名（只有 <see cref="PlayabilityOfHandCard"/> 会填，给 /acts 的报错文案用）。</summary>
        public string Title;
    }

    /// <summary>
    /// 走 <c>CardModel.CanPlay(out UnplayableReason, out AbstractModel)</c>（sts2.dll 的 IL 核实过：
    /// 它就是界面判断「这张牌能不能打出」的那一个入口，按顺序累加这些位）：
    ///   * <c>HasUnplayableKeyword</c>（key 里有 Unplayable）—— bit 2；
    ///   * <c>PlayerCombatState.HasEnoughResourcesFor(card, out reason)</c> → 能量 / 星费不够（EnergyCostTooHigh / StarCostTooHigh）；
    ///   * <c>Hook.ShouldPlay</c> 为假 → <c>BlockedByHook</c> —— bit 4；
    ///   * <c>CardModel.IsPlayable</c>（子类 override，比如「本回合已经打出过技能牌」）为假 → <c>BlockedByCardLogic</c> —— bit 8；
    ///   * 需要队友的目标但没有活着的队友 → <c>NoLivingAllies</c> —— bit 64。
    /// 返回值 = (reason == None)。不在战斗里（没有 CombatState）时它返回 false 且 reason = None，这时不算
    /// 「有原因」——调用方看到的是「条件不满足」，不编具体理由。
    /// </summary>
    private static CardPlayability Playability(CardModel card)
    {
        var result = new CardPlayability();
        if (card == null)
        {
            result.Reason = "条件不满足";
            result.Code = "Unknown";
            return result;
        }

        UnplayableReason reason;
        AbstractModel source = null;
        bool canPlay;
        try
        {
            canPlay = card.CanPlay(out reason, out source);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("playability: CanPlay 抛异常（" + e.GetType().Name + "）：" + e.Message);
            result.Playable = true;      // 读不出来就不要拦（宁可让游戏自己拒绝）
            return result;
        }

        if (canPlay)
        {
            result.Playable = true;
            return result;
        }

        result.Playable = false;
        result.Code = reason == UnplayableReason.None ? "Unknown" : reason.ToString();
        result.Source = SourceTitle(source);

        // 文案优先级：能量 > 星费 > 队友 > 关键字 > 其它条件。只写游戏真的给出的那一个，
        // 复合原因的具体组合留在 unplayableReasonCode 里（不合并成一句编出来的话）。
        if ((reason & UnplayableReason.EnergyCostTooHigh) != 0) result.Reason = "能量不足";
        else if ((reason & UnplayableReason.StarCostTooHigh) != 0) result.Reason = "星费不足";
        else if ((reason & UnplayableReason.NoLivingAllies) != 0) result.Reason = "没有存活的队友";
        else if ((reason & UnplayableReason.HasUnplayableKeyword) != 0) result.Reason = "这张牌不能打出";
        else result.Reason = "条件不满足";

        return result;
    }

    /// <summary>拦下这张牌的那个模型的名字（读不到就算了，不编）。</summary>
    private static string SourceTitle(AbstractModel source)
    {
        if (source == null) return null;
        try
        {
            var prop = source.GetType().GetProperty("Title", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(string))
                return prop.GetValue(source) as string;
        }
        catch { }
        return null;
    }

    /// <summary>手牌里 uid 对应的那张牌的可用性（给 /acts 的预检用）。uid 不在手里返回 null。</summary>
    internal static CardPlayability PlayabilityOfHandCard(int uid)
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var hand = state?.Players is { Count: > 0 } players ? players[0].PlayerCombatState?.Hand?.Cards : null;
            if (hand == null) return null;
            foreach (var card in hand)
            {
                if (card == null) continue;
                if (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(card) != uid) continue;
                var playability = Playability(card);
                playability.Title = card.Title;
                return playability;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("playability: 读手牌失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return null;
    }

    /// <summary>
    /// The energy actually needed right now. Canonical is the printed/base cost and ignores
    /// modifiers (free-this-turn, Corruption, ...), which made the agent skip affordable cards.
    /// </summary>
    private static int EffectiveCost(CardModel card)
    {
        try
        {
            var cost = card.EnergyCost;
            if (cost == null) return 0;
            return cost.GetAmountToSpend();
        }
        catch
        {
            try { return card.EnergyCost?.Canonical ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>Structured numbers (damage/block/stacks) so the agent never has to parse card text.</summary>
    private static Dictionary<string, int> VarsOf(CardModel card)
    {
        var values = new Dictionary<string, int>();
        try
        {
            if (card.DynamicVars == null) return values;
            foreach (var variable in card.DynamicVars.Values)
            {
                if (variable == null || string.IsNullOrEmpty(variable.Name)) continue;
                values[variable.Name] = variable.IntValue;
            }
        }
        catch { }
        return values;
    }

    private static Dictionary<string, object> CreatureDto(Creature c) => new()
    {
        ["name"] = c.Name,
        ["hp"] = c.CurrentHp,
        ["maxHp"] = c.MaxHp,
        ["block"] = c.Block,
        ["hittable"] = c.IsHittable,
        ["side"] = c.Side.ToString(),
        ["intents"] = IntentsOf(c),
        ["powers"] = c.Powers?.Select(p => new Dictionary<string, object>
        {
            ["id"] = p.Id?.ToString(),
            ["amount"] = p.Amount,
        }).ToList<object>(),
    };

    /// <summary>What the monster is about to do: type plus damage/repeats for attacks.</summary>
    private static List<object> IntentsOf(Creature c)
    {
        var result = new List<object>();
        try
        {
            var intents = c.Monster?.NextMove?.Intents;
            if (intents == null) return result;

            foreach (var intent in intents)
            {
                var entry = new Dictionary<string, object> { ["type"] = intent.IntentType.ToString() };
                if (intent is AttackIntent attack)
                {
                    // The UI shows damage after strength/weak modifiers; DamageCalc alone is the base.
                    var targets = c.CombatState?.PlayerCreatures ?? new[] { c };
                    try
                    {
                        entry["damage"] = attack.GetTotalDamage(targets, c);
                        entry["single"] = attack.GetSingleDamage(targets, c);
                        entry["base"] = (int)attack.DamageCalc();
                    }
                    catch
                    {
                        entry["damage"] = (int)attack.DamageCalc();
                    }
                    entry["repeats"] = attack.Repeats;
                }
                result.Add(entry);
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("intent read failed: " + e.Message);
        }
        return result;
    }

    private static string SafeText(MegaCrit.Sts2.Core.Localization.LocString text)
    {
        try { return text?.GetFormattedText(); }
        catch { return null; }
    }

    private static Dictionary<string, object> Coord(MapCoord? c) =>
        c is { } v ? new Dictionary<string, object> { ["col"] = v.col, ["row"] = v.row } : null;

    // --------------------------------------------------------------- actions

    public static Dictionary<string, object> Act(JsonElement req)
    {
        var kind = req.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(kind)) return Fail("missing 'kind'");

        var rm = RunManager.Instance;
        if (rm == null) return Fail("RunManager not available");

        return kind switch
        {
            "play_card" => PlayCard(rm, req),
            "end_turn" => EndTurn(rm, req),
            // move 是「发起 + 轮询确认」的两段式，必须在 HTTP 线程上跑（见 MoveAndConfirm）。
            // /act 与 /acts 都会在调用 Act 之前先分流，走到这里说明有人绕过了确认流程。
            "move" => Fail("move 必须走确认流程：请 POST /act {\"kind\":\"move\",\"col\":..,\"row\":..}（内部走 MoveAndConfirm）"),
            "use_potion" => UsePotion(req),
            "discard_potion" => DiscardPotion(req),
            "autoslay" => AutoSlay(req),
            _ => Fail($"unknown kind '{kind}'"),
        };
    }

    private static Dictionary<string, object> PlayCard(RunManager rm, JsonElement req)
    {
        var s = rm.DebugOnlyGetState();
        if (s?.CurrentRoom is not CombatRoom room || room.CombatState is not { } cs)
            return Fail("not in combat");

        var p = PlayerAt(s, req);
        if (p == null) return Fail("no player");
        var pcs = p.PlayerCombatState;
        if (pcs == null) return Fail("player is not in combat");

        var hand = pcs.Hand?.Cards;
        if (hand == null || hand.Count == 0) return Fail("hand is empty");

        // Prefer the stable uid: hand indices shift after every play.
        CardModel card = null;
        var cardIdx = -1;
        if (req.TryGetProperty("uid", out var uidEl) && uidEl.ValueKind == JsonValueKind.Number)
        {
            var uid = uidEl.GetInt32();
            for (var i = 0; i < hand.Count; i++)
            {
                if (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hand[i]) != uid) continue;
                cardIdx = i;
                card = hand[i];
                break;
            }
            if (card == null) return Fail($"no card with uid {uid} in hand");
        }
        else
        {
            if (!req.TryGetProperty("card", out var cardEl) || cardEl.ValueKind != JsonValueKind.Number)
                return Fail("missing 'uid' (preferred) or 'card' index");

            cardIdx = cardEl.GetInt32();
            if (cardIdx < 0 || cardIdx >= hand.Count) return Fail($"card index {cardIdx} out of range 0..{hand.Count - 1}");
            card = hand[cardIdx];
        }

        Creature target = null;
        if (req.TryGetProperty("target", out var tEl) && tEl.ValueKind == JsonValueKind.Number)
        {
            var targetIdx = tEl.GetInt32();
            var hittable = cs.HittableEnemies;
            if (hittable == null || targetIdx < 0 || targetIdx >= hittable.Count)
                return Fail($"target index {targetIdx} out of range");
            target = hittable[targetIdx];
        }

        // 游戏自己说打不出去就先挡住：排进队列的牌会被静默丢掉，agent 只能靠「6 秒后没生效」去猜。
        // 失败体必须带上原因（playable/unplayableReason/unplayableReasonCode），它才知道换哪张。
        var playability = Playability(card);
        if (!playability.Playable)
        {
            BridgeLog.Info($"play_card rejected: {card.Title} unplayable ({playability.Code})");
            var rejected = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = $"这张牌现在打不出去：{playability.Reason}（{card.Title}）",
                ["playable"] = false,
                ["unplayableReason"] = playability.Reason,
                ["unplayableReasonCode"] = playability.Code,
                ["card"] = card.Title,
            };
            if (!string.IsNullOrEmpty(playability.Source)) rejected["unplayableSource"] = playability.Source;
            return rejected;
        }

        var action = new PlayCardAction(card, target);

        // A target index is only valid for single-enemy cards; AoE/self cards must pass null or
        // the game rejects the action silently (no energy spent, no effect).
        if (target != null && card.TargetType != TargetType.AnyEnemy)
        {
            BridgeLog.Info($"card {card.Title} targetType={card.TargetType} -> dropping target");
            target = null;
            action = new PlayCardAction(card, null);
        }

        rm.ActionQueueSynchronizer.RequestEnqueue(action);

        BridgeLog.Info($"play_card hand[{cardIdx}]={card.Title} target={(target != null ? target.Name : "<none>")}");
        return Ok(new Dictionary<string, object>
        {
            ["played"] = card.Title,
            ["target"] = target?.Name,
        });
    }

    private static Dictionary<string, object> EndTurn(RunManager rm, JsonElement req)
    {
        var s = rm.DebugOnlyGetState();
        var p = PlayerAt(s, req);
        if (p == null) return Fail("no player");

        PlayerCmd.EndTurn(p, true, () => Task.CompletedTask);
        BridgeLog.Info("end_turn");
        return Ok(new Dictionary<string, object> { ["endedTurn"] = true });
    }

    // ------------------------------------------------------- move / 地图节点
    //
    // 旧实现是 `_ = EnterRoom(...)`（fire-and-forget），所以不管进没进房间都回 ok:true。
    // 实测里 EnterMapCoord 会在 RunManager.EnterMapCoordInternal 空引用（坐标不在本幕地图上 /
    // 地图没生成好），agent 却以为已经进了房间。
    //
    // 现在分两段：
    //   ① 主线程：校验 + 发起（BeginMove），并记下「发起前的房间实例」；
    //   ② HTTP 线程：等 EnterMapCoord 这个 Task + 轮询主线程状态，确认「坐标到了 **且** 房间换成了
    //      另一个实例」才算成功。只变了坐标不算 —— RunState.CurrentMapCoord 是
    //      `_visitedMapCoords.Last()`，EnterMapCoord 一进来就先 AddVisitedMapCoord，坐标在真正建房间
    //      之前就已经变了（这正是旧实现看不出来的假成功）。

    /// <summary>发起后最多等这么久让房间真的建出来。</summary>
    private const int MoveConfirmMs = 4000;

    /// <summary>Act 刚切换时给新一幕地图留的稳定时间；超过还不好就判定地图状态坏了。</summary>
    private const int MoveActSettleMs = 3000;

    /// <summary>轮询主线程状态的间隔。</summary>
    private const int MovePollMs = 150;

    /// <summary>同一时刻只允许一个移动在飞。</summary>
    private static volatile bool _moving;

    /// <summary>移动序号：每次发起 +1，EnterRoom 的收尾只认自己那一次。</summary>
    private static volatile int _moveSeq;
    private static volatile string _moveError;
    private static volatile bool _moveErrorIsNre;
    private static volatile string _moveErrorWhere;

    /// <summary>
    /// 移动并**确认真的进了房间**。必须在 HTTP 线程上调用（它会 await 主线程任务，主线程上调用会死锁）。
    /// </summary>
    public static async Task<Dictionary<string, object>> MoveAndConfirm(JsonElement req)
    {
        var sw = Stopwatch.StartNew();

        // JsonElement 只在 HTTP 的 case 块里活着：先把参数抠成局部变量，后面不再碰 req。
        if (!TryInt(req, "col", out var col) || !TryInt(req, "row", out var row))
            return Fail("missing 'col' / 'row'");
        var playerIdx = TryInt(req, "player", out var requested) ? requested : 0;
        var movingTo = new { col, row };

        // ① Act 刚切换、地图还在生成时先等它稳下来。判定「地图没准备好」= Map 为空 / StartingMapPoint
        //    为空。注意：单纯 actFloor=0 + room="Map" + CurrentMapPoint=null **不是**坏状态 ——
        //    EnterAct 在进下一幕时只建一个 MapRoom（EnterAct 的 IL 里就是 new MapRoom + EnterRoomInternal），
        //    这时游戏自己只把「起点节点」(列数/2, 0) 标成可走，玩家本来就要从它开始。
        var actWaited = 0;
        var before = ProbeMap(playerIdx);
        while (before != null && before.ActTransitionPending && actWaited < MoveActSettleMs)
        {
            await Task.Delay(250);
            actWaited += 250;
            before = ProbeMap(playerIdx);
        }
        if (before == null) return Fail("RunManager 不可用");

        var atActBoundary = before.ActBoundary;
        if (before.ActTransitionPending)
        {
            return MoveFailed(movingTo, new List<object>(), sw, true,
                "移动前的地图状态就不正常：actFloor=0、room=\"" + (before.Room ?? "null") + "\"、totalFloor=" +
                before.TotalFloor + "，等了 " + actWaited +
                "ms 新一幕的地图仍然没生成好（Map=" + (before.MapNull ? "null" : "有") +
                "，StartingMapPoint=" + (before.MapReady ? "有" : "null") +
                "）。这一局的地图状态已经坏了，需要重开：不要盲目重试，重试只会把 visited 坐标写乱。");
        }

        // ② 主线程：校验 + 发起。
        MoveStart start;
        try { start = await BridgePump.OnMain(() => BeginMove(playerIdx, col, row), 6000); }
        catch (Exception e) { return Fail("发起 move 失败：" + e.GetType().Name + ": " + e.Message); }

        if (start == null) return Fail("发起 move 失败：主线程没有返回结果");
        if (start.Error != null)
            return MoveFailed(movingTo, start.Available, sw, start.ActTransition || atActBoundary, start.Error);

        // ③ 等 EnterMapCoord 这个 Task，同时轮询主线程现场。
        MapProbe after = null;
        var taskFinished = false;
        while (sw.ElapsedMilliseconds < MoveConfirmMs)
        {
            var tick = Task.Delay(MovePollMs);
            var winner = start.Task == null ? tick : await Task.WhenAny(start.Task, tick);
            taskFinished = start.Task != null && winner == start.Task;

            after = ProbeMap(playerIdx);
            if (after != null && after.RoomReached(col, row, start.RoomId))
                return MoveSucceeded(movingTo, after, sw);

            if (taskFinished)
            {
                // EnterMapCoord 已经结束：给它 120ms 让房间节点/动画落地，再看最后一眼。
                await Task.Delay(120);
                after = ProbeMap(playerIdx);
                if (after != null && after.RoomReached(col, row, start.RoomId))
                    return MoveSucceeded(movingTo, after, sw);
                break;
            }
        }

        after ??= ProbeMap(playerIdx);
        var moveError = TakeMoveError(start.Seq);
        var actBoundaryNow = atActBoundary || (after != null && after.ActBoundary);
        var mapBrokenNow = after != null && after.ActTransitionPending;

        var text = new StringBuilder("没能进入房间：地图状态没有变化");
        text.Append("（等了 ").Append((int)sw.ElapsedMilliseconds).Append("ms，room 仍然是 ")
            .Append(after?.Room ?? "null").Append("，currentCoord 仍然是 ").Append(CoordText(after)).Append("）");
        if (moveError != null)
        {
            text.Append("。游戏侧抛出 ").Append(moveError.Text);
            if (!string.IsNullOrEmpty(moveError.Where)) text.Append("（").Append(moveError.Where).Append("）");
            if (moveError.IsNre)
                text.Append("。这就是「Act 切换后地图状态异常」：EnterMapCoordInternal 里 " +
                            "State.Map.GetPoint(coord) 拿不到点（坐标不在本幕地图上，或新一幕的地图还没生成好）");
        }
        if (mapBrokenNow)
            text.Append("。新一幕的地图到现在还是没生成好（Map/StartingMapPoint 为空）：" +
                        "这一局的地图状态已经坏了，需要重开；不要盲目重试，重试只会把 visited 坐标写乱");
        else if (actBoundaryNow)
            text.Append("。现在是刚切幕的地图状态（actFloor=0、room=Map）：只能走 available 里的起点节点，别用上一幕的坐标");

        return MoveFailed(movingTo, start.Available, sw, actBoundaryNow, text.ToString(), moveError?.Text);
    }

    /// <summary>发起一次移动（主线程）：校验坐标、记下发起前的现场、把 EnterMapCoord 丢到后台等它。</summary>
    private static MoveStart BeginMove(int playerIdx, int col, int row)
    {
        var start = new MoveStart();
        var rm = RunManager.Instance;
        if (rm == null) { start.Error = "RunManager not available"; return start; }

        var s = rm.DebugOnlyGetState();
        if (s == null) { start.Error = "还没开始一局"; return start; }

        var p = PlayerAt(s, playerIdx);
        if (p == null) { start.Error = "no player"; return start; }

        var coord = new MapCoord(col, row);
        start.Available = AvailableJson(s);
        start.ActTransition = s.ActFloor == 0 && s.TotalFloor > 0
                              && (s.CurrentRoom == null || s.CurrentRoom.RoomType.ToString() == "Map");

        // 只有「还没有当前房间」这一种情况会让 ClearScreens() 空引用（黑屏）。
        // 战斗结束后 CurrentRoom 仍是战斗房间对象，所以不能按房间类型拦截。
        if (s.CurrentRoom == null)
        {
            start.Error = "这局没有当前房间（CurrentRoom=null）：半初始化的对局，move 一定失败。" + " 先回主菜单（或放弃本局）重新开局，不要在这局上继续操作。";
            return start;
        }

        // 战斗还没打完时也不能 move —— 那等于「逃跑」，直接把战斗（和它的奖励）跳过。
        try
        {
            var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (cm != null && cm.IsInProgress)
            {
                start.Error = "战斗还没结束，不能 move（会直接跳过这场战斗和它的奖励）。" +
                              "打完这场再用 /move";
                return start;
            }
        }
        catch { }

        // 人还在房间里（事件/营火/商店/宝箱/奖励…）时不能直接 move —— 那会把整个房间跳过，
        // 事件选项、营火选项、宝箱遗物全丢。必须先用 /request + /respond 处理完，再 /proceed 离开。
        // 判据抽到 RoomBlocksTravel 里：/map/next 用的是同一个方法，不会出现「next 说能走、move 被拒」。
        var roomType = s.CurrentRoom.RoomType.ToString();
        if (RoomBlocksTravel(s, out var blockedRoom))
        {
            start.Error = $"你现在还在 {blockedRoom} 房间里（坐标 ({s.CurrentMapCoord?.col},{s.CurrentMapCoord?.row})），" +
                          "直接 move 会把整个房间跳过（事件选项/营火选项/宝箱遗物都会丢）。" +
                          "先用 /request 看有什么要决定的，用 /respond 做完，再看能不能 /proceed 离开这个房间";
            return start;
        }

        var map = s.Map;
        if (map == null || !map.HasPoint(coord))
        {
            start.Error = s.ActFloor == 0 && s.TotalFloor > 0
                ? $"地图里没有坐标 ({col},{row})：刚切幕时只能走 available 里的起点节点" +
                  "（EnterMapCoordInternal 会对不在本幕地图上的坐标空引用，并把脏坐标写进 visited）"
                : $"地图里没有坐标 ({col},{row})：它不在本幕地图上（EnterMapCoordInternal 会空引用）";
            return start;
        }

        if (s.VisitedMapCoords != null && s.VisitedMapCoords.Contains(coord))
        {
            // 人就在这个坐标的房间里：这不是「走过」，是「你现在在里面」，别再 move 了。
            var inThatRoom = s.CurrentMapCoord is { } cc0 && cc0.col == col && cc0.row == row
                             && roomType != "Map";
            if (inThatRoom)
            {
                start.Error = $"你现在就在 ({col},{row}) 这个房间里（{roomType}），不用再 move。" +
                              "要处理房间内容就用 /request + /respond，要离开就用 /proceed";
                return start;
            }

            // 坐标已经写进 visited、人却还停在地图上 = EnterMapCoord 写了一半就炸了
            // （AddVisitedMapCoord 在真正建房间之前就把坐标记下了）。这时候重试也不会有效果。
            var stuckOnMap = s.CurrentMapCoord is { } cc && cc.col == col && cc.row == row
                             && s.CurrentRoom.RoomType.ToString() == "Map";
            if (stuckOnMap && s.ActFloor == 0 && s.TotalFloor > 0) start.ActTransition = true;
            start.Error = stuckOnMap
                ? $"坐标 ({col},{row}) 已经写进 visited，但房间没建出来、人还停在地图上 —— " +
                  "这是 EnterMapCoord 写了一半就炸的现场。重试同一个坐标没用（AddVisitedMapCoord 会返回 false）；" +
                  "available 里是游戏自己兜底的可走节点（通常只有起点节点），先走它，连起点都进不去才需要重开"
                : $"坐标 ({col},{row}) 已经走过：RunState.AddVisitedMapCoord 会直接返回 false，" +
                  "游戏不会重新进这个房间（看起来成功后 room 不会变）";
            return start;
        }

        // 只允许「当前节点能走到的下一批节点」。不加这个判断 EnterMapCoord 会直接传送到地图上任意
        // 一个点，整层就直接跳过了。
        var current = s.CurrentMapPoint;
        var travelable = TravelablePoints(s, current);
        var allowed = travelable.Any(tp => tp.coord.col == col && tp.coord.row == row);
        if (!allowed)
        {
            start.Error = current != null
                ? $"({col},{row}) 不和当前节点 ({current.coord.col},{current.coord.row}) 相连；只能走 available 里的节点"
                : $"({col},{row}) 不是现在能走的节点：判不出当前节点（刚切幕，或上次 move 把不存在的坐标写进了 visited），" +
                  "只能走 available 里的节点 —— 游戏自己的兜底也是起点节点";
            return start;
        }

        if (_moving)
        {
            start.Error = "上一次移动还没结束，等一秒再走";
            return start;
        }

        start.Room = s.CurrentRoom.RoomType.ToString();
        start.RoomId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.CurrentRoom);
        start.Seq = ++_moveSeq;
        _moveError = null;
        _moveErrorIsNre = false;
        _moveErrorWhere = null;
        _moving = true;

        BridgeLog.Info($"move to ({col},{row}) seq={start.Seq} from room={start.Room}");
        start.Task = EnterRoom(rm, coord, start.Seq);
        return start;
    }

    /// <summary>
    /// 人还卡在一个「有内容、跳过会丢东西」的房间里吗（事件/营火/商店/宝箱/奖励）？
    /// 这是 <see cref="BeginMove"/> 拦 move 用的**同一份判据**，<c>/map/next</c> 也调它，
    /// 所以不会出现「next 说能走、move 被拒」。
    ///
    /// 两个例外不算卡住：
    ///   * 地图界面已经开着（事件/奖励处理完，游戏自己开了地图，但 CurrentRoom 还挂着旧房间对象）；
    ///   * 房间界面节点已经不在场景树里（同上，只是地图没开）。
    /// 战斗/地图房间不在拦截范围内（战斗另有 CombatManager.IsInProgress 判据）。
    /// 必须在主线程调用（会遍历场景树）。
    /// </summary>
    internal static bool RoomBlocksTravel(RunState s, out string roomType)
    {
        roomType = null;
        try
        {
            if (s?.CurrentRoom == null) return false;
            roomType = s.CurrentRoom.RoomType.ToString();
            if (roomType == "Map") return false;
            if (MapScreenOpen()) return false;
            if (RoomUiPresent()) return true;
            return roomType != "Combat" && roomType != "Monster"
                   && roomType != "Elite" && roomType != "Boss";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>地图界面是不是开着（NMapScreen.Instance.IsOpen）。</summary>
    private static bool MapScreenOpen()
    {
        try
        {
            var mapScreen = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            return mapScreen != null && Godot.GodotObject.IsInstanceValid(mapScreen) && mapScreen.IsOpen;
        }
        catch { return false; }
    }

    /// <summary>
    /// 场景树里还有没有可见的房间界面节点。
    /// CurrentRoom 里挂的旧房间对象不可靠：事件/奖励处理完，人已经回到地图，CurrentRoom 还是那个房间，
    /// 真正该看的是「那些房间的界面节点还在不在」。
    /// </summary>
    private static bool RoomUiPresent()
    {
        try
        {
            if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree) return false;
            var stack = new System.Collections.Generic.Stack<Godot.Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000)
            {
                var node = stack.Pop();
                if (node is Godot.Control control && control.IsInsideTree() && control.IsVisibleInTree())
                {
                    var full = node.GetType().FullName ?? "";
                    if (full.EndsWith(".NEventRoom") || full.EndsWith(".NRestSiteRoom")
                        || full.EndsWith(".NMerchantInventory") || full.EndsWith(".NTreasureRoom")
                        || full.EndsWith(".NRewardsScreen"))
                        return true;
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return false;
    }

    private static async Task EnterRoom(RunManager rm, MapCoord coord, int seq)
    {
        try
        {
            await rm.EnterMapCoord(coord);
            BridgeLog.Info($"entered room at ({coord.col},{coord.row})");
        }
        catch (Exception e)
        {
            if (seq == _moveSeq)
            {
                _moveError = e.GetType().Name + ": " + e.Message;
                _moveErrorIsNre = e is NullReferenceException;
                _moveErrorWhere = FirstFrame(e);
            }
            BridgeLog.Error("enter room failed", e);
        }
        finally
        {
            if (seq == _moveSeq)
            {
                _moving = false;
            }
        }
    }

    private static Dictionary<string, object> MoveSucceeded(object movingTo, MapProbe after, Stopwatch sw)
    {
        BridgeLog.Info($"move confirmed: room={after.Room} coord=({after.Col},{after.Row}) waited={(int)sw.ElapsedMilliseconds}ms");
        return Ok(new Dictionary<string, object>
        {
            ["movingTo"] = movingTo,
            ["room"] = after.Room,
            ["waitedMs"] = (int)sw.ElapsedMilliseconds,
        });
    }

    private static Dictionary<string, object> MoveFailed(
        object movingTo, List<object> available, Stopwatch sw, bool actTransition, string message, string cause = null)
    {
        var d = new Dictionary<string, object>
        {
            ["ok"] = false,
            ["error"] = message,
            ["movingTo"] = movingTo,
            ["available"] = available ?? new List<object>(),
            ["waitedMs"] = (int)sw.ElapsedMilliseconds,
        };
        if (actTransition) d["actTransition"] = true;
        if (!string.IsNullOrEmpty(cause)) d["cause"] = cause;
        return d;
    }

    private static MoveErrorInfo TakeMoveError(int seq)
    {
        if (seq != _moveSeq) return null;
        var text = _moveError;
        if (text == null) return null;
        return new MoveErrorInfo { Text = text, Where = _moveErrorWhere, IsNre = _moveErrorIsNre };
    }

    private static string CoordText(MapProbe p)
    {
        if (p?.Col is not { } col || p?.Row is not { } row) return "null";
        return $"({col},{row})";
    }

    /// <summary>异常栈里第一帧（够用来认出 EnterMapCoordInternal）。</summary>
    private static string FirstFrame(Exception e)
    {
        try
        {
            var trace = e.StackTrace;
            if (string.IsNullOrEmpty(trace)) return null;
            foreach (var line in trace.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                return t.Length > 180 ? t[..180] : t;
            }
        }
        catch { }
        return null;
    }

    private sealed class MoveStart
    {
        public string Error;
        public bool ActTransition;
        public List<object> Available = new();
        /// <summary>发起前的房间类型（"Monster" / "Map" / …）。</summary>
        public string Room;
        /// <summary>发起前的 CurrentRoom 实例哈希：换了房间 = 换了一个实例（同类型也算）。</summary>
        public int RoomId;
        public int Seq;
        public Task Task;
    }

    private sealed class MoveErrorInfo
    {
        public string Text;
        public string Where;
        public bool IsNre;
    }

    /// <summary>
    /// 一帧地图现场（全部在主线程读）。判断「真的进了房间」用的是 <see cref="RoomId"/>
    /// （CurrentRoom 的实例哈希），不是坐标：坐标在 AddVisitedMapCoord 那一刻就变了。
    /// </summary>
    private sealed class MapProbe
    {
        public bool InRun;
        public string Room;
        public int RoomId;
        public int? Col;
        public int? Row;
        public int ActFloor;
        public int TotalFloor;
        public bool MapNull;
        /// <summary>Map 有 StartingMapPoint = 新一幕的地图已经生成好了。</summary>
        public bool MapReady;
        public string CurrentPointType;
        public int? PointCol;
        public int? PointRow;

        /// <summary>
        /// Act 刚切换：actFloor=0、还没进新一幕的任何房间、totalFloor 已经走过 Boss 层。
        /// EnterAct 在进下一幕时只建一个 MapRoom，所以这个状态本身是正常的（玩家要走进起点节点）。
        /// </summary>
        public bool ActBoundary =>
            InRun && ActFloor == 0 && TotalFloor > 0 && (Room == null || Room == "Map");

        /// <summary>Act 边界 + 地图还没生成好：这才是真坏了（等一会儿还是这样就只能重开）。</summary>
        public bool ActTransitionPending => ActBoundary && !MapReady;

        /// <summary>坐标到了目标，**并且** CurrentRoom 换成了另一个实例（room 不是 Map / null）。</summary>
        public bool RoomReached(int col, int row, int baselineRoomId)
        {
            if (!InRun) return false;
            if (Col != col || Row != row) return false;
            if (Room == null || Room == "Map") return false;   // 还停在地图上，或者房间被清空了
            return RoomId != baselineRoomId;
        }
    }

    private static MapProbe ProbeMap(int playerIdx)
    {
        try
        {
            return BridgePump.OnMain(() =>
            {
                var probe = new MapProbe();
                try
                {
                    var s = RunManager.Instance?.DebugOnlyGetState();
                    if (s == null) return probe;
                    probe.InRun = true;
                    probe.Room = s.CurrentRoom?.RoomType.ToString();
                    probe.RoomId = s.CurrentRoom == null
                        ? 0
                        : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.CurrentRoom);
                    if (s.CurrentMapCoord is { } c)
                    {
                        probe.Col = c.col;
                        probe.Row = c.row;
                    }
                    probe.ActFloor = s.ActFloor;
                    probe.TotalFloor = s.TotalFloor;
                    var map = s.Map;
                    probe.MapNull = map == null;
                    // Grid 是 protected，读不到；StartingMapPoint 有值 + 至少有一行才算生成好了
                    // （NullActMap 的 StartingMapPoint 是 (0,0) 占位、Grid 是 0x0，不能算 ready）。
                    probe.MapReady = map != null && map.StartingMapPoint != null && RowCount(map) > 0;
                    var point = s.CurrentMapPoint;
                    if (point != null)
                    {
                        probe.CurrentPointType = point.PointType.ToString();
                        probe.PointCol = point.coord.col;
                        probe.PointRow = point.coord.row;
                    }
                }
                catch { }
                return probe;
            }, 5000).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            BridgeLog.Warn("move: 读地图现场失败（" + e.GetType().Name + "）：" + e.Message);
            return null;
        }
    }

    /// <summary>
    /// 当前能走的节点。**照抄游戏自己的 <c>NMapScreen.RecalculateTravelability</c>**（IL 核过）：
    ///   1. 一个坐标都没走过（新一幕刚开始）→ 只有**起点节点**可走（`ActMap.StartingMapPoint`，
    ///      coord = (列数/2, 0)）；这是进新一幕的第一步，不是第一行的普通节点。
    ///   2. 最后走的是第二 Boss → 第二 Boss。
    ///   3. 最后走的那一行 == 行数-1 → Boss 节点。
    ///   4. 否则 → <see cref="MapTravel.GetTravelablePointsFrom"/>（没有 free-travel 时就是 Children）。
    ///   5. 最后那个坐标不在本幕地图里（地图重生成 / visited 被写脏）→ 游戏自己也是退回起点。
    /// </summary>
    internal static List<MapPoint> TravelablePoints(RunState s, MapPoint current)
    {
        var result = new List<MapPoint>();
        if (s == null) return result;

        var map = s.Map;
        if (map == null) return result;

        // 半初始化对局（开局后地图界面没建出来、CurrentRoom 为空、画面黑屏）：
        // 这时给「可走节点」是假的——move 一定被 CurrentRoom 护栏拒。宁可不给。
        if (s.CurrentRoom == null) return result;

        var visited = s.VisitedMapCoords;
        if (visited == null || visited.Count == 0)
        {
            if (map.StartingMapPoint != null && RowCount(map) > 0) result.Add(map.StartingMapPoint);
            return result;
        }

        var last = visited[visited.Count - 1];

        if (map.SecondBossMapPoint != null && SameCoord(map.SecondBossMapPoint.coord, last))
        {
            result.Add(map.SecondBossMapPoint);
            return result;
        }

        var rows = RowCount(map);
        if (rows > 0 && last.row == rows - 1)
        {
            if (map.BossMapPoint != null) result.Add(map.BossMapPoint);
            return result;
        }

        var point = current ?? map.GetPoint(last);
        if (point == null)
        {
            // 和游戏自己一样退回起点（最后那个坐标不在本幕地图里 = 地图重生成过 / visited 被写脏）。
            if (map.StartingMapPoint != null && RowCount(map) > 0) result.Add(map.StartingMapPoint);
            return result;
        }

        try
        {
            var travelable = MapTravel.GetTravelablePointsFrom(s, point);
            if (travelable != null)
            {
                foreach (var p in travelable)
                    if (p != null) result.Add(p);
                return result;
            }
        }
        catch (Exception e) { BridgeLog.Warn("MapTravel 失败，退回 Children：" + e.Message); }

        if (point.Children != null)
            foreach (var p in point.Children)
                if (p != null) result.Add(p);
        return result;
    }

    /// <summary>ActMap.GetRowCount()：正常返回 Grid 的行数；读不到就 -1（调用方会跳过行相关判断）。</summary>
    private static int RowCount(ActMap map)
    {
        try { return map.GetRowCount(); }
        catch { return -1; }
    }

    private static bool SameCoord(MapCoord a, MapCoord b) => a.col == b.col && a.row == b.row;

    /// <summary>available 的 JSON 形状：[{"col":..,"row":..,"type":"Monster"}, …]</summary>
    private static List<object> AvailableJson(RunState s)
    {
        var list = new List<object>();
        try
        {
            foreach (var point in TravelablePoints(s, s?.CurrentMapPoint))
            {
                list.Add(new Dictionary<string, object>
                {
                    ["col"] = point.coord.col,
                    ["row"] = point.coord.row,
                    ["type"] = point.PointType.ToString(),
                });
            }
        }
        catch (Exception e) { BridgeLog.Warn("列出可走节点失败：" + e.Message); }
        return list;
    }

    private static bool TryInt(JsonElement req, string name, out int value)
    {
        value = 0;
        if (req.ValueKind != JsonValueKind.Object) return false;
        if (!req.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetInt32(out value);
    }

    private static Dictionary<string, object> UsePotion(JsonElement req)
    {
        // potion（推荐）= 稳定 key「标题#序号」；uid / index 兼容保留。
        var potion = req.TryGetProperty("potion", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        int? uid = req.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : null;
        int? index = req.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        int? target = req.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        return Potions.UseNow(potion, uid, index, target);
    }

    private static Dictionary<string, object> DiscardPotion(JsonElement req)
    {
        var potion = req.TryGetProperty("potion", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        int? uid = req.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : null;
        int? index = req.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        return Potions.DiscardNow(potion, uid, index);
    }

    private static Dictionary<string, object> AutoSlay(JsonElement req)
    {
        var on = !req.TryGetProperty("on", out var onEl) || onEl.GetBoolean();
        if (on)
        {
            Slayer.Start(null, null);
            return Ok(new Dictionary<string, object> { ["autoSlay"] = AutoSlayer.IsActive });
        }

        Slayer.Stop();
        return Ok(new Dictionary<string, object> { ["autoSlay"] = AutoSlayer.IsActive });
    }

    // --------------------------------------------------------------- helpers

    /// <summary>Local player's current energy, or -1 outside combat. Used by the batch runner.</summary>
    public static int LocalEnergy()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        var player = state?.Players is { Count: > 0 } players ? players[0] : null;
        return player?.PlayerCombatState?.Energy ?? -1;
    }

    /// <summary>Uids of the local hand. A play succeeded when its uid is no longer here.</summary>
    public static List<int> LocalHandUids()
    {
        var uids = new List<int>();
        var state = RunManager.Instance?.DebugOnlyGetState();
        var hand = state?.Players is { Count: > 0 } players ? players[0].PlayerCombatState?.Hand?.Cards : null;
        if (hand == null) return uids;
        foreach (var card in hand)
            uids.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(card));
        return uids;
    }

    /// <summary>True while a card/action animation is still resolving.</summary>
    public static bool ActionRunning()
    {
        try { return RunManager.Instance?.ActionExecutor?.IsRunning ?? false; }
        catch { return false; }
    }

    /// <summary>Current player turn number, or -1 outside combat.</summary>
    public static int LocalTurn()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.Players is { Count: > 0 } players ? players[0].PlayerCombatState?.TurnNumber ?? -1 : -1;
        }
        catch { return -1; }
    }

    /// <summary>Sum of enemy current HP; used to verify that a card's damage actually landed.</summary>
    public static int EnemyHpTotal()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state?.CurrentRoom is not CombatRoom room || room.CombatState is not { } combat) return -1;
            var total = 0;
            foreach (var enemy in combat.Enemies) total += enemy.CurrentHp;
            return total;
        }
        catch { return -1; }
    }

    /// <summary>
    /// A coarse fingerprint of the board (enemy hp/block, player hp/block, power counts).
    /// A card that left the hand without changing any of it did not actually resolve.
    /// </summary>
    public static int BattleSignature()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state?.CurrentRoom is not CombatRoom room || room.CombatState is not { } combat) return -1;

            var signature = 0;
            foreach (var enemy in combat.Enemies)
            {
                signature += enemy.CurrentHp * 31 + enemy.Block * 7;
                signature += (enemy.Powers?.Count ?? 0) * 101;
            }

            if (state.Players.Count > 0)
            {
                var player = state.Players[0];
                signature += (player.Creature?.CurrentHp ?? 0) * 13;
                signature += (player.Creature?.Block ?? 0) * 17;
                signature += (player.Creature?.Powers?.Count ?? 0) * 211;
            }

            return signature;
        }
        catch { return -1; }
    }

    private static Player PlayerAt(RunState s, JsonElement req)
    {
        if (s?.Players == null || s.Players.Count == 0) return null;
        var idx = 0;
        if (req.TryGetProperty("player", out var pEl) && pEl.ValueKind == JsonValueKind.Number)
            idx = pEl.GetInt32();
        return idx >= 0 && idx < s.Players.Count ? s.Players[idx] : null;
    }

    private static Player PlayerAt(RunState s, int index)
    {
        if (s?.Players == null || s.Players.Count == 0) return null;
        return index >= 0 && index < s.Players.Count ? s.Players[index] : null;
    }

    // ---------------------------------------------------------- handComplete
    //
    // 发牌是逐张动画：回合刚开始时读 /state 会看到 1~3 张手牌，agent 照着半成品手牌决策就会漏牌。
    // 模型层（pcs.Hand.Cards）本身没被截断，问题只是「动画还没播完」。
    //
    // 判据（低成本，不误报成 false 的方向）：每帧在主线程采一次手牌张数，只要张数（或回合号）变了
    // 就把时间戳刷新；/state 里 handComplete = 距离最后一次变化是否已经 ≥250ms。
    // 拿不到采样（mod 刚加载/不在战斗）时一律 true —— 宁可不说也不乱说。

    private const int HandSettleMs = 250;

    private sealed class HandWatch
    {
        public bool Seen;
        public int Count = -1;
        public int Turn = -1;
        /// <summary>PlayerCombatState 实例哈希：换了战斗就重新计时。</summary>
        public int CombatId;
        public long ChangedMs;
    }

    private static readonly Dictionary<int, HandWatch> HandWatches = new();

    /// <summary>每帧由 <see cref="BridgePump"/> 在主线程调一次（很轻：只读一个 Count）。</summary>
    internal static void TickHandWatch()
    {
        try
        {
            var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
            if (players == null) return;
            var now = System.Environment.TickCount64;
            for (var i = 0; i < players.Count; i++)
            {
                var pcs = players[i]?.PlayerCombatState;
                if (pcs == null) continue;

                if (!HandWatches.TryGetValue(i, out var watch))
                {
                    watch = new HandWatch();
                    HandWatches[i] = watch;
                }

                var count = pcs.Hand?.Cards?.Count ?? -1;
                var turn = pcs.TurnNumber;
                var combatId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pcs);
                if (!watch.Seen || watch.Count != count || watch.Turn != turn || watch.CombatId != combatId)
                {
                    watch.Seen = true;
                    watch.Count = count;
                    watch.Turn = turn;
                    watch.CombatId = combatId;
                    watch.ChangedMs = now;
                }
            }
        }
        catch { }
    }

    /// <summary>手牌还可能在变吗？只有「最近 250ms 内张数/回合号变过」才报 false。</summary>
    private static bool HandComplete(int playerIndex)
    {
        if (!HandWatches.TryGetValue(playerIndex, out var watch) || !watch.Seen) return true;
        return System.Environment.TickCount64 - watch.ChangedMs >= HandSettleMs;
    }

    private static Dictionary<string, object> Ok(Dictionary<string, object> payload = null)
    {
        var d = new Dictionary<string, object> { ["ok"] = true };
        if (payload != null) foreach (var kv in payload) d[kv.Key] = kv.Value;
        return d;
    }

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}

