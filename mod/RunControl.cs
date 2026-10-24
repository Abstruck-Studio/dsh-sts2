using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// 存档并退出 / 重新读档（重开本局）。两个都是「游戏自己的入口」，不强杀进程、不自己写文件。
///
/// ---- /save-quit 的真实链路（MetaPriv + IL 核对）----
/// 暂停菜单里那个按钮是 <c>NPauseMenu.OnSaveAndQuitButtonPressed(NButton)</c>，它的 IL 只有一句：
///   <c>TaskHelper.RunSafely(CloseToMenu())</c>
/// 而 <c>CloseToMenu()</c> 做的是：把暂停菜单上的按钮全部 Disable → 停音乐 → 停调试音 →
///   <c>NGame.Instance.ReturnToMainMenu()</c>
/// 也就是说游戏自己的「保存并退出」**并不会额外调存盘**：进度是在房间推进时由
/// <c>SaveManager.Instance.SaveRun(room, saveProgress)</c> 自动写的（<c>RunManager.ShouldSave</c> 为真时才写）。
/// 我们这里两步都走游戏自己的入口：
///   ① <c>SaveManager.Instance.SaveRun(preFinishedRoom: null, saveProgress: true)</c>
///      —— 就是自动存档用的那一个（内部：等上一次存盘 → BeginSaveBatch → SaveProgressFile →
///      <c>RunManager.ToSave(preFinishedRoom)</c> → <c>RunSaveManager.SaveRun</c> → EndSaveBatch）；
///      <c>preFinishedRoom: null</c> 表示「就按当前房间存」，和游戏自己进房间时存的是同一个形状；
///   ② <c>NGame.Instance.ReturnToMainMenu()</c>
///      —— ReturnToMainMenu 的 IL：FadeOut → LoadCommonAndMainMenuAssets →
///      <c>RunManager.Instance.CleanUp(true)</c>（注意 CleanUp 会把 ShouldSave 置 false，所以必须**先存再退**）→ LoadMainMenu。
///
/// ---- /reload 的真实链路 ----
/// 主菜单「继续游戏」= <c>NMainMenu._continueButton</c>（NMainMenuTextButton，Released → OnContinueButtonPressed
/// → OnContinueButtonPressedAsync）。它的 IL 依次是：
///   <c>_readRunSaveResult.SaveData</c> → <c>RunState.FromSerializable(save)</c> →
///   <c>RunManager.Instance.SetUpSavedSingleplayer(runState, save)</c> → 淡入 →
///   <c>NGame.Instance.LoadRun(runState, save.PreFinishedRoom)</c> → 淡出。
/// 所以 /reload 优先**直接按那个按钮**（最忠实）；按钮拿不到时才按上面这段自己走一遍。
/// 读档落点就是游戏自己的存档点（存档里存的是当前房间，不是「战斗开始」）。
/// </summary>
public static class RunControl
{
    /// <summary>等存盘任务完成的预算（内含上一次存盘要等，正常是几百毫秒）。</summary>
    private const int SaveTimeoutMs = 15000;

    /// <summary>等「回主菜单」真的落地的预算（淡出 + 载资源 + CleanUp + 载主菜单）。</summary>
    private const int QuitTimeoutMs = 30000;

    /// <summary>等「读档进对局」真的落地的预算（淡出 + FromSerializable + SetUpSavedSingleplayer + LoadRun）。</summary>
    private const int ReloadTimeoutMs = 40000;

    /// <summary>轮询间隔。</summary>
    private const int PollMs = 150;

    // =================================================================== /save-quit

