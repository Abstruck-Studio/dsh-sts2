using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2AgentBridge;

/// <summary>
/// 菜单通道的「进阶难度」阶段（<c>kind:"menu"</c> + <c>stage:"ascension"</c>）。
///
/// ---- 为什么要多这一阶段 ----
/// 原来 <c>stage:"character"</c> 一答就直接按「开始」开打，agent 根本没有机会选 Ascension，
/// 每个角色自己的已解锁进阶等级也就永远用不上。
/// 现在流程拆成两步：character（只选中角色）→ ascension（选等级 + 按「开始」）。
///
/// ---- 真实数据链（MetaPriv + IL 核对）----
///   * <c>NCharacterSelectScreen._ascensionPanel : NAscensionPanel</c>；
///   * 角色被选中时 <c>SelectCharacter</c> 会调 panel.AnimIn()；上限由
///     <c>StartRunLobby.SetSingleplayerAscensionAfterCharacterChanged</c> 算好之后经
///     <c>NCharacterSelectScreen.MaxAscensionChanged</c> → <c>NAscensionPanel.SetMaxAscension(int)</c>
///     写进 <c>_maxAscension</c>。这条链里已经包含了游戏自己的两条规则：
///       ① 该角色存档里的 <c>CharacterStats.MaxAscension</c>；
///       ② 进阶 epoch 有没有揭开（<c>IsAscensionEpochRevealed</c>，没揭开就把上限压成 0）。
///   * 当前等级：<c>NAscensionPanel.Ascension</c>（int）。改它的公开入口是
///     <c>SetAscensionLevel(int)</c>：set_Ascension + EmitSignal(AscensionLevelChanged) + 刷新文本；
///     信号 → <c>NCharacterSelectScreen.OnAscensionPanelLevelChanged</c> →
///     <c>StartRunLobby.SyncAscensionChange(panel.Ascension)</c>，开打时就用 lobby 里的这个值。
///   * 「开始」还是原来的 <c>_embarkButton.ForceClick()</c> / <c>OnEmbarkPressed()</c>。
///
/// ---- 拿不到解锁等级时 ----
/// 只给 Ascension 0，并在 prompt / ascensionNote / pending.ascensionSource 里**明说**
/// 「无法读取解锁等级」。绝不拿一个编出来的上限给 agent。
/// </summary>
public static partial class RequestApi
{
    /// <summary>进阶阶段（选完角色之后）。</summary>
    private const string MenuStageAscension = "ascension";

    /// <summary>
    /// 「角色已经由 agent 选中、正在等它选进阶」的 mod 侧状态。
    /// 为什么不靠界面上「有没有已选角色」判断：角色选择页自己也可能预选一张卡（或者玩家手动点过），
    /// 那样读出来的 stage 会跳过 character 阶段。这个标记只在 <see cref="ApplyMenuCharacter"/> 成功之后立起来，
    /// 答完进阶 / 菜单整个消失 / 进了对局就清掉。
    /// </summary>
    private static bool _ascensionArmed;

    /// <summary>立起标记时选中的角色 id（只用来给日志/返回体做说明，不参与判定）。</summary>
    private static string _ascensionArmedCharacter;

    /// <summary>进阶候选的标题前缀（key = 标题#同标题序号）。</summary>
    private const string AscensionTitlePrefix = "Ascension";

    private const string AscensionVisualApplied = "ascension-applied";

    /// <summary>没走成时的 visual 占位。</summary>
    private const string AscensionVisualNone = "ascension-none";

    /// <summary>进阶可用性的一帧现场（全部在游戏数据上读）。</summary>
    private sealed class AscensionAvailability
    {
        /// <summary>已解锁的最高进阶（0 = 只有无进阶）。读不到时也是 0，但同时会带 <see cref="Unreadable"/>。</summary>
        public int Max;

        /// <summary>面板上当前选的等级；读不到是 -1。</summary>
        public int Current = -1;

        /// <summary>上限是从哪儿读到的（给 agent 的证据链，读不到就是 null）。</summary>
        public string Source;

        /// <summary>读不到解锁等级时的现场事实（null = 读到了）。</summary>
        public string Unreadable;
    }

