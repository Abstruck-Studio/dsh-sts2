using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// 药水：列表 / 使用 / 丢弃。
///
/// <b>两个标识</b>（和卡牌、奖励共用一套 key 规则）：
/// <list type="bullet">
/// <item><b>key</b>（推荐）：<c>标题#同标题序号</c>，序号从 1 开始，按当前槽位顺序数。
/// 例如 <c>力量药水#1</c>、<c>力量药水#2</c>。每次调用都用 key 现场重读药水槽，
/// 所以读档 / 槽位重排之后 key 仍然指向"那一瓶"。</item>
/// <item><b>uid</b>（兼容保留）：<c>RuntimeHelpers.GetHashCode(PotionModel)</c>，
/// 本质是对象身份哈希。药水对象被换掉（读档、换局）就失效了，只留给老调用方。</item>
/// </list>
///
/// <b>丢弃走哪个入口</b>：<c>PotionCmd.Discard(potion)</c> —— 这是游戏自己的
/// <c>DiscardPotionGameAction.ExecuteAction</c> 最后调的那一个（先记地图点历史，
/// 再 <c>PotionModel.Discard()</c> 真正把药水从槽位移除并触发 <c>Player.PotionDiscarded</c>
/// 让界面播"药水飞走"的动画，最后 <c>Hook.AfterPotionDiscarded</c> 让遗物生效）。
/// <para>
/// 之前直接 <c>new DiscardPotionGameAction(player, slot, true)</c> + <c>RequestEnqueue</c>
/// 之所以没用：<c>RequestEnqueue</c> 只是把动作塞进玩家队列，真正取出来执行的是
/// <c>ActionExecutor</c> → <c>ActionQueueSet.GetReadyAction()</c>，而那里会按
/// <c>ActionQueueSet._isInCombat</c> 和动作自己的 <c>ActionType</c> 再判一次；
/// <c>DiscardPotionGameAction.ActionType</c> 在 <c>WasEnqueuedInCombat=true</c> 时是
/// <c>Combat(2)</c>，非战斗房间里 <c>_isInCombat=false</c>，这条动永远不算"ready"，
/// 就静静躺在队列里 —— 于是接口回了 ok，槽位一点没动。
/// </para>
///
/// 线程：<c>*Now</c> 必须在主线程调；不带 Now 的 async 版本从 HTTP 线程调（内部自己切主线程）。
/// </summary>
public static class Potions
{
    /// <summary>验证轮询的间隔（毫秒）。</summary>
    private const int PollMs = 250;

    /// <summary>验证轮询次数：8 × 250ms = 最多 2 秒，和 /potion/use 原来那套一致。</summary>
    private const int PollAttempts = 8;

    /// <summary>动手之前等动作队列空闲的上限：别在结算到一半时把药水抽走。</summary>
    private const int IdleWaitMs = 4000;

    // =================================================================== 快照 / key

    /// <summary>一次现场快照里的一个药水槽（key 就在这次快照里算出来）。</summary>
    private sealed class Slot
    {
        public int Index;          // 真实槽位下标（空槽也算一位）
        public string Key;         // 标题#同标题序号
        public int Uid;            // RuntimeHelpers.GetHashCode(PotionModel)
        public string Title;
        public PotionModel Potion;
    }

