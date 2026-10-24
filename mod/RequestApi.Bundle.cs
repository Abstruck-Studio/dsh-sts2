using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace Sts2AgentBridge;

/// <summary>
/// 选牌包通道（kind:"bundle"）：Act1 开局那种「选择一组牌 / 一包牌」的界面。
///
/// ---- 为什么 selector 通道接不住它 ----
/// 这一屏是 <c>CardSelectCmd.FromChooseABundleScreen(player, bundles)</c> 弹出来的，**IL 核实**它是
/// <c>NChooseABundleSelectionScreen.ShowScreen(bundles)</c> + <c>await screen.CardsSelected()</c>，
/// 全程没有问过 <c>CardSelectCmd.Selector</c>（只有 TestMode.IsOn 时直接取 bundles[0]）。
/// 所以只能像奖励 / 宝箱那样单独读界面 —— 但决策语义要按「一捆」算，不能按单张卡。
///
/// ---- 界面自己的结构（MetaPriv + IL 核对）----
///   * <c>_bundleRow</c>：Control，一排放着 N 个 <c>NCardBundle</c>（_Ready 里逐个 Create + AddChild）；
///   * <c>NCardBundle.Bundle</c>：IReadOnlyList&lt;CardModel&gt;，就是这一捆的牌；
///   * <c>NCardBundle.Hitbox</c>：NClickableControl，Released → NCardBundle.OnClicked → EmitSignal(Clicked)
///     → 界面的 <c>OnBundleClicked(NCardBundle)</c>；
///   * <c>OnBundleClicked</c>（每捆被点中时）：_selectedBundle = 这一捆；_bundleRow 隐藏；
///     _bundlePreviewContainer 显示、把牌飞进预览；**_previewCancelButton / _previewConfirmButton 这时才 Enable**；
///   * <c>_previewConfirmButton</c>：NConfirmButton（: NButton : NClickableControl），Released → ConfirmSelection；
///   * <c>ConfirmSelection(NButton)</c>：给每张牌播飞进牌组的 vfx，然后
///     <c>_completionSource.SetResult(_selectedBundle.Bundle)</c>；那个参数在 IL 里完全没用到；
///   * <c>CardsSelected()</c>：await <c>_completionSource.Task</c> 之后
///     <c>NOverlayStack.Instance.Remove(this)</c> —— 界面从浮层栈上摘掉。
///
/// ---- 所以「选中了一组」和「真的交出去了」是两个判据 ----
///   ① 选中：界面自己的 <c>_selectedBundle</c> **就是**这一捆（引用相等），且 √ 已经 Enable；
///   ② 成功：<c>_completionSource.Task</c> 完成，**并且**界面从场景树上消失
///      （IsShown == false；游戏自带 AutoSlay 的 ChooseABundleScreenHandler 也是等这个）。
///   只看「点过」不算成功：点过但界面还在 = 失败，请求会挂回去让人重答。
///
/// ---- 两阶段（都能重答）----
///   stage = "bundles"：候选 = 每一捆（标题是组内卡名摘要，detail 是整组清单）。
///                      应答 = 选中这一捆 → 需要确认就按界面自己的 √（NConfirmButton）。
///   stage = "confirm"：上一轮选中了但 √ 没按成（或者别人把它点进预览了）时的恢复态：
///                      唯一候选「确认这组」，应答 = 只按 √。
/// </summary>
public static partial class RequestApi
{
    private const string BundleKind = "bundle";
    private const string BundleRoute = "bundle";
    private const string BundleCaller = "NChooseABundleSelectionScreen";

    /// <summary>阶段 1：一排牌包还没选。</summary>
    private const string BundleStageBundles = "bundles";

    /// <summary>阶段 2：已经选中一捆、等按 √。</summary>
    private const string BundleStageConfirm = "confirm";

    /// <summary>阶段 2 唯一候选的标题（key = 标题#1）。</summary>
    private const string BundleConfirmTitle = "确认这组";

    private const string BundlePrompt =
        "选择一组牌：候选就是一个个牌包（detail 是组里的牌），选中之后要按 √ 确认";

    /// <summary>阶段 2 的 prompt（已经选中一捆，只剩确认）。</summary>
    private const string BundleConfirmPrompt = "选牌：这一组已经选中，按 √ 确认";