    /// <summary>读进阶可用性（必须在主线程跑）。三级回退，全部是游戏自己的数据。</summary>
    private static AscensionAvailability DescribeAscensionAvailability(NCharacterSelectScreen screen)
    {
        var info = new AscensionAvailability();
        if (screen == null)
        {
            info.Unreadable = "角色选择界面不在了";
            return info;
        }

        var panel = SafeModel(() => ReadMember(screen, "_ascensionPanel") as NAscensionPanel);
        if (panel != null) info.Current = ReadInt(panel, "Ascension", -1);

        // ① 界面自己算好的上限（含「角色解锁到第几级」+「进阶 epoch 揭没揭开」两条游戏规则）。
        if (panel != null)
        {
            var max = ReadInt(panel, "_maxAscension", -1);
            if (max >= 0)
            {
                info.Max = max;
                info.Source = "NAscensionPanel._maxAscension（SetMaxAscension 由 StartRunLobby 算出）";
            }
        }

        // ② 同一份数据的源头：StartRunLobby.MaxAscension。
        if (info.Source == null)
        {
            var lobby = SafeModel(() => ReadMember(screen, "_lobby"));
            if (lobby != null)
            {
                var max = ReadInt(lobby, "MaxAscension", -1);
                if (max >= 0)
                {
                    info.Max = max;
                    info.Source = "StartRunLobby.MaxAscension";
                }
            }
        }

        // ③ 最后回退到存档进度里的角色统计（只读字典；不调 GetOrCreateCharacterStats，免得往存档里写条目）。
        if (info.Source == null)
        {
            var stats = ProgressMaxAscension(screen);
            if (stats >= 0)
            {
                info.Max = stats;
                info.Source = "SaveManager.Progress.CharacterStats[角色].MaxAscension";
            }
        }

        // 游戏自己的硬上限（读不到就跳过这一层）。
        try
        {
            var cap = AscensionManager.maxAscensionAllowed;
            if (cap > 0 && info.Max > cap) info.Max = cap;
        }
        catch { }

        if (info.Source == null)
        {
            info.Max = 0;
            info.Unreadable = "无法读取解锁等级（NAscensionPanel._maxAscension / StartRunLobby.MaxAscension / 存档进度都读不到）";
        }

        if (info.Current > info.Max) info.Current = info.Max;
        return info;
    }

