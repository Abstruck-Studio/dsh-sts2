using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// Runs a whole turn in one request (plays, potions, end turn), while pacing the game so each
/// card is visibly played one after another. Stops early when an action does not take effect,
/// which is how an unexpected choice screen (e.g. Headbutt) hands control back to the agent.
/// </summary>
public static class Batch
{
    public static Dictionary<string, object> Run(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return Fail("missing 'actions' array");

        var steps = new List<object>();
        var stopped = (string)null;

        // stopped 的结构化补充：三种「没做成」长得太像（牌打出去但棋盘没变 / 弹了选牌 / uid 失效），
        // 光靠文案 agent 只能猜。reason 固定是 no_effect / choice / uid_gone / energy / other。
        var stoppedReason = (string)null;
        var stoppedHint = (string)null;
        long? stoppedPendingId = null;
        Dictionary<string, object> stoppedExtra = null;

        // 这一批动作里敌人被打光了：end_turn 发现战斗已经结束，这不是失败。
        var combatOver = false;
        var combatOverNote = (string)null;

        foreach (var action in actions.EnumerateArray())
        {
            var kind = action.TryGetProperty("kind", out var k) ? k.GetString() : "?";
            int uid = -1;
            if (kind == "play_card" && action.TryGetProperty("uid", out var uidEl) && uidEl.ValueKind == JsonValueKind.Number)
                uid = uidEl.GetInt32();

            var signatureBefore = kind == "play_card" ? Signature() : 0;
            var hpBefore = kind == "play_card" ? EnemyHp() : 0;
            int? expectedDamage = action.TryGetProperty("expect", out var exp) && exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt32()
                : null;

            // ---- end_turn 的「先自己看一眼」----
            // 最后一击把敌人清空之后，同一批动作里的 end_turn 本来就不需要执行：战斗已经结束，
            // 这时候再调 PlayerCmd.EndTurn 只会打在一个已经结束的战斗上，然后白等 10 秒超时，
            // 最后被当成失败 —— agent 会以为自己的动作没生效。
            // 注意顺序：「游戏在等选牌」优先，那种状态下战斗还没结束，只是轮不到 end_turn。
            if (kind == "end_turn")
            {
                var pre = ProbeCombat();
                if (pre != null && pre.PendingCardSelect)
                    return CardSelectPending(
                        "游戏在等你选牌（所以回合没推进）：GET /request 拿选牌请求，再 POST /respond 交 key");

                if (pre != null && CombatOverNow(pre))
                {
                    combatOver = true;
                    combatOverNote = "这一批动作里敌人已经被清空，回合不需要再结束";
                    BridgeLog.Info("batch: combat already over before end_turn, skip it");
                    steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = true, ["combatOver"] = true });
                    break;
                }

                // ---- 空跳回合的守卫：还有能量、手里又有能打的牌时，不直接结束 ----
                // 实测里偶尔会出现「还有 2~3 点能量就结束回合」，白丢一整个回合的输出。
                // 这里不替 agent 做决定：把现场行情报回去，让它显式确认（action.confirm = true）。
                var confirmEnd = action.TryGetProperty("confirm", out var confirmEl)
                                 && confirmEl.ValueKind == JsonValueKind.True;
                if (!confirmEnd)
                {
                    var energyLeft = GameApi.LocalEnergy();
                    if (energyLeft > 0)
                    {
                        var playableTitles = new List<string>();
                        var playableCount = 0;
                        try
                        {
                            var handUids = GameApi.LocalHandUids();
                            foreach (var handUid in handUids)
                            {
                                var info = GameApi.PlayabilityOfHandCard(handUid);
                                if (info != null && info.Playable)
                                {
                                    playableCount++;
                                    if (playableTitles.Count < 6) playableTitles.Add(info.Title ?? ("uid " + handUid));
                                }
                            }
                        }
                        catch { }

                        if (playableCount > 0)
                        {
                            var list = playableTitles.Count > 0 ? string.Join("、", playableTitles) : "（见 /state 的 hand）";
                            return Fail(
                                $"还有 {energyLeft} 点能量、手牌里还有 {playableCount} 张能打出（{list}）。" +
                                "如果确定要跳过这个回合，请把 end_turn 加上 confirm:true 再发一次；" +
                                "否则先把这些牌打出去。",
                                "energy_left",
                                "回合没结束：还有可用能量。要么出完牌，要么显式确认跳过（end_turn + confirm:true）");
                        }
                    }
                }
            }