    /// <summary>按 √ 之后等界面真的关掉的预算。</summary>
    private const int BundleConfirmWaitMs = 5000;

    /// <summary>轮询界面的间隔。</summary>
    private const int BundlePollMs = 120;

    /// <summary>真的把答案交出去、界面也关掉时的 visual。</summary>
    private const string BundleVisualApplied = "bundle-confirmed";

    /// <summary>没走成时的 visual 占位。</summary>
    private const string BundleVisualNone = "bundle-none";

    /// <summary>给 /request 的 animation 字段配的说明。</summary>
    private const string BundleAnimationPlan =
        "应答时：点这一捆自己的 Hitbox（NClickableControl.ForceClick = 玩家点一下 → NCardBundle.OnClicked → "
        + "界面的 OnBundleClicked：记下 _selectedBundle、隐藏牌包行、把牌飞进预览），然后按界面自己的确认键 "
        + "_previewConfirmButton（NConfirmButton.ForceClick → 界面的 ConfirmSelection：播飞进牌组的 vfx + "
        + "_completionSource.SetResult），不点坐标；确认之后等界面真的从浮层栈上摘掉才返回 ok。";

    /// <summary>选牌包通道的待答请求。单槽：同一时刻只可能开着一个选牌包界面。</summary>
    private static PendingSelection _bundlePending;

    /// <summary>这一帧的选牌包现场。</summary>
    private sealed class BundleView
    {
        public NChooseABundleSelectionScreen Screen;
        public string Stage = BundleStageBundles;
        public string Prompt = BundlePrompt;
        public string Selected;
        public List<Candidate> Candidates = new();
    }

    // =================================================================== 发现