    /// <summary>存档进度里这个角色的 <c>CharacterStats.MaxAscension</c>；读不到返回 -1。</summary>
    private static int ProgressMaxAscension(NCharacterSelectScreen screen)
    {
        try
        {
            var model = SafeModel(() => ReadMember(screen, "_selectedButton") as NCharacterSelectButton)?.Character;
            if (model == null) return -1;
            var wanted = model.Id.ToString();

            var progress = SaveManager.Instance?.Progress;
            var stats = progress?.CharacterStats;
            if (stats == null) return -1;

            foreach (var entry in stats)
            {
                try
                {
                    if (!string.Equals(entry.Key.ToString(), wanted, StringComparison.Ordinal)) continue;
                    if (entry.Value == null) return -1;
                    return entry.Value.MaxAscension;
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("ascension: 读存档进度失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return -1;
    }

    /// <summary>进阶阶段的 prompt：把「已解锁到第几级」写清楚；读不到就照实说。</summary>
    private static string AscensionPrompt(AscensionAvailability info)
    {
        if (!string.IsNullOrEmpty(info.Unreadable))
            return "选择进阶难度（" + info.Unreadable + "，只允许 Ascension 0）";
        return $"选择进阶难度（已解锁 Ascension 0~{info.Max}）";
    }

    /// <summary>单个等级的 detail：0 = 无进阶；其余用游戏自己的标题/说明，读不到就写「进阶 N」。</summary>
    private static string AscensionDetail(int level)
    {
        if (level <= 0) return "无进阶（基础难度）";

        string title = null;
        string description = null;
        try { title = LocText(AscensionHelper.GetTitle(level)); } catch { }
        try { description = LocText(AscensionHelper.GetDescription(level)); } catch { }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description)) return "进阶 " + level;
        if (string.IsNullOrWhiteSpace(description)) return title;
        if (string.IsNullOrWhiteSpace(title)) return description;
        return title + "：" + description;
    }

    /// <summary>
    /// 进阶阶段的候选：<c>Ascension 0</c> … <c>Ascension {max}</c>。
    /// key 用「Ascension N#1」，所以 agent 交 key 就等于交了等级；等级本身还写在候选的
    /// <c>ascension</c> 字段里，应答时按候选对象取值，不从 key 里解析字符串。
    /// </summary>
    private static MenuView ReadAscensionMenu(NCharacterSelectScreen screen)
    {
        var info = DescribeAscensionAvailability(screen);
        var view = new MenuView
        {
            Stage = MenuStageAscension,
            Prompt = AscensionPrompt(info),
            Caller = "NAscensionPanel",
            Screen = screen,
        };

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var level = 0; level <= info.Max; level++)
        {
            var title = AscensionTitlePrefix + " " + level;
            var candidate = new Candidate
            {
                Key = NextKey(counts, title),
                Title = title,
                Upgraded = false,
                Cost = -1,
                Enabled = true,
                MenuAscension = level,
                MenuStage = MenuStageAscension,
                Detail = AscensionDetail(level),
            };
            view.Candidates.Add(candidate);
            if (level == info.Current) view.Selected = candidate.Key;
        }

        return view;
    }

    /// <summary>
    /// 进阶阶段给 /request 的补充字段：上限、当前等级、上限的来源（读不到时的现场事实也带上）。
    /// 只在这个阶段出现，其它菜单阶段不受影响。
    /// </summary>
    private static void FillAscensionFields(Dictionary<string, object> dict, MenuView view)
    {
        if (dict == null || view == null) return;
        if (!string.Equals(view.Stage, MenuStageAscension, StringComparison.Ordinal)) return;

        var info = DescribeAscensionAvailability(view.Screen as NCharacterSelectScreen);
        dict["ascensionMax"] = info.Max;
        if (info.Current >= 0) dict["ascensionCurrent"] = info.Current;
        if (!string.IsNullOrEmpty(info.Source)) dict["ascensionSource"] = info.Source;
        if (!string.IsNullOrEmpty(info.Unreadable)) dict["ascensionNote"] = info.Unreadable;
        if (!string.IsNullOrEmpty(_ascensionArmedCharacter)) dict["characterId"] = _ascensionArmedCharacter;
    }

    /// <summary>
    /// 把面板上的进阶等级设成 <paramref name="level"/>（必须在主线程跑），并确认它真的落到
    /// <c>StartRunLobby.Ascension</c>（开打时用的是 lobby 里这个值，不是面板的）。
    /// </summary>
    private static (bool Ok, string How, string Why) ApplyAscensionLevel(NCharacterSelectScreen screen, int level)
    {
        var panel = SafeModel(() => ReadMember(screen, "_ascensionPanel") as NAscensionPanel);
        if (panel == null)
            return (false, AscensionVisualNone, "界面上找不到 NAscensionPanel（这个界面看起来不支持进阶）");

        try { panel.SetAscensionLevel(level); }
        catch (Exception e)
        {
            return (false, AscensionVisualNone,
                "调 NAscensionPanel.SetAscensionLevel 失败（" + e.GetType().Name + "）：" + e.Message);
        }

        var now = ReadInt(panel, "Ascension", -1);
        if (now != level)
            return (false, AscensionVisualNone, $"调了 SetAscensionLevel({level})，但面板上的 Ascension 是 {now}");

        var how = "ascension-panel";
        var lobby = SafeModel(() => ReadMember(screen, "_lobby"));
        var lobbyLevel = lobby == null ? -1 : ReadInt(lobby, "Ascension", -1);
        if (lobby != null && lobbyLevel != level)
        {
            // 面板的信号没连上（在别的模式下初始化过）时补一次：SyncAscensionChange 就是它的信号回调。
            var sync = FindMethod(lobby.GetType(), "SyncAscensionChange", 1);
            if (sync == null)
                return (false, AscensionVisualNone,
                    $"面板设成了 {level}，但 StartRunLobby.Ascension 还是 {lobbyLevel}，而且找不到 SyncAscensionChange");
            try
            {
                sync.Invoke(lobby, new object[] { level });
                how = "ascension-panel+lobby-sync";
            }
            catch (Exception e)
            {
                return (false, AscensionVisualNone, "调 StartRunLobby.SyncAscensionChange 失败：" + e.Message);
            }
        }

        return (true, how, null);
    }

    /// <summary>开局之后 <c>RunState.AscensionLevel</c>（读不到返回 -1）。</summary>
    private static int RunAscensionLevel()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.AscensionLevel ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// 进阶阶段的核心体：设等级 → 按「开始」（游戏自己的 _embarkButton）→ 等 RunManager 真的进对局
    /// → 用 <c>RunState.AscensionLevel</c> 复核「真的是这个进阶开起来的」。
    /// </summary>
    private static async Task<Dictionary<string, object>> ApplyMenuAscension(
        PendingSelection pending, Candidate chosen, MenuView view, Stopwatch sw)
    {
        if (chosen.MenuAscension < 0)
            return Fail($"{chosen.Key} 不是进阶候选");
        if (view.Screen is not NCharacterSelectScreen screen || !IsShown(screen))
            return Fail("角色选择界面已经不在场景树里了（重新 GET /request）");

        var level = chosen.MenuAscension;

        // 现场复核上限：选项发出去之后解锁状态不会变，但面板可能还没初始化完。
        var info = await BridgePump.OnMain(() => DescribeAscensionAvailability(screen));
        if (level > info.Max)
        {
            return Fail($"Ascension {level} 没有解锁：这个角色最多到 Ascension {info.Max}" +
                        (string.IsNullOrEmpty(info.Unreadable) ? "" : $"（{info.Unreadable}）"));
        }

        var apply = await BridgePump.OnMain(() => ApplyAscensionLevel(screen, level));
        if (!apply.Ok) return Fail(apply.Why ?? $"没能把进阶设成 Ascension {level}");

        var embark = await BridgePump.OnMain(() => PressEmbark(screen));
        if (!embark.Ok) return Fail(embark.Why ?? "「开始」按钮没按下去");

        var started = await WaitRunStarted();
        var waited = sw.ElapsedMilliseconds;
        if (!started)
        {
            var blocker = RunStartBlocker();
            return Fail($"Ascension {level} 已设好（{apply.How}）、「开始」也按了（{embark.How}），" +
                        $"但 RunManager 还没进入进行中（IsInProgress=false，等了 {waited}ms）" +
                        (string.IsNullOrEmpty(blocker) ? "" : "；" + blocker));
        }

        // 真的以这个进阶开起来的？以 RunState.AscensionLevel 为准（不是面板、不是 lobby）。
        var actual = await BridgePump.OnMain(RunAscensionLevel);

        // 开局后的现场自检：有时候 RunManager 起来了但地图/第一间房间没建出来
        // （CurrentRoom=null、画面只剩 HUD 黑屏），这时 move 一定失败、save-quit 会卡死主线程。
        // 这里等最多 2.5s 观察 CurrentRoom，把结果写进日志和返回值，让 agent 立刻知道要退回主菜单。
        string roomAfter = null;
        var roomWaitMs = 0;
        while (roomWaitMs < 2500)
        {
            try
            {
                roomAfter = await BridgePump.OnMain(() =>
                {
                    var st = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
                    return st?.CurrentRoom?.RoomType.ToString();
                });
            }
            catch { }
            if (!string.IsNullOrEmpty(roomAfter)) break;
            await Task.Delay(150);
            roomWaitMs += 150;
        }

        var halfInit = string.IsNullOrEmpty(roomAfter);
        if (halfInit)
        {
            var screens = "?";
            try { screens = await BridgePump.OnMain(() => MapScreenOpen() ? "MapScreen" : "no-map-screen"); } catch { }
            BridgeLog.Warn($"menu ashore: 开局后 CurrentRoom 仍是 null（等了 {roomWaitMs}ms，屏幕={screens}）" +
                           " → 半初始化对局，move/save-quit 都会坏，应退回主菜单重开");
        }

        BridgeLog.Info($"menu answered id={pending.Id} stage=ascension level={level} how={apply.How} " +
                       $"embark={embark.How} runAscension={actual} room={roomAfter ?? "null"} waitedMs={waited}");

        var characterId = chosen.MenuId;
        _ascensionArmed = false;

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = MenuKind,
            ["stage"] = MenuStageAscension,
            ["applied"] = chosen.Key,
            ["how"] = apply.How + "+" + embark.How,
            ["animated"] = false,
            ["waitedMs"] = waited,
            ["visual"] = AscensionVisualApplied,
            ["ascension"] = level,
            ["ascensionMax"] = info.Max,
            ["ascensionVerified"] = actual == level,
            ["runAscension"] = actual,
            ["characterId"] = characterId ?? "",
            ["room"] = roomAfter ?? "",
            ["halfInit"] = halfInit,
            ["embark"] = embark.How,
            ["selected"] = true,
            ["started"] = true,
            ["settled"] = true,
        };
        if (!string.IsNullOrEmpty(info.Source)) result["ascensionSource"] = info.Source;
        if (actual != level)
        {
            // 开起来了但不是这个进阶：如实标出来（不谎报成功）。
            result["ascensionNote"] = actual < 0
                ? "读不到 RunState.AscensionLevel，无法复核这一局用的是哪个进阶"
                : $"这一局实际是以 Ascension {actual} 开的（请求 {level}）";
        }
        return result;
    }
}