            // ---- play_card 的「先自己看一眼」----
            // 有些牌除了费用还有别的条件（本回合已经打出过技能牌之类），游戏会说打不出去，
            // 排进队列只会被静默丢掉、然后等 6 秒超时。这里先用游戏自己的可用性判断拦一次，
            // 失败体带上 unplayableReason（agent 才知道换哪张）。
            if (kind == "play_card" && uid >= 0)
            {
                GameApi.CardPlayability pre = null;
                try { pre = BridgePump.OnMain(() => GameApi.PlayabilityOfHandCard(uid), 5000).GetAwaiter().GetResult(); }
                catch { pre = null; }

                // pre == null：uid 已经不在手里了 → 交给原来的「没生效」判定去报 uid 失效。
                if (pre != null && !pre.Playable)
                {
                    stopped = "play_card rejected: 这张牌现在打不出去 —— " +
                              pre.Reason + "（" + (pre.Title ?? "?") + "）";
                    stoppedReason = "unplayable";
                    stoppedHint = string.Equals(pre.Code, "EnergyCostTooHigh", StringComparison.Ordinal)
                        ? "能量不够付这张牌：换一张更便宜的，或者 end_turn 把回合交出去"
                        : "这张牌的打出条件还没满足（不是费用问题）：换一张能打的牌，或者先做它需要的动作；" +
                          "unplayableReasonCode 是游戏自己的原因名，unplayableSource 是拦下它的东西（有的话）";
                    var step = new Dictionary<string, object>
                    {
                        ["step"] = kind,
                        ["ok"] = false,
                        ["error"] = stopped,
                        ["playable"] = false,
                        ["unplayableReason"] = pre.Reason,
                        ["unplayableReasonCode"] = pre.Code,
                    };
                    if (!string.IsNullOrEmpty(pre.Source)) step["unplayableSource"] = pre.Source;
                    steps.Add(step);
                    stoppedExtra = new Dictionary<string, object>
                    {
                        ["playable"] = false,
                        ["unplayableReason"] = pre.Reason,
                        ["unplayableReasonCode"] = pre.Code,
                    };
                    if (!string.IsNullOrEmpty(pre.Source)) stoppedExtra["unplayableSource"] = pre.Source;
                    break;
                }
            }