    /// <summary>
    /// 树里第一个可见的 <c>NChooseABundleSelectionScreen</c>（必须在主线程调用）。
    /// 按**具体类型**找：这个界面没有派生类，模糊匹配反而会撞上选牌网格。
    /// </summary>
    private static NChooseABundleSelectionScreen FindBundleScreen()
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
                if (node is NChooseABundleSelectionScreen screen && IsShown(screen)) return screen;
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn("bundle: 找选牌包界面失败（" + e.GetType().Name + "）：" + e.Message);
        }
        return null;
    }

    /// <summary>一排里的每一捆（按界面里的左右顺序；容器读不到就退化成扫整棵子树）。</summary>
    private static List<NCardBundle> BundleNodes(NChooseABundleSelectionScreen screen)
    {
        var list = new List<NCardBundle>();
        if (screen == null) return list;

        var root = SafeModel(() => ReadMember(screen, "_bundleRow") as Node) ?? screen;
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 4000)
        {
            var node = stack.Pop();
            if (node == null) continue;
            if (node is NCardBundle bundle) list.Add(bundle);
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        // 牌包按场景里的位置排：key 的序号才稳定，不会因为遍历顺序变了而换号。
        list.Sort((a, b) =>
        {
            float ax = 0f, ay = 0f, bx = 0f, by = 0f;
            try { ax = a.GlobalPosition.X; ay = a.GlobalPosition.Y; } catch { }
            try { bx = b.GlobalPosition.X; by = b.GlobalPosition.Y; } catch { }
            var dy = ay.CompareTo(by);
            return dy != 0 ? dy : ax.CompareTo(bx);
        });
        return list;
    }

    /// <summary>这一捆的牌（<c>NCardBundle.Bundle</c>，组内 CardModel 列表）。</summary>
    private static IReadOnlyList<CardModel> BundleCards(NCardBundle bundle)
    {
        try { return bundle?.Bundle; } catch { return null; }
    }

    /// <summary>组内卡名（带 "+" 表示已升级）。<paramref name="max"/> ≤ 0 表示不截断。</summary>
    private static List<string> BundleCardNames(IReadOnlyList<CardModel> cards, int max)
    {
        var names = new List<string>();
        if (cards == null) return names;
        foreach (var card in cards)
        {
            if (card == null) continue;
            var title = CardTitle(card);
            if (SafeUpgraded(card)) title += "+";
            names.Add(title);
            if (max > 0 && names.Count >= max) break;
        }
        return names;
    }

    /// <summary>标题：组内卡名用「、」连起来（最多 3 个，超了加省略号）。同名包靠 #序号 区分。</summary>
    private static string BundleTitle(IReadOnlyList<CardModel> cards)
    {
        var names = BundleCardNames(cards, 3);
        if (names.Count == 0) return "一捆";
        var text = string.Join("、", names);
        var total = 0;
        try { total = cards?.Count ?? 0; } catch { }
        if (total > names.Count) text += "…";
        return text;
    }

    /// <summary>detail：整组清单 + 张数（agent 靠它决定要哪一捆）。</summary>
    private static string BundleDetail(IReadOnlyList<CardModel> cards)
    {
        var names = BundleCardNames(cards, 8);
        var total = 0;
        try { total = cards?.Count ?? 0; } catch { }
        if (names.Count == 0) return total > 0 ? $"共 {total} 张（卡名读不到）" : "空包";
        var text = $"共 {total} 张：" + string.Join("、", names);
        if (total > names.Count) text += " …";
        return text;
    }

    /// <summary>读一帧现场（必须在主线程调用）。没有界面就是 null。</summary>
    private static BundleView ReadBundleView()
    {
        var screen = FindBundleScreen();
        if (screen == null) return null;

        var view = new BundleView { Screen = screen };

        var selected = SafeModel(() => ReadMember(screen, "_selectedBundle") as NCardBundle);
        var confirm = SafeModel(() => ReadMember(screen, "_previewConfirmButton") as NClickableControl);
        var confirmReady = confirm != null && SafeValid(confirm) && confirm.IsInsideTree() && IsShown(confirm);

        // ---- 阶段 2：已经选中一捆、√ 也亮了 → 只剩「按 √」（上一轮没按成 / 被点进预览时的恢复态）----
        if (selected != null && confirmReady && SafeEnabled(confirm))
        {
            var cards = BundleCards(selected);
            view.Stage = BundleStageConfirm;
            view.Prompt = BundleConfirmPrompt;
            view.Candidates.Add(new Candidate
            {
                Key = BundleConfirmTitle + "#1",
                Title = BundleConfirmTitle,
                Upgraded = false,
                Cost = -1,
                Enabled = true,
                MenuButton = confirm,
                MenuStage = BundleStageConfirm,
                Detail = "已选：" + BundleDetail(cards),
                BundleRef = selected,
            });
            view.Selected = view.Candidates[0].Key;
            return view;
        }

        // ---- 阶段 1：一排牌包 ----
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bundle in BundleNodes(screen))
        {
            if (bundle == null) continue;
            var cards = BundleCards(bundle);
            var hitbox = SafeModel(() => bundle.Hitbox);

            var candidate = new Candidate
            {
                Key = NextKey(counts, BundleTitle(cards)),
                Title = BundleTitle(cards),
                Upgraded = false,
                Cost = -1,
                Enabled = hitbox == null || SafeEnabled(hitbox),
                MenuButton = hitbox,
                MenuStage = BundleStageBundles,
                Detail = BundleDetail(cards),
                BundleRef = bundle,
            };
            view.Candidates.Add(candidate);
        }

        if (view.Candidates.Count == 0) return view;      // 一捆都没读到：交给调用方判空
        return view;
    }

    // =================================================================== /request

    private static string BundleSignature(BundleView view)
    {
        if (view == null) return "";
        var parts = new List<string> { view.Stage ?? "", view.Selected ?? "" };
        foreach (var c in view.Candidates) parts.Add(c.Key);
        return string.Join("|", parts);
    }

    /// <summary>
    /// 选牌包界面的发现（必须在主线程调用）。界面不在 / 一捆都没读到 → 返回 null 并清掉 pending。
    /// </summary>
    private static Dictionary<string, object> DescribeBundlePending()
    {
        // 选牌包界面只可能出现在对局里：没在跑一局、又没有挂着的请求时，连树都不用扫。
        if (_bundlePending == null && !RunInProgress()) return null;

        BundleView view;
        try { view = ReadBundleView(); }
        catch (Exception e)
        {
            BridgeLog.Warn("bundle: 读界面失败（" + e.GetType().Name + "）：" + e.Message);
            DropBundlePending("读界面失败：" + e.Message);
            return null;
        }

        if (view == null || view.Candidates.Count == 0)
        {
            DropBundlePending(view == null ? "场景树里没有可见的 NChooseABundleSelectionScreen" : "界面上一捆牌都没读到");
            return null;
        }

        var signature = BundleSignature(view);
        var state = _bundlePending;
        if (state == null || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
        {
            state = new PendingSelection
            {
                Id = NextId(),
                Min = 1,                 // 一次只能交一捆
                Max = 1,
                Kind = BundleKind,
                Caller = BundleCaller,
                Prompt = view.Prompt,
                Cards = view.Candidates,
                Screen = view.Screen,
                CreatedUtc = DateTime.UtcNow,
            };
            state.Signature = signature;
            _bundlePending = state;
            BridgeLog.Info($"bundle: 新请求 id={state.Id} stage={view.Stage} " +
                           $"keys={DescribeMenuKeys(view.Candidates)}");
        }
        else
        {
            // 同一屏：只刷新节点引用（界面重建会换实例）和提示语，id 不变。
            state.Cards = view.Candidates;
            state.Prompt = view.Prompt;
            state.Caller = BundleCaller;
            state.Screen = view.Screen;
            state.Min = 1;
            state.Max = 1;
        }

        return DescribeBundlePendingJson(state, view);
    }

    private static Dictionary<string, object> DescribeBundlePendingJson(PendingSelection pending, BundleView view)
    {
        var confirmStage = view != null
            ? string.Equals(view.Stage, BundleStageConfirm, StringComparison.Ordinal)
            : pending.Cards.Count > 0
              && string.Equals(pending.Cards[0].MenuStage, BundleStageConfirm, StringComparison.Ordinal);
        var dict = new Dictionary<string, object>
        {
            ["id"] = pending.Id,
            ["kind"] = BundleKind,
            ["stage"] = confirmStage ? BundleStageConfirm : BundleStageBundles,
            ["prompt"] = pending.Prompt ?? BundlePrompt,
            ["min"] = pending.Min,
            ["max"] = pending.Max,
            // 选中一捆之后界面会亮出 √：这一条是「需要确认」的真值（阶段 2 就是在按它）。
            ["requireConfirm"] = true,
            // 选「取消」只回到牌包行，不会让 CardsSelected() 返回，所以这次请求不能放弃。
            ["cancelable"] = false,
            ["caller"] = pending.Caller ?? BundleCaller,
            ["confidence"] = "high",          // 候选来自 NCardBundle.Bundle（组内 CardModel），不是猜坐标
            ["animated"] = true,              // 播的是游戏自己的「飞进预览 + 飞进牌组」
            ["animation"] = BundleAnimationPlan,
            ["route"] = BundleRoute,
            ["candidates"] = pending.Cards.Select(BundleCandidateJson).ToList(),
        };

        if (!string.IsNullOrEmpty(view?.Selected)) dict["selected"] = view.Selected;
        return dict;
    }

    /// <summary>
    /// 选牌包候选的 JSON：比别的通道多带组内牌名清单（cards）和张数（count），
    /// 因为「一捆」的语义不可能靠一个标题说清。
    /// </summary>
    private static Dictionary<string, object> BundleCandidateJson(Candidate c)
    {
        var cards = BundleCardNames(BundleCards(c.BundleRef), 0);
        var dict = new Dictionary<string, object>
        {
            ["key"] = c.Key,
            ["title"] = c.Title ?? "",
            ["upgraded"] = c.Upgraded,
            ["cost"] = -1,
            ["enabled"] = c.Enabled,
            ["itemType"] = string.Equals(c.MenuStage, BundleStageConfirm, StringComparison.Ordinal) ? "Confirm" : "Bundle",
            ["detail"] = c.Detail ?? "",
            ["cards"] = cards,
            ["count"] = cards.Count,
        };
        if (c.MenuButton != null) dict["buttonType"] = c.MenuButton.GetType().Name;
        return dict;
    }

    /// <summary>把待答的选牌包请求摘掉（不动游戏状态）。</summary>
    private static void DropBundlePending(string why)
    {
        var pending = _bundlePending;
        if (pending == null) return;
        _bundlePending = null;
        BridgeLog.Info($"bundle: 清掉请求 id={pending.Id}（{why}）");
    }

    /// <summary>给 Batch 用：现在有没有挂着待答的选牌包请求（它同样意味着「游戏在等玩家选东西」）。</summary>
    internal static bool HasPendingBundleRequest() => _bundlePending != null;

    /// <summary>给 Batch 用：挂着的那条选牌包请求 id（没有就是 null）。</summary>
    internal static long? PendingBundleRequestId() => _bundlePending?.Id;

    /// <summary>ok:false 时把请求挂回槽里，让 agent 改一道答案再答同一个 id。</summary>
    private static void RestoreBundleAfterReject(PendingSelection pending)
    {
        if (pending == null) return;
        if (_lastAnsweredId == pending.Id) _lastAnsweredId = -1;
        _bundlePending = pending;
        BridgeLog.Info($"bundle: 请求 id={pending.Id} 没被接受，恢复成可再答（已挂回 _bundlePending）");
    }

    // =================================================================== /respond

    /// <summary>
    /// 包裹层：校验 → 现场重读界面 → 交给核心体去选 + 按 √。任何 ok:false 都能重答同一个 id。
    /// </summary>
    private static async Task<Dictionary<string, object>> AnswerBundle(PendingSelection pending, string[] picks)
    {
        picks ??= Array.Empty<string>();
        var failure = Validate(pending.Min, pending.Max, pending.Cards, picks, out var chosen, "项");
        if (failure != null) return failure;

        BundleView view;
        try { view = await BridgePump.OnMain(ReadBundleView); }
        catch (Exception e) { return Fail("读取选牌包界面失败：" + e.Message); }
        if (view == null)
            return Fail("选牌包界面已经不在场景树里了（可能已经交出去了，重新 GET /request）");
        if (view.Candidates.Count == 0)
            return Fail("选牌包界面还在，但一捆牌都没读到（牌包可能还没摆好：稍等再 GET /request）");

        var key = chosen[0].Key;
        var fresh = view.Candidates.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
        if (fresh == null)
            return Fail($"{key} 不在当前选牌包的候选里（可选：{DescribeMenuKeys(view.Candidates)}）");

        var sw = Stopwatch.StartNew();

        // 先摘掉：操作过程中不能再被 /request 看到，也不能被答第二次。
        _bundlePending = null;
        _lastAnsweredId = pending.Id;
        _animating = true;

        Dictionary<string, object> result;
        try
        {
            result = await ApplyBundleChoice(pending, fresh, view, sw);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"bundle: 应答 id={pending.Id} 抛异常（{e.GetType().Name}）：{e.Message}");
            result = Fail("选牌包操作出错：" + e.Message);
        }
        finally { _animating = false; }

        if (!IsTrue(result, "ok")) RestoreBundleAfterReject(pending);
        return result;
    }

    /// <summary>
    /// 核心体：① 阶段 2 直接按 √；② 阶段 1 先选中那一捆（核验 _selectedBundle 就是它、√ 亮了），再按 √；
    /// ③ 等 _completionSource 完成 **并且** 界面从场景树上消失才算成功。
    /// </summary>
    private static async Task<Dictionary<string, object>> ApplyBundleChoice(
        PendingSelection pending, Candidate chosen, BundleView view, Stopwatch sw)
    {
        var confirmStage = string.Equals(view.Stage, BundleStageConfirm, StringComparison.Ordinal);
        var bundle = chosen.BundleRef;
        var bundleCards = BundleCards(bundle);

        var how = "bundle-confirm-only";
        if (!confirmStage)
        {
            if (bundle == null || !SafeValid(bundle))
                return Fail($"{chosen.Key} 那一捆已经不在场景树里了（重新 GET /request）");

            var pick = await BridgePump.OnMain(() => SelectBundle(view.Screen, bundle));
            if (!pick.Ok) return Fail(pick.Why ?? $"{chosen.Key} 没选中");
            how = pick.How;

            // 「选中了一组」的真值：界面自己记下的 _selectedBundle 就是这一捆，且 √ 已经 Enable。
            var confirmedState = await BridgePump.OnMain(() => BundleSelectionState(view.Screen, bundle));
            if (!confirmedState.Selected)
                return Fail($"点了 {chosen.Key}（{pick.How}），但界面没有把它记成 _selectedBundle");
            if (!confirmedState.ConfirmReady)
                return Fail($"{chosen.Key} 已经选中，但界面上的 √ 没亮（{confirmedState.Why ?? "确认键不可用"}）");

            // 让「这一组被选中」在屏幕上看得见（牌正从牌包飞进预览），再按 √。
            await Task.Delay(220);
        }

        // 按界面自己的确认键（NConfirmButton；拿不到控件才退化成调它的 ConfirmSelection）。
        var press = await BridgePump.OnMain(() => PressBundleConfirm(view.Screen));
        if (!press.Ok) return Fail(press.Why ?? "确认键没按下去");

        var closed = await WaitBundleClosed(view.Screen);
        var waited = sw.ElapsedMilliseconds;
        if (!closed)
        {
            return Fail($"{chosen.Key} 选中了、√ 也按了（{press.How}），" +
                        $"但选牌包界面在 {BundleConfirmWaitMs}ms 内没有关掉（答案可能没交出去）");
        }

        BridgeLog.Info($"bundle answered id={pending.Id} key={chosen.Key} how={how}+{press.How} " +
                       $"cards=[{string.Join("、", BundleCardNames(bundleCards, 0))}] waitedMs={waited}");

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["source"] = "screen",
            ["route"] = BundleRoute,
            ["stage"] = confirmStage ? BundleStageConfirm : BundleStageBundles,
            ["applied"] = chosen.Key,
            ["how"] = how + "+" + press.How,
            ["animated"] = true,
            ["waitedMs"] = waited,
            ["visual"] = BundleVisualApplied,
            ["bundle"] = BundleCardNames(bundleCards, 0),
            ["count"] = BundleCardNames(bundleCards, 0).Count,
            ["selected"] = true,
            ["confirmed"] = true,
            ["screenClosed"] = true,      // 界面真的从浮层栈上摘掉了（不是「点过」）
            ["settled"] = true,
        };
        return result;
    }

    /// <summary>
    /// 选中这一捆（必须在主线程跑）。先走真实点击入口 <c>NCardBundle.Hitbox.ForceClick()</c>
    /// （= NClickableControl 的 OnRelease + EmitSignal(Released) → OnClicked → 界面的 OnBundleClicked），
    /// 界面没记下这一捆时再退一步直接调 <c>OnBundleClicked(NCardBundle)</c>（同一段代码，只是绕开信号）。
    /// </summary>
    private static (bool Ok, string How, string Why) SelectBundle(NChooseABundleSelectionScreen screen, NCardBundle bundle)
    {
        if (!SafeValid(screen) || !screen.IsInsideTree())
            return (false, BundleVisualNone, "选牌包界面已经不在场景树里了");
        if (!SafeValid(bundle) || !bundle.IsInsideTree())
            return (false, BundleVisualNone, "这一捆已经不在场景树里了");

        var how = (string)null;
        try
        {
            var hitbox = SafeModel(() => bundle.Hitbox);
            if (hitbox != null && SafeValid(hitbox) && hitbox.IsInsideTree() && IsShown(hitbox))
            {
                if (!SafeEnabled(hitbox))
                    return (false, BundleVisualNone, "这一捆的 Hitbox 当前不可点（IsEnabled=false）");
                hitbox.ForceClick();
                how = "bundle-hitbox-force-click";
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"bundle: Hitbox.ForceClick 失败（{e.GetType().Name}）：{e.Message}");
        }

        if (!ReferenceEquals(SafeModel(() => ReadMember(screen, "_selectedBundle")), bundle))
        {
            var method = FindMethod(screen.GetType(), "OnBundleClicked", 1);
            if (method != null)
            {
                try
                {
                    method.Invoke(screen, new object[] { bundle });
                    how ??= "screen-onbundleclicked";
                }
                catch (Exception e)
                {
                    return (false, BundleVisualNone,
                        "调界面的 OnBundleClicked 失败（" + e.GetType().Name + "）：" + e.Message);
                }
            }
        }

        if (!ReferenceEquals(SafeModel(() => ReadMember(screen, "_selectedBundle")), bundle))
            return (false, BundleVisualNone, "点了这一捆，但界面没有把它记成 _selectedBundle");

        return (true, how ?? "screen-onbundleclicked", null);
    }

    /// <summary>「这一捆选中了吗 / √ 亮了吗」的现场（必须在主线程跑）。</summary>
    private static (bool Selected, bool ConfirmReady, string Why) BundleSelectionState(
        NChooseABundleSelectionScreen screen, NCardBundle bundle)
    {
        try
        {
            if (!SafeValid(screen) || !screen.IsInsideTree())
                return (false, false, "选牌包界面已经不在场景树里了");

            var selected = ReadMember(screen, "_selectedBundle") as NCardBundle;
            if (!ReferenceEquals(selected, bundle))
                return (false, false, "_selectedBundle 不是这一捆");

            var confirm = ReadMember(screen, "_previewConfirmButton") as NClickableControl;
            if (confirm == null || !SafeValid(confirm) || !confirm.IsInsideTree())
                return (true, false, "界面里没有 _previewConfirmButton");
            if (!IsShown(confirm)) return (true, false, "√ 当前不可见");
            if (!SafeEnabled(confirm)) return (true, false, "√ 当前不可用（IsEnabled=false）");
            return (true, true, null);
        }
        catch (Exception e)
        {
            return (false, false, "读选中状态失败：" + e.Message);
        }
    }

    /// <summary>
    /// 按界面自己的确认键（必须在主线程跑）：<c>_previewConfirmButton</c>（NConfirmButton）的
    /// <c>ForceClick()</c> = Released → 界面的 <c>ConfirmSelection</c>。
    /// 控件拿不到才退化成反射调 <c>ConfirmSelection(NButton)</c>（IL 里那个参数完全没用到，传 null 就行）。
    /// </summary>
    private static (bool Ok, string How, string Why) PressBundleConfirm(NChooseABundleSelectionScreen screen)
    {
        if (!SafeValid(screen) || !screen.IsInsideTree())
            return (false, BundleVisualNone, "选牌包界面已经不在场景树里了");

        var confirm = SafeModel(() => ReadMember(screen, "_previewConfirmButton") as NClickableControl);
        if (confirm != null && SafeValid(confirm) && confirm.IsInsideTree())
        {
            if (!IsShown(confirm))
                return (false, BundleVisualNone, "界面上的确认键（NConfirmButton）当前不可见");
            if (!SafeEnabled(confirm))
                return (false, BundleVisualNone, "确认键还没亮（要先选中一捆）");
            try
            {
                confirm.ForceClick();
                return (true, "confirm-button-force-click", null);
            }
            catch (Exception e)
            {
                BridgeLog.Warn($"bundle: 确认键 ForceClick 失败（{e.GetType().Name}）：{e.Message}");
            }
        }

        var method = FindMethod(screen.GetType(), "ConfirmSelection", 1);
        if (method == null)
            return (false, BundleVisualNone, "既没有 _previewConfirmButton，也没有 ConfirmSelection(NButton)");
        try
        {
            method.Invoke(screen, new object[] { null });
            return (true, "screen-confirmselection", null);
        }
        catch (Exception e)
        {
            return (false, BundleVisualNone, "调界面的 ConfirmSelection 失败（" + e.GetType().Name + "）：" + e.Message);
        }
    }

    /// <summary>
    /// 等界面真的交出去：<c>_completionSource.Task</c> 完成，并且界面从场景树上消失
    /// （CardsSelected() 里 NOverlayStack.Remove 之后 IsVisibleInTree 才是 false）。
    /// </summary>
    private static async Task<bool> WaitBundleClosed(NChooseABundleSelectionScreen screen)
    {
        for (var waited = 0; waited < BundleConfirmWaitMs; waited += BundlePollMs)
        {
            bool gone;
            try
            {
                gone = await BridgePump.OnMain(() =>
                {
                    if (!SafeValid(screen)) return true;          // 节点被释放：界面已经没了
                    if (!IsShown(screen)) return true;
                    return BundleCompletionDone(screen);
                });
            }
            catch { gone = true; }
            if (gone) return true;
            await Task.Delay(BundlePollMs);
        }
        return false;
    }

    /// <summary>界面自己的 <c>_completionSource.Task</c> 完成了吗（= CardsSelected() 已能返回）。</summary>
    private static bool BundleCompletionDone(NChooseABundleSelectionScreen screen)
    {
        try
        {
            var tcs = ReadMember(screen, "_completionSource");
            if (tcs == null) return false;
            return ReadMember(tcs, "Task") is Task task && task.IsCompleted;
        }
        catch { return false; }
    }
}