    /// <summary>
    /// 主线程：药水槽是不是满了（满了就没法再拿新药水），顺便给出当前已有药水的 key。
    /// 奖励里出现「药水」时先问这个，避免沉默地丢掉一瓶。
    /// </summary>
    public static bool SlotsFull(out int used, out int max, out List<string> keys)
    {
        used = 0;
        max = 0;
        keys = new List<string>();
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var player = state?.Players != null && state.Players.Count > 0 ? state.Players[0] : null;
            if (player == null) return false;

            max = player.MaxPotionCount;
            var slots = player.PotionSlots;
            if (slots != null)
            {
                foreach (var potion in slots)
                {
                    if (potion != null) used++;
                }
            }

            keys = Snapshot(player).Select(s => s.Key).ToList();
            return max > 0 && used >= max;
        }
        catch (Exception e)
        {
            BridgeLog.Warn("potions: 检查药水槽是否满失败：" + e.Message);
            return false;
        }
    }

    public static Dictionary<string, object> List()
    {
        var rm = RunManager.Instance;
        var state = rm?.DebugOnlyGetState();
        if (state == null) return Fail("no active run");

        var players = new List<object>();
        for (var pi = 0; pi < state.Players.Count; pi++)
        {
            var player = state.Players[pi];
            var slots = new List<object>();
            foreach (var slot in Snapshot(player))
            {
                slots.Add(new Dictionary<string, object>
                {
                    ["index"] = slot.Index,
                    ["key"] = slot.Key,          // 推荐用这个
                    ["uid"] = slot.Uid,          // 兼容保留
                    ["id"] = slot.Potion.Id?.ToString(),
                    ["title"] = slot.Title,
                    ["description"] = Text(slot.Potion.DynamicDescription),
                });
            }

            players.Add(new Dictionary<string, object>
            {
                ["player"] = pi,
                ["maxPotions"] = player.MaxPotionCount,
                ["potions"] = slots,
            });
        }

        return Ok(new Dictionary<string, object>
        {
            ["players"] = players,
            ["keyHint"] = "用 key（标题#同标题序号，例如 力量药水#1）定位药水；uid 仍返回，仅作兼容",
        });
    }

    /// <summary>主线程：把玩家现在身上所有非空药水槽读成一个列表，顺手算好稳定 key。</summary>
    private static List<Slot> Snapshot(Player player)
    {
        var list = new List<Slot>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        IReadOnlyList<PotionModel> slots;
        try { slots = player?.PotionSlots; }
        catch { slots = null; }
        if (slots == null) return list;

        for (var i = 0; i < slots.Count; i++)
        {
            var potion = slots[i];
            if (potion == null) continue;

            var title = TitleOf(potion);
            counts.TryGetValue(title, out var n);
            n++;
            counts[title] = n;

            list.Add(new Slot
            {
                Index = i,
                Key = title + "#" + n,
                Uid = Uid(potion),
                Title = title,
                Potion = potion,
            });
        }

        return list;
    }

    /// <summary>主线程：只读当前局（players[0]）的药水槽，读不到就返回空列表。</summary>
    private static List<Slot> CurrentSlots()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state?.Players == null || state.Players.Count == 0) return new List<Slot>();
            return Snapshot(state.Players[0]);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("potions: 读药水槽失败：" + e.Message);
            return new List<Slot>();
        }
    }

    /// <summary>key 的前半截：本地化标题（取不到就退回 ModelId 最后一段）。</summary>
    private static string TitleOf(PotionModel potion)
    {
        try
        {
            var title = Text(potion?.Title);
            if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        }
        catch { }

        try
        {
            var id = potion?.Id?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                var last = id.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(last)) return last.Trim();
            }
        }
        catch { }

        return "药水";
    }

    /// <summary>
    /// 把 key / uid / index 解析成这次快照里的具体那一瓶。优先级：key &gt; uid &gt; index。
    /// 解析失败时 error 里会带当前可用 key，让 agent 不用再 GET /potions 猜。
    /// </summary>
    private static bool Resolve(List<Slot> slots, string potionKey, int? uid, int? index,
        out Slot slot, out string error)
    {
        slot = null;
        error = null;

        var wanted = potionKey?.Trim();
        if (!string.IsNullOrEmpty(wanted))
        {
            slot = slots.FirstOrDefault(s => string.Equals(s.Key, wanted, StringComparison.Ordinal))
                   ?? slots.FirstOrDefault(s => string.Equals(s.Key, wanted, StringComparison.OrdinalIgnoreCase));
            if (slot == null)
            {
                error = $"没有 key 为「{wanted}」的药水。当前可用 key：{KeyListText(slots)}";
                return false;
            }
            return true;
        }

        if (uid is { } wantedUid)
        {
            slot = slots.FirstOrDefault(s => s.Uid == wantedUid);
            if (slot == null)
            {
                error = $"没有 uid 为 {wantedUid} 的药水（uid 现在只作兼容，推荐改用 key）。" +
                        $"当前可用 key：{KeyListText(slots)}";
                return false;
            }
            return true;
        }

        if (index is { } wantedIndex)
        {
            // index 是真实槽位下标（含空槽），所以按 Slot.Index 找，不是列表下标。
            slot = slots.FirstOrDefault(s => s.Index == wantedIndex);
            if (slot == null)
            {
                error = $"药水槽 {wantedIndex} 是空的（当前共 {slots.Count} 瓶）。当前可用 key：{KeyListText(slots)}";
                return false;
            }
            return true;
        }

        error = $"要指定哪一瓶药水：用 potion（推荐，key 形如 力量药水#1）或 uid / index。" +
                $"当前可用 key：{KeyListText(slots)}";
        return false;
    }

    private static string KeyListText(List<Slot> slots) =>
        slots.Count == 0 ? "（药水槽是空的）" : string.Join("、", slots.Select(s => s.Key));

    // =================================================================== 可用性 / 目标（按类型 + 模型层，不扫类名）

    /// <summary>
    /// 这瓶药水「需要选一个目标」时目标属于哪一类。<b>按 <c>PotionModel.TargetType</c> 判断</b>，
    /// 绝不按界面节点的类名字符串猜（旧实现扫场景树找类名含 <c>"MerchantCharacter"</c> 的节点，
    /// 再反射它的 <c>Creature</c> 属性 —— 但 <c>NMerchantCharacter</c> 上根本没有 <c>Creature</c>，
    /// 所以永远返回 null，「喝了但没打中」却报 ok:true）。
    /// </summary>
    private enum TargetKind
    {
        /// <summary>不需要目标（自己喝）：<c>EnqueueManualUse(null)</c> 由游戏自己把 owner 填进去。</summary>
        None,

        /// <summary>要一个 <c>Creature</c>：<c>AnyEnemy</c>（单机没有队友目标）。</summary>
        Creature,

        /// <summary>
        /// <c>TargetType.TargetedNoCreature</c>：目标「不是 Creature」。游戏里只有一种 —— 商人按钮
        /// （<c>FoulPotion.GetFoulPotionMerchantTarget(room)</c> 返回的 <c>NMerchantButton</c>），
        /// 而且交给模型的 target 本来就是 <c>null</c>（<c>NPotionHolder.TargetNode</c> 里
        /// <c>isinst NMerchantButton</c> 之后 <c>target = null</c>）。
        /// </summary>
        Merchant,
    }

    private static TargetKind KindOf(PotionModel potion)
    {
        if (potion == null) return TargetKind.None;
        try
        {
            // 联机里「可以投队友」也算需要选目标；单机恒为 false。
            if (potion.CanThrowAtAlly()) return TargetKind.Creature;
            switch (potion.TargetType)
            {
                case TargetType.AnyEnemy: return TargetKind.Creature;
                case TargetType.TargetedNoCreature: return TargetKind.Merchant;
                // None / Self / AllEnemies / RandomEnemy / AnyPlayer / AnyAlly / AllAllies / Osty：
                // 游戏自己的 NPotionHolder.UsePotion 也不为它们开目标 UI。
                default: return TargetKind.None;
            }
        }
        catch { return TargetKind.None; }
    }

    /// <summary>
    /// 主线程：这瓶药水「现在能不能用」。规则照抄游戏自己的 <c>NPotionPopup._Ready</c> +
    /// <c>RefreshButtons</c>（IL 逐条核对）：
    ///   ① <c>PotionUsage</c>：<c>AnyTime</c> 随时能用；<c>CombatOnly</c> 只在战斗里；
    ///      <c>Automatic</c>（自动生效，如精灵瓶）不能手动用；<c>None</c> 游戏自己会抛；
    ///   ② <c>PotionModel.PassesCustomUsabilityCheck</c>（污浊药水 override 成「战斗里 / 商店或假商人
    ///      且有商人按钮可点」）；
    ///   ③ 角色活着、<c>Player.CanRemovePotions</c>、这瓶没在队列里。
    /// </summary>
    public static bool CanUseNow(PotionModel potion, Player player, bool inCombat, out string whyNot)
    {
        whyNot = null;
        if (potion == null) { whyNot = "没有药水模型"; return false; }

        var creature = SafeCreature(player);
        if (creature == null) { whyNot = "读不到角色"; return false; }
        try { if (creature.IsDead) { whyNot = "角色已经死了"; return false; } } catch { }

        try { if (!player.CanRemovePotions) { whyNot = "当前事件不允许使用药水"; return false; } } catch { }
        try { if (potion.IsQueued) { whyNot = "这瓶药水已经在使用队列里了"; return false; } } catch { }

        PotionUsage usage;
        try { usage = potion.Usage; } catch { usage = PotionUsage.None; }
        switch (usage)
        {
            case PotionUsage.AnyTime:
                break;
            case PotionUsage.CombatOnly:
                if (!inCombat) { whyNot = "只有在战斗里才能用（当前不是战斗，没有可作用的目标）"; return false; }
                break;
            case PotionUsage.Automatic:
                whyNot = "自动生效的药水，不能手动使用";
                return false;
            default:
                whyNot = "游戏没有给这瓶药水设置用法（PotionUsage.None）";
                return false;
        }

        bool custom;
        try { custom = potion.PassesCustomUsabilityCheck; } catch { custom = true; }
        if (!custom)
        {
            whyNot = KindOf(potion) == TargetKind.None
                ? "游戏自己的可用性检查没过（PassesCustomUsabilityCheck = false）"
                : "游戏自己的可用性检查没过：这个场景没有它能作用的目标"
                  + "（PassesCustomUsabilityCheck = false）";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 主线程：当前场景有没有「商人」这个药水目标。<b>按类型判断</b>
    /// （<c>MerchantRoom</c> / <c>EventRoom.CanonicalEvent is FakeMerchant</c>），
    /// 再读那两间房间自己持有的 <c>MerchantButton</c> / <c>Inventory</c>：
    ///   * 真商店：<c>NMerchantRoom.Instance</c>（<c>MerchantRoom</c> 的房间节点）；
    ///   * 假商人：<c>EventRoom.LocalMutableEvent.Node as NFakeMerchant</c>。
    /// 游戏里「商人是药水目标」的唯一出处就是这两个 <c>MerchantButton</c>
    /// （<c>FoulPotion.GetFoulPotionMerchantTarget(room)</c> 返回的 <c>Item1</c>）。
    /// </summary>
    public static bool MerchantTargetAvailable(Player player, out string why)
    {
        why = null;
        AbstractRoom room = null;
        try { room = player?.RunState?.CurrentRoom; } catch { }

        if (room is MerchantRoom)
        {
            try
            {
                var node = NMerchantRoom.Instance;
                if (node == null || !Godot.GodotObject.IsInstanceValid(node)) { why = "NMerchantRoom 不在场"; return false; }
                if (node.Inventory == null) { why = "商店面板还没初始化"; return false; }
                if (node.MerchantButton == null) { why = "商人按钮不在场"; return false; }
                return true;
            }
            catch (Exception e) { why = "读 NMerchantRoom 失败：" + e.Message; return false; }
        }

        if (room is EventRoom eventRoom)
        {
            try
            {
                if (eventRoom.CanonicalEvent is not FakeMerchant) { why = "当前事件不是假商人"; return false; }
                var node = eventRoom.LocalMutableEvent?.Node as NFakeMerchant;
                if (node == null) { why = "假商人界面不在场"; return false; }
                if (node.Inventory == null) { why = "假商人的商店面板还没初始化"; return false; }
                if (node.MerchantButton == null) { why = "假商人的商人按钮不在场"; return false; }
                return true;
            }
            catch (Exception e) { why = "读假商人界面失败：" + e.Message; return false; }
        }

        why = "当前房间不是商店，也不是假商人事件";
        return false;
    }

    /// <summary>
    /// 主线程：药水槽快照，给 <c>/request</c> 的 <c>potions</c> 字段用
    /// （<c>key/title/description/usable/canTargetMerchant</c>，不能用时附 <c>whyNot</c>）。
    /// <c>usable</c> 只是**参考信息**：真正的拦截发生在 <c>/potion/use</c> 那一刻（见 <see cref="Probe"/>）。
    /// </summary>
    public static List<Dictionary<string, object>> DescribeSlotsForRequest(Player player)
    {
        var list = new List<Dictionary<string, object>>();
        try
        {
            if (player == null) return list;

            var inCombat = false;
            try { inCombat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress ?? false; }
            catch { inCombat = false; }

            foreach (var slot in Snapshot(player))
            {
                var usable = CanUseNow(slot.Potion, player, inCombat, out var whyNot);
                var kind = KindOf(slot.Potion);
                bool merchant = kind == TargetKind.Merchant && MerchantTargetAvailable(player, out _);
                if (!usable && kind == TargetKind.Merchant)
                    whyNot = (whyNot ?? "不能用") + "（商人目标只有商店面板没打开时才可选：先按返回关掉面板再丢）";

                var item = new Dictionary<string, object>
                {
                    ["key"] = slot.Key,
                    ["title"] = slot.Title,
                    ["description"] = Text(slot.Potion?.DynamicDescription),
                    ["usable"] = usable,
                    ["canTargetMerchant"] = merchant,
                };
                if (!usable && !string.IsNullOrEmpty(whyNot)) item["whyNot"] = whyNot;
                list.Add(item);
            }
        }
        catch (Exception e) { BridgeLog.Warn("potions: 读药水槽快照失败：" + e.Message); }
        return list;
    }

    /// <summary>主线程：这条药水目标请求还成不成立（药水还在槽里 + 目标还在）。</summary>
    public static bool TargetRequestStillValid(string potionKey, bool merchantTarget, out string why)
    {
        why = null;
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var player = state?.Players != null && state.Players.Count > 0 ? state.Players[0] : null;
            if (player == null) { why = "这一局已经不在了"; return false; }

            var found = Snapshot(player)
                .FirstOrDefault(s => string.Equals(s.Key, potionKey, StringComparison.Ordinal));
            if (found == null) { why = "药水「" + potionKey + "」已经不在槽里了"; return false; }

            if (merchantTarget && !MerchantTargetAvailable(player, out var mwhy))
            {
                why = "商人目标不在了（" + (mwhy ?? "?") + "）";
                return false;
            }
            return true;
        }
        catch (Exception e) { why = e.GetType().Name + "：" + e.Message; return false; }
    }

    /// <summary>「先问一遍」的结果：要么明确失败、要么要建目标请求、要么可以接着往下喝。</summary>
    private sealed class UseProbe
    {
        public Dictionary<string, object> Failure;      // 非空 = 直接返回它（明确失败或目标请求）
        public Dictionary<string, object> Request;      // 非空 = 建好了 kind="potion" 的目标请求
        public Slot Slot;
        public bool MerchantTarget;                     // 这瓶的目标是商人（不是 Creature）
    }

    /// <summary>
    /// 主线程：使用前的判定 + 目标解析。**不改任何状态**（不喝药、不入队、不消耗）。
    /// 战斗里沿用原来的 <c>target</c> 下标法，只把「需要目标却没给到」变成明确失败；
    /// 非战斗里先问游戏「这瓶现在能不能用」，能用但要选目标时按需建请求。
    /// </summary>
    private static UseProbe Probe(string potionKey, int? uid, int? index, int? targetIndex, bool createRequest)
    {
        var probe = new UseProbe();
        var rm = RunManager.Instance;
        var state = rm?.DebugOnlyGetState();
        if (rm == null || state?.Players == null || state.Players.Count == 0)
        {
            probe.Failure = Fail("no active run");
            return probe;
        }

        var player = state.Players[0];
        var slots = Snapshot(player);
        if (!Resolve(slots, potionKey, uid, index, out var slot, out var error))
        {
            probe.Failure = FailWithKeys(error, slots);
            return probe;
        }
        probe.Slot = slot;

        var potion = slot.Potion;
        var combatRoom = state.CurrentRoom as CombatRoom;
        var inCombat = combatRoom != null;

        // ---- 战斗中：沿用原来的 target 下标法 ----
        if (inCombat)
        {
            var hittable = SafeHittable(combatRoom);
            var tt = SafeTargetType(potion);

            if (targetIndex is { } t)
            {
                if (t < 0 || t >= hittable.Count)
                {
                    probe.Failure = Fail(
                        $"战斗里 target 下标 {t} 越界：当前可打的目标下标是 "
                        + (hittable.Count == 0 ? "（一个都没有）" : $"0..{hittable.Count - 1}")
                        + TargetChoices(hittable));
                    return probe;
                }
                return probe;               // 合法下标：交给 UseNow 用它
            }

            var needsTarget = tt == TargetType.AnyEnemy
                              || tt == TargetType.TargetedNoCreature
                              || SafeCanThrowAtAlly(potion);
            if (needsTarget)
            {
                probe.Failure = Fail("战斗里这瓶药水需要目标：请带上 target 下标（0 起的敌人下标）"
                                     + TargetChoices(hittable));
                return probe;
            }
            return probe;                   // 自喝：target:null 本来就是对的
        }

        // ---- 非战斗（商店 / 事件 / 营火 / 地图）：先问游戏「这瓶现在能不能用」----
        if (!CanUseNow(potion, player, false, out var whyNot))
        {
            // 区分两种失败：「用法本身不允许」vs「需要目标但场景里没有目标」。
            //   前者（战斗专用药水在商店里）→ 「用不了（游戏不允许）」；
            //   后者（污浊药水但商人不可选）→ 「需要目标，但没有可选的商人/敌人目标」。
            var usage = SafeUsage(potion);
            var needsTargetPotion = SafeTargetType(potion) == TargetType.TargetedNoCreature
                                    || SafeTargetType(potion) == TargetType.AnyEnemy;
            var usageForbids = usage != PotionUsage.AnyTime;
            probe.Failure = needsTargetPotion && !usageForbids
                ? NoTargetFailure(whyNot)
                : Unusable(whyNot);
            return probe;
        }

        var kind = KindOf(potion);
        if (kind == TargetKind.None) return probe;      // 不需要目标：target:null 正常，不误伤

        if (kind == TargetKind.Creature)
        {
            // 非战斗里没有可选的敌人/队友：宁可明确失败，也不能「喝了但没打中」。
            probe.Failure = NoTargetFailure("当前不是战斗，没有可选敌人");
            return probe;
        }

        // ---- kind == Merchant：TargetedNoCreature，目标是商人按钮（模型层 target 就是 null）----
        if (!MerchantTargetAvailable(player, out var mwhy))
        {
            probe.Failure = NoTargetFailure(mwhy);
            return probe;
        }

        probe.MerchantTarget = true;
        if (!createRequest) return probe;               // 目标已由调用方选定（应答那条路）

        probe.Request = RequestApi.BeginPotionTargetRequest(
            slot.Key, slot.Title, Text(potion.DynamicDescription),
            new List<RequestApi.PotionTargetOption>
            {
                new RequestApi.PotionTargetOption { Title = "商人", Merchant = true },
            });
        return probe;
    }

    private static List<Creature> SafeHittable(CombatRoom room)
    {
        try
        {
            var hittable = room?.CombatState?.HittableEnemies;
            if (hittable == null) return new List<Creature>();
            return hittable.Where(c => c != null).ToList();
        }
        catch { return new List<Creature>(); }
    }

    private static string TargetChoices(List<Creature> hittable)
    {
        if (hittable == null || hittable.Count == 0) return "";
        var parts = new List<string>();
        for (var i = 0; i < hittable.Count; i++)
        {
            string name;
            try { name = hittable[i]?.Name; } catch { name = null; }
            parts.Add($"{i}={name ?? "?"}");
        }
        return "（" + string.Join("、", parts) + "）";
    }

    private static TargetType SafeTargetType(PotionModel potion)
    {
        try { return potion?.TargetType ?? TargetType.None; } catch { return TargetType.None; }
    }

    private static bool SafeCanThrowAtAlly(PotionModel potion)
    {
        try { return potion?.CanThrowAtAlly() ?? false; } catch { return false; }
    }

    private static PotionUsage SafeUsage(PotionModel potion)
    {
        try { return potion?.Usage ?? PotionUsage.None; } catch { return PotionUsage.None; }
    }

    private static Creature SafeCreature(Player player)
    {
        try { return player?.Creature; } catch { return null; }
    }

    /// <summary>「游戏不允许在这用」的明确失败（不消耗药水、不入队）。</summary>
    private static Dictionary<string, object> Unusable(string why) => new()
    {
        ["ok"] = false,
        ["error"] = "这瓶药水在当前场景用不了（游戏不允许）" + (string.IsNullOrEmpty(why) ? "" : "：" + why),
        ["usable"] = false,
    };

    /// <summary>「需要目标但找不到目标」的明确失败 —— 以前这种情况会返回 ok:true + target:null。</summary>
    private static Dictionary<string, object> NoTargetFailure(string detail) => new()
    {
        ["ok"] = false,
        ["error"] = "这瓶药水需要目标，但当前场景没有可选的商人/敌人目标"
                    + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"),
        ["usable"] = false,
    };

    // =================================================================== 使用

    /// <summary>兼容老调用（GameApi 那条 4 参入口）：只是不带「打中了谁」的标签。</summary>
    public static Dictionary<string, object> UseNow(
        string potionKey, int? uid, int? index, int? targetIndex)
        => UseNow(potionKey, uid, index, targetIndex, null);

    /// <summary>
    /// 主线程：解析并当场喝掉一瓶药水。只负责"提交"，不做验证（验证在 <see cref="Probe"/>）。
    /// 战斗内照旧入动作队列（用 <paramref name="targetIndex"/> 下标取敌人）；
    /// 非战斗（商店/事件）退回 <c>PotionModel.EnqueueManualUse</c>，因为队列那时候不会跑。
    /// <paramref name="targetLabel"/> 只用于返回/日志里的「打中了谁」（商人不是 Creature，没法从模型里取名字）。
    /// </summary>
    public static Dictionary<string, object> UseNow(
        string potionKey, int? uid, int? index, int? targetIndex, string targetLabel)
    {
        var rm = RunManager.Instance;
        var state = rm?.DebugOnlyGetState();
        if (rm == null || state?.Players == null || state.Players.Count == 0) return Fail("no active run");

        var player = state.Players[0];
        var slots = Snapshot(player);
        if (!Resolve(slots, potionKey, uid, index, out var slot, out var error))
            return FailWithKeys(error, slots);

        var potion = slot.Potion;
        var inCombat = state.CurrentRoom is CombatRoom;

        Creature target = null;
        var label = targetLabel;
        if (inCombat && targetIndex is { } t && state.CurrentRoom is CombatRoom room && room.CombatState != null)
        {
            var hittable = room.CombatState.HittableEnemies;
            if (t >= 0 && t < hittable.Count)
            {
                target = hittable[t];
                try { label = target?.Name; } catch { }
            }
        }

        var payload = new Dictionary<string, object>
        {
            ["used"] = slot.Title,
            ["key"] = slot.Key,
            ["uid"] = slot.Uid,
            ["index"] = slot.Index,
            ["target"] = label,
        };

        if (inCombat)
        {
            // 正确 ctor 是 (PotionModel, Creature target, bool wasEnqueuedInCombat)。
            var wantsTarget = SafeTargetType(potion) == TargetType.AnyEnemy;
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, wantsTarget ? target : null, true);
            rm.ActionQueueSynchronizer.RequestEnqueue(action);
            payload["via"] = "action_queue";
            BridgeLog.Info($"use potion key={slot.Key} uid={slot.Uid} slot={slot.Index} ({potion.Id}) " +
                           $"via queue target={(wantsTarget ? target?.CombatId.ToString() : "none")}");
        }
        else
        {
            // 队列在非战斗房间里不跑，走界面"扔出去"按钮用的那个模型层入口。
            // target 一律 null：Self/AnyTime 药水由 EnqueueManualUse 自己填 owner；
            // TargetedNoCreature（污浊药水打商人）本来就没有 Creature，游戏自己在 OnUse 里认商人按钮。
            potion.EnqueueManualUse(null);
            payload["via"] = "model";
            BridgeLog.Info($"use potion key={slot.Key} uid={slot.Uid} slot={slot.Index} ({potion.Id}) " +
                           $"via model target={label ?? "none"} kind={KindOf(potion)}");
        }

        return Ok(payload);
    }

    /// <summary>
    /// HTTP 线程：喝药 → 轮询药水槽直到那一瓶真的消失（最多 2 秒）才算 ok。
    /// 使用前先 <see cref="Probe"/> 问一遍游戏：不能用的直接明确失败，需要选目标的改建请求。
    /// </summary>
    public static async Task<Dictionary<string, object>> Use(string potionKey, int? uid, int? index, int? targetIndex)
    {
        await WaitIdleAsync(IdleWaitMs);

        UseProbe probe;
        try { probe = await BridgePump.OnMain(() => Probe(potionKey, uid, index, targetIndex, true)); }
        catch (Exception e) { return Fail("检查药水能不能用的时候抛了：" + e.GetType().Name + "：" + e.Message); }

        if (probe == null) return Fail("检查药水能不能用的时候返回了空结果");
        if (probe.Failure != null) return probe.Failure;
        if (probe.Request != null) return probe.Request;     // {"ok":false,"needTarget":true,"request":{...}}

        var applied = await BridgePump.OnMain(() => UseNow(potionKey, uid, index, targetIndex, null));
        return await ConfirmUse(applied);
    }

    /// <summary>
    /// HTTP 线程：药水目标请求的应答 —— 用一个**已经由 agent 选定**的目标把药水用掉。
    /// 只在非战斗场景走这条路（战斗里的 target 下标仍走 <see cref="Use"/>）。
    /// </summary>
    public static async Task<Dictionary<string, object>> UseResolved(
        string potionKey, bool merchantTarget, string targetLabel)
    {
        await WaitIdleAsync(IdleWaitMs);

        UseProbe probe;
        try { probe = await BridgePump.OnMain(() => Probe(potionKey, null, null, null, false)); }
        catch (Exception e) { return Fail("检查药水能不能用的时候抛了：" + e.GetType().Name + "：" + e.Message); }

        if (probe == null) return Fail("检查药水能不能用的时候返回了空结果");
        if (probe.Failure != null) return probe.Failure;
        if (merchantTarget && !probe.MerchantTarget)
            return NoTargetFailure("这瓶药水的目标不是商人");

        var applied = await BridgePump.OnMain(() => UseNow(potionKey, null, null, null, targetLabel));
        return await ConfirmUse(applied);
    }

    /// <summary>药水提交之后统一核对：槽位里那一瓶真的消失了才叫 ok。</summary>
    private static async Task<Dictionary<string, object>> ConfirmUse(Dictionary<string, object> applied)
    {
        if (!IsTrue(applied, "ok")) return applied;
        if (!TryGetInt(applied, "uid", out var usedUid)) return applied;

        var (gone, waited) = await WaitGone(usedUid, PollAttempts);
        applied["confirmed"] = gone;
        applied["waitedMs"] = waited;
        applied["how"] = "slot-emptied";        // 老字段，保留兼容

        if (!gone)
        {
            applied["ok"] = false;
            applied["error"] =
                "药水没有生效：槽位里那瓶还在。常见原因：角色已死亡、动作队列卡在别的动作、" +
                "该药水需要目标而这次没给（或给的目标非法），或者这一局已经结束。" +
                "（战斗中入队是异步的，可以稍后 GET /potions 再看一眼）";
            applied["availableKeys"] = (await ReadSlotsSafe()).Select(s => s.Key).ToList();
        }

        return applied;
    }

    // =================================================================== 丢弃

    /// <summary>
    /// 主线程：解析并当场丢掉一瓶药水。真正动手的是 <c>PotionCmd.Discard(potion)</c>，
    /// 它同步做完"记历史 → 从槽位移除 → 触发 PotionDiscarded 动画"，最后的 hook 是异步的，
    /// 我们不等它（它失败也只会记日志，不影响"药水已经没了"这个事实）。
    /// </summary>
    public static Dictionary<string, object> DiscardNow(string potionKey, int? uid, int? index)
    {
        var rm = RunManager.Instance;
        var state = rm?.DebugOnlyGetState();
        if (rm == null || state?.Players == null || state.Players.Count == 0) return Fail("no active run");

        var player = state.Players[0];
        var slots = Snapshot(player);
        if (!Resolve(slots, potionKey, uid, index, out var slot, out var error))
            return FailWithKeys(error, slots);

        var potion = slot.Potion;
        var via = "potion-cmd";

        try
        {
            // 游戏自己的"丢弃"入口（DiscardPotionGameAction.ExecuteAction 最后调的就是它）。
            var task = PotionCmd.Discard(potion);
            Observed(task, "PotionCmd.Discard");
        }
        catch (Exception e)
        {
            // 极少见：PotionCmd 里记历史那步炸了。直接退到模型层，效果一样（少一条历史 + 一个 hook）。
            BridgeLog.Warn($"discard: PotionCmd.Discard 抛 {e.GetType().Name}: {e.Message}，退回 PotionModel.Discard()");
            try
            {
                potion.Discard();
                via = "model";
            }
            catch (Exception e2)
            {
                return Fail($"丢弃失败：{e2.GetType().Name}: {e2.Message}");
            }
        }

        var removed = CurrentSlots().All(s => s.Uid != slot.Uid);
        BridgeLog.Info($"discard potion key={slot.Key} uid={slot.Uid} slot={slot.Index} via={via} " +
                       $"removedNow={removed}");

        return Ok(new Dictionary<string, object>
        {
            ["discarded"] = slot.Title,
            ["key"] = slot.Key,
            ["uid"] = slot.Uid,
            ["index"] = slot.Index,
            ["via"] = via,
        });
    }

    /// <summary>
    /// HTTP 线程：丢弃 → 轮询药水槽直到那一瓶真的消失（最多 2 秒）才回 ok；没消失就是 ok:false。
    /// </summary>
    public static async Task<Dictionary<string, object>> Discard(string potionKey, int? uid, int? index)
    {
        await WaitIdleAsync(IdleWaitMs);

        var applied = await BridgePump.OnMain(() => DiscardNow(potionKey, uid, index));
        if (!IsTrue(applied, "ok")) return applied;

        if (!TryGetInt(applied, "uid", out var discardedUid)) return applied;

        var (gone, waited) = await WaitGone(discardedUid, PollAttempts);
        applied["confirmed"] = gone;
        applied["waitedMs"] = waited;
        applied["slotEmptied"] = gone;

        // 一开始就没丢干净（模型层返回但槽位没动）也要如实报失败。
        if (!gone)
        {
            var now = await ReadSlotsSafe();
            applied["ok"] = false;
            applied["error"] =
                $"丢弃没有生效：药水「{applied.GetValueOrDefault("discarded")}」还在槽位里。" +
                "常见原因：这一局已经结束、药水槽被重排（对象换掉了），或者动作队列正在结算别的动作。" +
                $"当前可用 key：{KeyListText(now)}";
            applied["availableKeys"] = now.Select(s => s.Key).ToList();
        }

        return applied;
    }

    // =================================================================== 公共工具

    /// <summary>等动作队列空闲（HTTP 线程用；不要在主线程上调用）。超时就算了，不阻塞调用方。</summary>
    public static async Task WaitIdleAsync(int timeoutMs = IdleWaitMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            bool running;
            try { running = await BridgePump.OnMain(GameApi.ActionRunning); }
            catch { return; }
            if (!running) return;
            await Task.Delay(80);
        }
    }

    /// <summary>轮询药水槽，直到这个 uid 消失。返回（是否消失，花了多少毫秒）。</summary>
    private static async Task<(bool Gone, long Waited)> WaitGone(int uid, int attempts)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(PollMs);

            List<Slot> slots;
            try { slots = await BridgePump.OnMain(CurrentSlots); }
            catch { continue; }                 // 主线程忙 / 场景在切：再等一轮

            if (slots.All(s => s.Uid != uid)) return (true, sw.ElapsedMilliseconds);
        }
        return (false, sw.ElapsedMilliseconds);
    }

    /// <summary>HTTP 线程：切主线程读一次当前药水槽。读不到就返回空列表，绝不抛。</summary>
    private static async Task<List<Slot>> ReadSlotsSafe()
    {
        try { return await BridgePump.OnMain(CurrentSlots); }
        catch { return new List<Slot>(); }
    }

    /// <summary>不 await 的 Task：把异常记进日志，免得被 UnobservedTaskException 静默吞掉。</summary>
    private static void Observed(Task task, string what)
    {
        if (task == null) return;
        _ = task.ContinueWith(t =>
        {
            try
            {
                var e = t.Exception?.GetBaseException();
                BridgeLog.Warn($"{what} 后续失败：{e?.GetType().Name}: {e?.Message}");
            }
            catch { }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static int Uid(PotionModel potion) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(potion);

    private static string Text(MegaCrit.Sts2.Core.Localization.LocString text)
    {
        try { return text?.GetFormattedText(); }
        catch { return null; }
    }

    private static bool IsTrue(Dictionary<string, object> payload, string key) =>
        payload != null && payload.TryGetValue(key, out var value) && value is bool b && b;

    private static bool TryGetInt(Dictionary<string, object> payload, string key, out int value)
    {
        value = 0;
        if (payload == null || !payload.TryGetValue(key, out var raw)) return false;
        if (raw is int i) { value = i; return true; }
        try
        {
            value = Convert.ToInt32(raw);
            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, object> Ok(Dictionary<string, object> payload)
    {
        var d = new Dictionary<string, object> { ["ok"] = true };
        foreach (var kv in payload) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>失败但"当前有哪些 key"是这次请求最需要的信息时用它。</summary>
    private static Dictionary<string, object> FailWithKeys(string message, List<Slot> slots) => new()
    {
        ["ok"] = false,
        ["error"] = message,
        ["availableKeys"] = slots.Select(s => s.Key).ToList(),
    };

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}