            // move 是「发起 + 轮询确认」两段式：它自己会 await 主线程读状态，
            // 不能塞进 BridgePump.OnMain（主线程上等主线程任务会死锁）。
            if (kind == "move")
            {
                var moved = GameApi.MoveAndConfirm(action).GetAwaiter().GetResult();
                var movedOk = moved.TryGetValue("ok", out var mv) && mv is true;
                if (!movedOk)
                {
                    var moveError = moved.TryGetValue("error", out var mErr) ? mErr?.ToString() : "unknown";
                    stopped = "move rejected: " + moveError;
                    stoppedReason = "other";
                    stoppedHint = "移动没有进房间：看 available 里现在能走的节点，别重试同一个坐标";
                    stoppedExtra = new Dictionary<string, object>();
                    if (moved.TryGetValue("available", out var avail)) stoppedExtra["available"] = avail;
                    if (moved.TryGetValue("actTransition", out var at)) stoppedExtra["actTransition"] = at;
                    steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = false, ["error"] = stopped });
                    break;
                }

                steps.Add(new Dictionary<string, object>
                {
                    ["step"] = kind,
                    ["ok"] = true,
                    ["room"] = moved.TryGetValue("room", out var roomNow) ? roomNow : null,
                    ["waitedMs"] = moved.TryGetValue("waitedMs", out var moveWait) ? moveWait : null,
                });
                continue;
            }

            var outcome = BridgePump.OnMain(() => GameApi.Act(action), 15000).GetAwaiter().GetResult();
            var ok = outcome.TryGetValue("ok", out var flag) && flag is true;
            if (!ok)
            {
                var err = outcome.TryGetValue("error", out var errObj) ? errObj?.ToString() : null;
                stopped = kind + " rejected: " + (err ?? "unknown");
                var (reason, hint) = ClassifyRejected(kind, err);
                stoppedReason = reason;
                stoppedHint = hint;
                steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = false, ["error"] = stopped });
                break;
            }

            if (kind == "end_turn")
            {
                var turnBefore = Turn();
                var switched = false;
                CombatProbe probe = null;
                for (var i = 0; i < 40; i++)
                {
                    Task.Delay(250).Wait();
                    if (Turn() > turnBefore) { switched = true; break; }

                    // 每秒探一次：战斗结束 / 在等选牌都不该在这里干等到 10 秒超时。
                    if (i % 4 != 3) continue;
                    probe = ProbeCombat();
                    if (probe != null && (probe.PendingCardSelect || CombatOverNow(probe))) break;
                }

                if (switched)
                {
                    steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = true });
                    continue;
                }

                probe ??= ProbeCombat();

                // ① 先认「等选牌」：回合没推进是因为游戏在等玩家选牌，不是失败、也不是战斗结束。
                if (probe != null && probe.PendingCardSelect)
                    return CardSelectPending(
                        "游戏在等你选牌（所以回合没推进）：GET /request 拿选牌请求，再 POST /respond 交 key");

                // ② 战斗已经结束：回合不需要再结束 → 成功，并明确标出 combatOver。
                if (probe != null && CombatOverNow(probe))
                {
                    combatOver = true;
                    combatOverNote = "这一批动作里敌人已经被清空，回合不需要再结束";
                    BridgeLog.Info("batch: end_turn skipped, combat is over");
                    steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = true, ["combatOver"] = true });
                    break;
                }

                // ③ 既不在等选牌、战斗也还没结束：这才是真的失败（能量/选择/队列问题都在前面拦过了）。
                if (probe != null)
                    BridgeLog.Info(
                        $"batch: end_turn 没推进，战斗看起来还在继续 room={probe.Room} " +
                        $"inProgress={probe.InProgress} starting={probe.Starting} " +
                        $"overOrEnding={probe.OverOrEnding} aliveEnemies={probe.AliveEnemies} " +
                        $"enemies={probe.Enemies} hittable={probe.Hittable}");

                steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = false });

                stopped = "end_turn did not advance the turn";
                stoppedReason = "other";
                stoppedHint = "回合没有推进，但不在等选牌、战斗也还在：先 GET /state 看 combatState，再 GET /request 确认有没有别的选择";

                // 死掉了也是「回合没推进」，但那是完全不同的结局：明确告诉 agent 别再来一次。
                try
                {
                    var stNow = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
                    var meNow = stNow?.Players != null && stNow.Players.Count > 0 ? stNow.Players[0] : null;
                    var hpNow = meNow?.Creature?.CurrentHp ?? -1;
                    if (stNow?.IsGameOver == true || hpNow == 0)
                    {
                        stopped = "你这回合被打死了（HP 0，本局结束）";
                        stoppedReason = "dead";
                        stoppedHint = "别再出牌或结束回合了：用 GET /request 走 kind:\"gameover\" 收尾（继续 → 主菜单）";
                    }
                }
                catch { }
                break;
            }

            // A card play succeeded when its uid leaves the hand. Energy is not usable as the
            // success signal: potions and teammate cards can add energy mid-turn.
            var applied = kind != "play_card";
            var pending = false;
            var pendingProbe = (ChoiceProbe)null;
            if (kind == "play_card")
            {
                for (var i = 0; i < 24; i++)
                {
                    Task.Delay(250).Wait();
                    if (!HandUids().Contains(uid)) { applied = true; break; }

                    // Cards like True Grit+ open a card-selection overlay mid-play; the queue is
                    // paused until a card is picked. Hand control back instead of failing.
                    // selector 通道不弹界面，所以还要问 RequestApi 有没有挂着选牌请求。
                    if (i >= 4 && (pendingProbe = ProbeChoice()).Has)
                    {
                        pending = true;
                        break;
                    }
                }
            }
            else
            {
                Task.Delay(700).Wait();
            }

            if (pending)
            {
                return CardSelectPending(
                    "游戏在等你选牌：GET /request 拿选牌请求，再 POST /respond 交 key",
                    pendingProbe?.Id);
            }

            steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = applied });
            if (!applied)
            {
                // ① 先认「在等选牌」：这条以前只会报 queue busy，实际上游戏在等一张牌的答案。
                var choice = ProbeChoice();
                if (choice.Has)
                {
                    stopped = kind + " did not take effect (queue busy or a choice screen opened)";
                    stoppedReason = "choice";
                    stoppedPendingId = choice.Id;
                    stoppedHint = choice.Id != null
                        ? "游戏在等你选牌：GET /request，用 pendingId 那条请求的 id 交 picks（别重打这张牌）"
                        : "游戏在等你选牌，请求可能还在路上：先 GET /request 拿 pending，再交 picks";
                    break;
                }

                // ② 能量耗尽是最常见的「没生效」，但它不是界面卡住；分开报，免得 agent 去查界面。
                var leftEnergy = -1;
                try
                {
                    var st = RunManager.Instance?.DebugOnlyGetState();
                    var pl = st?.Players != null && st.Players.Count > 0 ? st.Players[0] : null;
                    if (pl?.PlayerCombatState != null) leftEnergy = pl.PlayerCombatState.Energy;
                }
                catch { }

                if (leftEnergy == 0)
                {
                    stopped = kind + " did not take effect: 能量不足（当前 0 能量，付不起这张牌）";
                    stoppedReason = "energy";
                    stoppedHint = "这张牌付不起：换一张更便宜的，或者 end_turn 把回合交出去";
                }
                else
                {
                    stopped = kind + " did not take effect (queue busy or a choice screen opened)";
                    stoppedReason = "other";
                    stoppedHint = "动作没被受理：先 GET /state 看 combatState 是不是 PlayPhase、手牌 uid 还在不在，必要时 GET /request";
                }
                break;
            }

            // Let the animation finish, then hold a beat so the player sees one card at a time.
            var idle = WaitIdle(4000);
            Task.Delay(700).Wait();

            // 队列一直不空闲 = 游戏在等玩家做选择（丢弃/消耗之类，界面就长在手牌上）。
            // 这时把候选手牌直接交回去，否则后面每个动作都会被静默丢弃。
            if (kind == "play_card" && applied && !idle)
            {
                var handNow = BridgePump.OnMain(ScreenApi.Inspect, 3000).GetAwaiter().GetResult();
                var choiceNow = ProbeChoice();
                BridgeLog.Info("batch: queue still busy — handing back a pending choice");
                var pendingResult = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["pending"] = "choice",
                    ["reason"] = "choice",
                    ["note"] = "游戏在等你从手牌里选一张（丢弃/消耗等效果）：GET /request 拿选牌请求，再 POST /respond 交 key",
                    ["screen"] = handNow,
                    ["turn"] = null,
                };
                if (choiceNow.Id != null) pendingResult["pendingId"] = choiceNow.Id.Value;
                return pendingResult;
            }

            if (kind == "play_card" && applied && Signature() == signatureBefore)
            {
                stopped = kind + " left the hand but changed nothing on the board";
                stoppedReason = "no_effect";
                // 典型是 0 费抽牌 / 充能 / 生成牌：牌确实打出去了，棋盘（血量/格挡/能力）当然不变。
                stoppedHint = "牌已经离开了手牌（这一张打出去了），只是 hp/block/能力数没变 —— " +
                              "如果是抽牌/充能/生成牌属正常；看 /state 的 energy / drawPile / hand 判断结果，别重打同一张";
                steps.Add(new Dictionary<string, object> { ["step"] = kind, ["ok"] = false, ["error"] = stopped });
                break;
            }

            if (kind == "play_card" && expectedDamage is { } expected)
            {
                var dealt = hpBefore - EnemyHp();
                if (dealt != expected)
                {
                    stopped = $"{kind} dealt {dealt} damage but {expected} was expected";
                    stoppedReason = "other";
                    stoppedHint = "伤害和预期不符（可能被格挡/减伤，或者敌人已经换了）：重新读 /state 核对敌我血量再决定";
                    steps.Add(new Dictionary<string, object>
                    {
                        ["step"] = kind, ["ok"] = false, ["error"] = stopped, ["dealt"] = dealt, ["expected"] = expected,
                    });
                    break;
                }
            }
        }

        // Let the turn transition settle before snapshotting, otherwise the state still shows
        // the just-ended turn (leftover energy, stale hand).
        WaitIdle(4000);
        Task.Delay(1500).Wait();

        var state = (Dictionary<string, object>)null;
        try { state = BridgePump.OnMain(GameApi.State, 5000).GetAwaiter().GetResult(); }
        catch { }

        var result = new Dictionary<string, object>
        {
            ["ok"] = stopped == null,
            ["stopped"] = stopped,
            ["turn"] = TurnSummary(state),
        };

        // 停下来的结构化原因（只有失败才有；ok 的情况不会带这些字段）。
        // stopped 文案保持原样做兼容，reason/hint 是给 agent 判断用的。
        if (stopped != null)
        {
            result["reason"] = stoppedReason ?? "other";
            if (!string.IsNullOrEmpty(stoppedHint)) result["hint"] = stoppedHint;
            if (stoppedPendingId != null) result["pendingId"] = stoppedPendingId.Value;
            if (stoppedExtra != null)
                foreach (var kv in stoppedExtra) result[kv.Key] = kv.Value;
        }

        // 战斗已经结束时的 end_turn 不是失败：ok 仍是 true，只多两个字段说清情况。
        if (combatOver)
        {
            result["combatOver"] = true;
            result["note"] = combatOverNote ?? "这一批动作里敌人已经被清空，回合不需要再结束";
        }

        return result;
    }

    /// <summary>Only what the agent needs to decide the next turn, to keep responses tiny.</summary>
    private static Dictionary<string, object> TurnSummary(Dictionary<string, object> state)
    {
        if (state == null) return null;

        var summary = new Dictionary<string, object>();
        try
        {
            summary["room"] = state.TryGetValue("room", out var room) ? room : null;
            summary["over"] = state.TryGetValue("gameOver", out var over) ? over : false;

            if (!state.TryGetValue("players", out var playersObj) || playersObj is not List<object> players || players.Count == 0)
                return summary;

            if (players[0] is not Dictionary<string, object> player) return summary;
            summary["hp"] = player.TryGetValue("hp", out var hp) ? hp : 0;
            summary["block"] = player.TryGetValue("block", out var block) ? block : 0;
            summary["energy"] = player.TryGetValue("energy", out var energy) ? energy : 0;
            summary["powers"] = player.TryGetValue("powers", out var powers) ? powers : null;
            summary["hand"] = player.TryGetValue("hand", out var hand) ? hand : null;
            // 发牌动画没播完时 hand 可能只有 1~3 张；顺手把 /state 的判据带出来。
            summary["handComplete"] = player.TryGetValue("handComplete", out var handComplete) ? handComplete : true;

            if (state.TryGetValue("combat", out var combatObj) && combatObj is Dictionary<string, object> combat)
            {
                summary["side"] = combat.TryGetValue("side", out var side) ? side : null;
                summary["enemies"] = combat.TryGetValue("hittable", out var enemies) ? enemies : null;
            }
        }
        catch { }

        return summary;
    }

    private static List<int> HandUids()
    {
        try { return BridgePump.OnMain(GameApi.LocalHandUids, 5000).GetAwaiter().GetResult(); }
        catch { return new List<int>(); }
    }

    private static int EnemyHp()
    {
        try { return BridgePump.OnMain(GameApi.EnemyHpTotal, 5000).GetAwaiter().GetResult(); }
        catch { return -1; }
    }

    private static int Turn()
    {
        try { return BridgePump.OnMain(GameApi.LocalTurn, 5000).GetAwaiter().GetResult(); }
        catch { return -1; }
    }

    private static int Signature()
    {
        try { return BridgePump.OnMain(GameApi.BattleSignature, 5000).GetAwaiter().GetResult(); }
        catch { return -1; }
    }

    // ---------------------------------------------------------- 战斗结束的判定

    /// <summary>
    /// 一帧「战斗还在不在打」的现场。全部在主线程读（由 <see cref="ProbeCombat"/> 调度）。
    /// </summary>
    private sealed class CombatProbe
    {
        /// <summary>当前房间类型（<c>RoomType.ToString()</c>）；战斗房是 "Monster"。</summary>
        public string Room;

        /// <summary>CurrentRoom 是 CombatRoom（而且带 CombatState）。</summary>
        public bool InCombatRoom;

        /// <summary><c>CombatManager.IsInProgress</c>：战斗是否在进行。</summary>
        public bool InProgress;

        /// <summary><c>CombatManager.IsStarting</c>：刚进战斗、还在启动（这时候不算「结束」）。</summary>
        public bool Starting;

        /// <summary><c>CombatManager.IsOverOrEnding</c>（= IsEnding || !IsInProgress）。</summary>
        public bool OverOrEnding;

        /// <summary><c>CombatState.Enemies.Count</c>：敌人列表长度（尸体也可能还在里面）。</summary>
        public int Enemies;

        /// <summary>敌人列表里还活着（<c>Creature.IsAlive</c>）的数量。0 = 没有活敌人了。</summary>
        public int AliveEnemies;

        /// <summary><c>CombatState.HittableEnemies.Count</c>：还能打的目标数（只用于日志）。</summary>
        public int Hittable;

        /// <summary>本地玩家当前 HP（读不到就是 -1）。0 = 自己已经倒下。</summary>
        public int LocalHp = -1;

        /// <summary>游戏正等玩家选牌：selector 通道挂着请求，或者界面上有选牌浮层。</summary>
        public bool PendingCardSelect;
    }

    /// <summary>
    /// 读一帧战斗现场。任何一项读不到都只当成「不知道」，不影响其它项；整体失败返回 null。
    /// </summary>
    private static CombatProbe ProbeCombat()
    {
        try
        {
            return BridgePump.OnMain(() =>
            {
                var probe = new CombatProbe();

                try
                {
                    var combat = CombatManager.Instance;
                    if (combat != null)
                    {
                        probe.InProgress = combat.IsInProgress;
                        probe.Starting = combat.IsStarting;
                        probe.OverOrEnding = combat.IsOverOrEnding;
                    }
                }
                catch { }

                try
                {
                    var state = RunManager.Instance?.DebugOnlyGetState();
                    probe.Room = state?.CurrentRoom?.RoomType.ToString();
                    try
                    {
                        var players = state?.Players;
                        if (players != null && players.Count > 0)
                            probe.LocalHp = players[0]?.Creature?.CurrentHp ?? -1;
                    }
                    catch { }

                    if (state?.CurrentRoom is CombatRoom room && room.CombatState is { } combatState)
                    {
                        probe.InCombatRoom = true;
                        var enemies = combatState.Enemies;
                        probe.Enemies = enemies?.Count ?? 0;
                        if (enemies != null)
                        {
                            foreach (var enemy in enemies)
                            {
                                try { if (enemy != null && enemy.IsAlive) probe.AliveEnemies++; }
                                catch { }
                            }
                        }
                        probe.Hittable = combatState.HittableEnemies?.Count ?? 0;
                    }
                }
                catch { }

                try
                {
                    // selector 通道不弹界面，只能问 mod 自己有没有挂着待答的选牌请求。
                    probe.PendingCardSelect =
                        RequestApi.HasPendingCardRequest() || ScreenApi.HasCardSelectScreen();
                }
                catch { }

                return probe;
            }, 5000).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            BridgeLog.Warn("batch: 读战斗现场失败（" + e.GetType().Name + "）：" + e.Message);
            return null;
        }
    }

    /// <summary>
    /// 战斗已经结束（不是失败）。判定用的全是游戏自己的真值（下面这些语义是从 sts2.dll 的 IL 里
    /// 核对过的，不是猜的）：
    ///
    /// * <c>CombatManager.IsEnding</c> 是**算出来的**，不是标志位：
    ///   <c>IsInProgress &amp;&amp; (有待处理的失败 || !Enemies.Any(c =&gt; c.IsAlive &amp;&amp; c.IsPrimaryEnemy)
    ///   &amp;&amp; !Hook.ShouldStopCombatFromEnding(state))</c>。最后一个**主**敌人一死它就立刻变 true，
    ///   不用等胜利动画/结算。
    /// * <c>CombatManager.IsOverOrEnding</c> = <c>IsEnding || !IsInProgress</c>：赢、输、已经不在战斗里都算。
    ///
    /// 为什么**不**拿「敌人列表为空 / HittableEnemies 为空」当判据（这三条都核实过）：
    /// * <c>EndCombatInternal</c> 全程没有 RemoveCreature，尸体和**非主敌人**（召唤物）会留在
    ///   <c>CombatState.Enemies</c> 里 —— 所以「敌人列表为空」既不必要也不充分；
    /// * <c>HittableEnemies</c> = 活着的 &amp;&amp; <c>Hook.ShouldAllowHitting</c>，会受「暂时不可选中」影响；
    /// * <c>RoomType</c> 不能用：「战斗结束后 CurrentRoom 仍是战斗房间对象」（GameApi 里也记着）；
    /// * <c>turn.over</c> 不能用：那是 <c>RunState.IsGameOver</c>（整局结束），赢一场战斗它还是 false。
    ///
    /// 反过来，还要排除两种「IsOverOrEnding 为 true 但不是战斗结束」的情况：
    /// 不在战斗房里（end_turn 走错地方，按真失败报）、战斗正在启动（<c>IsStarting</c>，此时
    /// <c>IsInProgress</c> 还是 false，会被 IsOverOrEnding 误算成「结束」）、以及自己已经倒下
    /// （<c>LocalHp == 0</c>，那是输，不是「敌人被清空」）。
    /// </summary>
    private static bool CombatOverNow(CombatProbe p)
    {
        if (p == null) return false;
        if (!p.InCombatRoom) return false;   // 不在战斗房：end_turn 本来就走错地方了
        if (p.Starting) return false;        // 刚进战斗房、还在启动（这期间 IsInProgress 是 false）
        if (p.LocalHp == 0) return false;    // 自己倒下了 → 这是输，不是「敌人被清空」
        return p.OverOrEnding;
    }

    /// <summary>
    /// 「游戏在等玩家选牌」的返回体。字段和 play_card 那条完全一致
    /// （ok / pending / note / screen / turn），只是 note 说清是哪一步撞上的；
    /// 另外多带 reason=choice 和（拿得到时）pendingId，让 agent 不用猜是哪条 /request。
    /// </summary>
    private static Dictionary<string, object> CardSelectPending(string note, long? pendingId = null)
    {
        object screen = null;
        try { screen = BridgePump.OnMain(ScreenApi.Inspect, 3000).GetAwaiter().GetResult(); }
        catch { }

        if (pendingId == null)
        {
            try { pendingId = BridgePump.OnMain(RequestApi.PendingCardRequestId, 3000).GetAwaiter().GetResult(); }
            catch { }
        }

        BridgeLog.Info("batch: card selection pending, handing control back" +
                       (pendingId != null ? $" (pendingId={pendingId})" : ""));
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["pending"] = "card_select",
            ["reason"] = "choice",
            ["note"] = note,
            ["screen"] = screen,
            ["turn"] = null,
        };
        if (pendingId != null) result["pendingId"] = pendingId.Value;
        return result;
    }

    /// <summary>选牌请求的现场：在不在等选牌，以及 selector 通道挂着的那条请求 id（可能拿不到）。</summary>
    private sealed class ChoiceProbe
    {
        public bool Has;
        public long? Id;
    }

    /// <summary>
    /// 问一次「是不是在等选牌」。selector 通道**不弹界面**，只看 ScreenApi 会漏掉，
    /// 所以两边都要问：RequestApi 有没有挂着的选牌请求，或者界面上有选牌浮层。
    /// </summary>
    private static ChoiceProbe ProbeChoice()
    {
        try
        {
            return BridgePump.OnMain(() =>
            {
                var probe = new ChoiceProbe();
                try
                {
                    probe.Id = RequestApi.PendingCardRequestId() ?? RequestApi.PendingBundleRequestId();
                    probe.Has = probe.Id != null
                                || RequestApi.HasPendingBundleRequest()
                                || ScreenApi.HasCardSelectScreen();
                }
                catch { }
                return probe;
            }, 3000).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            BridgeLog.Warn("batch: 读选牌现场失败（" + e.GetType().Name + "）：" + e.Message);
            return new ChoiceProbe();
        }
    }

    /// <summary>Act 明确拒绝一个动作时，把文案归到 reason 上（只做分类，不改文案）。</summary>
    private static (string Reason, string Hint) ClassifyRejected(string kind, string error)
    {
        var text = error ?? "";
        if (text.Contains("no card with uid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hand is empty", StringComparison.OrdinalIgnoreCase))
            return ("uid_gone",
                "uid 失效：这张牌已经不在手里了（可能被上一张牌消耗/弃掉/变形），重新读 /state 拿新的 uid");

        if (text.Contains("card index", StringComparison.OrdinalIgnoreCase)
            && text.Contains("out of range", StringComparison.OrdinalIgnoreCase))
            return ("uid_gone", "手牌下标已经失效（手牌变了），重新读 /state 用 uid 定位");

        if (text.Contains("能量不足", StringComparison.Ordinal)
            || text.Contains("not enough energy", StringComparison.OrdinalIgnoreCase))
            return ("energy", "能量不够付这张牌：换一张更便宜的，或者 end_turn 把回合交出去");

        // GameApi.PlayCard 用游戏自己的 CanPlay 挡住了「条件不满足」的牌（费用之外的条件）。
        if (text.Contains("打不出去", StringComparison.Ordinal))
            return ("unplayable",
                "这张牌现在打不出去（不是费用问题就是被游戏规则拦了）：换一张能打的牌，先 GET /state 看手牌的 unplayableReason");

        return ("other", "动作被游戏拒绝，原因见 stopped；先 GET /state 看这个动作现在还能不能做");
    }

    private static bool WaitIdle(int timeoutMs)
    {
        var waited = 0;
        while (waited < timeoutMs)
        {
            try
            {
                if (!BridgePump.OnMain(GameApi.ActionRunning, 3000).GetAwaiter().GetResult()) return true;
            }
            catch { return true; }
            Task.Delay(200).Wait();
            waited += 200;
        }
        return false;
    }

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };

    /// <summary>带结构化 reason/hint 的失败（和 /acts 里 stopped+reason+hint 同一套字段）。</summary>
    private static Dictionary<string, object> Fail(string message, string reason, string hint) =>
        new()
        {
            ["ok"] = false,
            ["error"] = message,
            ["stopped"] = message,
            ["reason"] = reason,
            ["hint"] = hint,
        };
}