    /// <summary>
    /// 保存当前进度并回到主菜单。必须在 HTTP 线程调用（它会 await 主线程上发起的 Task）。
    /// </summary>
    public static async Task<Dictionary<string, object>> SaveQuit()
    {
        var sw = Stopwatch.StartNew();

        SaveQuitStart start;
        try { start = await BridgePump.OnMain(CaptureSaveQuitStart, 6000); }
        catch (Exception e) { return Fail("读对局现场失败：" + e.GetType().Name + "：" + e.Message); }

        if (!start.InRun)
            return Fail("现在不在对局里（RunManager.IsInProgress=false）：没有可保存的进度");
        if (!start.ShouldSave)
            return Fail("RunManager.ShouldSave=false（游戏这会儿不写对局存档，通常是在结算/清理中）：先等这一屏处理完");

        // 半初始化对局（CurrentRoom 为 null，典型是开局后地图界面没建出来、黑屏）：
        // 这时 SaveRun 的任务永远不完成，15s 超时之外，挂住的存盘任务还会把主线程拖死。
        // 直接拒绝，让调用方走「回主菜单 / 放弃本局」。
        if (string.IsNullOrEmpty(start.Room))
            return Fail(
                "这局没有当前房间（CurrentRoom=null，通常开局后半初始化/黑屏）：对它存盘会卡死主线程，已拒绝。" +
                "别重试 /save-quit；请回主菜单或放弃本局后重新开局。");

        // ① 先用游戏自己的存盘入口存一次（null = 按当前房间存，和自动存档同一形状）。
        Task saveTask = null;
        try
        {
            saveTask = await BridgePump.OnMain(StartSaveRun, 6000);
        }
        catch (Exception e)
        {
            return Fail("发起存盘失败：" + e.GetType().Name + "：" + e.Message);
        }
        if (saveTask == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "发起存盘失败：SaveManager 不可用" };

        var saved = false;
        string saveError = null;
        try
        {
            var done = await Task.WhenAny(saveTask, Task.Delay(SaveTimeoutMs)) == saveTask;
            if (!done) saveError = $"存盘 {SaveTimeoutMs}ms 内没完成";
            else if (saveTask.IsFaulted)
                saveError = "存盘抛异常：" + (saveTask.Exception?.GetBaseException().Message ?? "原因不明");
            else if (saveTask.IsCanceled)
                saveError = "存盘被取消";
            else saved = true;
        }
        catch (Exception e)
        {
            saveError = "等存盘时出错：" + e.GetType().Name + "：" + e.Message;
        }

        if (!saved)
        {
            // 没存成功就别退：退了反而把这一局丢掉（CleanUp 之后 ShouldSave=false，再也存不了）。
            return Fail("存盘没有完成，所以没有退出对局：" + (saveError ?? "原因不明"));
        }

        var hasSave = await SafeOnMain(HasRunSave, false);

        // ② 再走游戏自己的「回主菜单」路径（= NPauseMenu.CloseToMenu 里最后那一步）。
        var quitTask = await BridgePump.OnMain(StartReturnToMainMenu, 6000);
        if (quitTask != null && quitTask.IsFaulted)
        {
            BridgeLog.Warn("save-quit: ReturnToMainMenu 抛了（" +
                           (quitTask.Exception?.GetBaseException().Message ?? "原因不明") + "）");
        }

        var arrived = await WaitMainMenu(QuitTimeoutMs);
        var waited = sw.ElapsedMilliseconds;
        if (!arrived)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["saved"] = true,
                ["error"] = $"进度已保存，但「回主菜单」在 {QuitTimeoutMs}ms 内没有落地（RunManager 还在进行中 / 主菜单没出来）",
                ["waitedMs"] = waited,
                ["room"] = await SafeOnMain(CurrentRoom, (string)null),
            };
        }

        BridgeLog.Info($"save-quit: saved then returned to main menu (waitedMs={waited}, hasRunSave={hasSave})");
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["saved"] = true,
            ["room"] = "MainMenu",
            ["waitedMs"] = waited,
            ["hasRunSave"] = hasSave,
            ["wasRoom"] = start.Room,
            ["wasAct"] = start.Act,
            ["wasFloor"] = start.TotalFloor,
            ["wasAscension"] = start.Ascension,
            ["saveEntry"] = "SaveManager.Instance.SaveRun(preFinishedRoom:null, saveProgress:true)",
            ["quitEntry"] = "NGame.Instance.ReturnToMainMenu()（= NPauseMenu.CloseToMenu 的最后一步）",
            ["note"] = "读档落点就是游戏自己的存档点（存档里记的是当前房间，不是战斗开始）；/reload = 主菜单「继续游戏」",
        };
    }

    private sealed class SaveQuitStart
    {
        public bool InRun;
        public bool ShouldSave;
        public string Room;
        public string Act;
        public int TotalFloor;
        public int Ascension = -1;
    }

    /// <summary>一帧对局现场（主线程）。</summary>
    private static SaveQuitStart CaptureSaveQuitStart()
    {
        var start = new SaveQuitStart();
        try
        {
            var run = RunManager.Instance;
            if (run == null) return start;
            start.InRun = run.IsInProgress;
            start.ShouldSave = run.ShouldSave;

            var state = run.DebugOnlyGetState();
            if (state != null)
            {
                start.Room = state.CurrentRoom?.RoomType.ToString();
                start.Act = state.Act?.Id?.ToString();
                start.TotalFloor = state.TotalFloor;
                start.Ascension = state.AscensionLevel;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("save-quit: 读对局现场失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return start;
    }

    /// <summary>发起游戏自己的存盘（主线程）：返回那个 Task，**不**在这里 await（主线程上 await 会卡住泵）。</summary>
    private static Task StartSaveRun()
    {
        try
        {
            var manager = SaveManager.Instance;
            if (manager == null) return null;
            var task = manager.SaveRun(null, true);
            BridgeLog.Info("save-quit: SaveManager.SaveRun(null, saveProgress:true) 已发起");
            return task;
        }
        catch (Exception e)
        {
            BridgeLog.Error("save-quit: 发起存盘抛异常", e);
            var tcs = new TaskCompletionSource<bool>();
            tcs.SetException(e);
            return tcs.Task;
        }
    }

    /// <summary>发起游戏自己的「回主菜单」（主线程）：NGame.ReturnToMainMenu()。</summary>
    private static Task StartReturnToMainMenu()
    {
        try
        {
            var game = NGame.Instance;
            if (game == null) return null;
            var task = game.ReturnToMainMenu();
            BridgeLog.Info("save-quit: NGame.ReturnToMainMenu() 已发起");
            return task;
        }
        catch (Exception e)
        {
            BridgeLog.Error("save-quit: 发起回主菜单抛异常", e);
            var tcs = new TaskCompletionSource<bool>();
            tcs.SetException(e);
            return tcs.Task;
        }
    }

    /// <summary>等「已经不在对局里 + 主菜单节点在树上」。</summary>
    private static async Task<bool> WaitMainMenu(int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += PollMs)
        {
            bool there;
            try { there = await BridgePump.OnMain(MainMenuReady, 5000); }
            catch { there = false; }
            if (there) return true;
            await Task.Delay(PollMs);
        }
        return false;
    }

    /// <summary>不在对局里、而且 NMainMenu 已经挂到树上（ReturnToMainMenu 的最后一步）。</summary>
    private static bool MainMenuReady()
    {
        try
        {
            var run = RunManager.Instance;
            if (run != null && run.IsInProgress) return false;
            return FindMainMenu() != null;
        }
        catch { return false; }
    }

    // =================================================================== /reload

    /// <summary>
    /// 从存档继续游戏（= 主菜单「继续游戏」）。必须在 HTTP 线程调用。
    /// </summary>
    public static async Task<Dictionary<string, object>> Reload()
    {
        var sw = Stopwatch.StartNew();

        var inRun = await SafeOnMain(() => RunManager.Instance != null && RunManager.Instance.IsInProgress, false);
        if (inRun)
            return Fail("现在已经在一局里了：先 POST /save-quit 回到主菜单，再 /reload（否则读档会和当前对局打架）");

        var hasSave = await SafeOnMain(HasRunSave, false);
        if (!hasSave)
            return Fail("没有可用的对局存档（SaveManager.HasRunSave=false）：这个档可能已经被通关/放弃清掉了");

        var menuUp = await WaitMainMenu(QuitTimeoutMs);

        // ① 最忠实的一条：直接按主菜单的「继续游戏」按钮。
        ReloadPress press;
        try { press = await BridgePump.OnMain(PressContinueButton, 6000); }
        catch (Exception e) { return Fail("按「继续游戏」失败：" + e.GetType().Name + "：" + e.Message); }

        string how = press.How;
        string why = press.Why;
        if (!press.Ok)
        {
            // ② 按钮拿不到（主菜单还没起来 / 没有这个字段）：按 OnContinueButtonPressedAsync 的 IL 自己走一遍。
            var manual = await SafeOnMain(StartManualLoad, false);
            if (!manual)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "没能开始读档：" + (why ?? "主菜单的「继续游戏」按钮不可用") +
                                (menuUp ? "" : "（主菜单当时也还没出来）"),
                    ["menuReady"] = menuUp,
                    ["hasRunSave"] = hasSave,
                };
            }
            how = "manual-load";
        }

        var arrived = await WaitRunStarted(ReloadTimeoutMs);
        var waited = sw.ElapsedMilliseconds;
        if (!arrived)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["hasRunSave"] = hasSave,
                ["how"] = how,
                ["error"] = $"读档已经发起（{how}），但 RunManager 在 {ReloadTimeoutMs}ms 内没有进入进行中（inRun 还是 false）",
                ["waitedMs"] = waited,
            };
        }

        var info = await SafeOnMain(DescribeRun, null);
        BridgeLog.Info($"reload: in run again via {how} (waitedMs={waited})");

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["inRun"] = true,
            ["hasRunSave"] = hasSave,
            ["how"] = how,
            ["waitedMs"] = waited,
            ["reloadEntry"] = "NMainMenu._continueButton（ForceClick = 主菜单「继续游戏」）",
            ["note"] = "落点是游戏自己的存档点（存的是当前房间），不是「回到战斗开始」",
        };
        if (info != null)
            foreach (var kv in info) result[kv.Key] = kv.Value;
        return result;
    }

    private sealed class ReloadPress
    {
        public bool Ok;
        public string How;
        public string Why;
    }

    /// <summary>
    /// 按主菜单自己的「继续游戏」按钮（主线程）。走按钮的 <c>ForceClick()</c>
    /// （= NClickableControl 的 OnRelease + EmitSignal(Released) → NMainMenu.OnContinueButtonPressed
    /// → OnContinueButtonPressedAsync）。
    /// </summary>
    private static ReloadPress PressContinueButton()
    {
        var press = new ReloadPress();
        try
        {
            var menu = FindMainMenu();
            if (menu == null) { press.Why = "场景树里没有可见的 NMainMenu"; return press; }

            var button = ReadMemberStatic(menu, "_continueButton") as NClickableControl;
            if (button == null) { press.Why = "NMainMenu 上没有 _continueButton"; return press; }
            if (!IsUsable(button)) { press.Why = "「继续游戏」按钮当前不可用（不可见 / IsEnabled=false）"; return press; }

            button.ForceClick();
            press.Ok = true;
            press.How = "continue-button";
            BridgeLog.Info("reload: 按了主菜单的「继续游戏」（ForceClick）");
            return press;
        }
        catch (Exception e)
        {
            press.Why = "按「继续游戏」失败：" + e.GetType().Name + "：" + e.Message;
            return press;
        }
    }

    /// <summary>
    /// 退路（主线程）：按主菜单 OnContinueButtonPressedAsync 的 IL 自己走一遍。
    /// 它是 async，（这里）只发起、不 await —— 主线程上 await 主线程任务会卡住泵。
    /// </summary>
    private static bool StartManualLoad()
    {
        try
        {
            var game = NGame.Instance;
            var run = RunManager.Instance;
            var manager = SaveManager.Instance;
            if (game == null || run == null || manager == null) return false;

            var read = manager.LoadRunSave();
            if (read == null || !read.Success || read.SaveData == null)
            {
                BridgeLog.Warn("reload: LoadRunSave 失败 status=" + (read?.Status.ToString() ?? "null") +
                               " err=" + (read?.ErrorMessage ?? ""));
                return false;
            }

            var save = read.SaveData;
            var state = RunState.FromSerializable(save);
            _ = LoadRunFromMakeup(run, game, state, save);
            BridgeLog.Info("reload: 走 manual-load（LoadRunSave → RunState.FromSerializable → SetUpSavedSingleplayer → NGame.LoadRun）");
            return true;
        }
        catch (Exception e)
        {
            BridgeLog.Error("reload: manual-load 失败", e);
            return false;
        }
    }

    /// <summary>manual-load 的后半段：先 SetUpSavedSingleplayer 再 LoadRun（与主菜单那条路同一个顺序）。</summary>
    private static async Task LoadRunFromMakeup(RunManager run, NGame game, RunState state, SerializableRun save)
    {
        try
        {
            await run.SetUpSavedSingleplayer(state, save);
            await game.LoadRun(state, save.PreFinishedRoom);
        }
        catch (Exception e)
        {
            BridgeLog.Error("reload: manual-load 的后续抛了", e);
        }
    }

    // =================================================================== 共用

    /// <summary>等 RunManager 真的进入进行中。</summary>
    private static async Task<bool> WaitRunStarted(int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += PollMs)
        {
            bool running;
            try { running = await BridgePump.OnMain(() => RunManager.Instance != null && RunManager.Instance.IsInProgress, 5000); }
            catch { running = false; }
            if (running) return true;
            await Task.Delay(PollMs);
        }
        return false;
    }

    private static readonly System.Reflection.BindingFlags AnyInstance =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    /// <summary>沿继承链读成员（字段优先；主菜单 / 界面上的东西都是 private 字段）。</summary>
    private static object ReadMemberStatic(object target, string name)
    {
        if (target == null) return null;
        for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            try
            {
                var field = t.GetField(name, AnyInstance | System.Reflection.BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
                var prop = t.GetProperty(name, AnyInstance | System.Reflection.BindingFlags.DeclaredOnly);
                if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(target);
            }
            catch { }
        }
        return null;
    }

    private static NMainMenu FindMainMenu() => FindFirstVisible<NMainMenu>();

    /// <summary>场景树里第一个可见的 T（必须在主线程跑）。</summary>
    private static T FindFirstVisible<T>() where T : Node
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 40000)
            {
                var node = stack.Pop();
                if (node == null) continue;
                if (node is T typed && IsUsable(typed)) return typed;
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return null;
    }

    private static bool IsUsable(Node node)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return false;
            if (!node.IsInsideTree()) return false;
            if (node is CanvasItem item && !item.IsVisibleInTree()) return false;
            if (node is NClickableControl control && !control.IsEnabled) return false;
            return true;
        }
        catch { return false; }
    }

    private static bool HasRunSave()
    {
        try { return SaveManager.Instance?.HasRunSave == true; }
        catch { return false; }
    }

    private static string CurrentRoom()
    {
        try { return RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom?.RoomType.ToString(); }
        catch { return null; }
    }

    /// <summary>读档进来之后的现场（房间 / 楼层 / 进阶），字段名和 /state 里的一致。</summary>
    private static Dictionary<string, object> DescribeRun()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null) return null;
            var dict = new Dictionary<string, object>
            {
                ["room"] = state.CurrentRoom?.RoomType.ToString(),
                ["act"] = state.Act?.Id?.ToString(),
                ["actFloor"] = state.ActFloor,
                ["totalFloor"] = state.TotalFloor,
                ["ascension"] = state.AscensionLevel,
            };
            try { dict["currentCoord"] = state.CurrentMapCoord is { } c ? (object)new Dictionary<string, object> { ["col"] = c.col, ["row"] = c.row } : null; }
            catch { }
            return dict;
        }
        catch { return null; }
    }

    /// <summary>在主线程读一个值；抛了就当 fallback。</summary>
    private static async Task<T> SafeOnMain<T>(Func<T> read, T fallback)
    {
        try { return await BridgePump.OnMain(read, 5000); }
        catch { return fallback; }
    }

    private static Dictionary<string, object> Fail(string message) =>
        new() { ["ok"] = false, ["error"] = message };
}
