using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Sts2AgentBridge;

/// <summary>
/// <b>调试 / 紧急兜底用</b>的通用界面工具：找出当前屏幕上每一个可见控件（原生按钮、
/// 游戏的 NClickableControl 派生类、以及名字像按钮的东西），让菜单、角色选择、以及
/// 「先选再确认」的对话框都能被统一驱动，而不是猜像素坐标。
///
/// 正常流程**不要**用这里的 <see cref="Tap"/> / <see cref="SelectWait"/> / <see cref="SelectCard"/>：
/// 所有决策都走 <c>GET /request</c> + <c>POST /respond</c>（语义请求协议，见 <see cref="RequestApi"/>），
/// 那条路不点坐标。这里保留的意义只有一个：菜单页万一协议没覆盖，还能手动兜底
/// （对应调试路由 <c>/screen</c>、<c>/tap</c>、<c>/click</c>、<c>/click_at</c>、<c>/drag</c>、<c>/key</c>）。
/// </summary>
public static class ScreenApi
{
    private static readonly string[] ConfirmWords = { "确认", "確定", "确定", "完成", "继续", "下一步", "确定选择", "选择", "OK", "Confirm", "Done", "✓", "√", "✔" };

    /// <summary>
    /// 当前屏幕的身份：优先读界面节点上的 ScreenType 枚举（联机层用来同步「开着什么界面」），
    /// 这样调用方不必去猜类名。
    /// </summary>
    public static string CurrentScreenType()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 5000)
            {
                var node = stack.Pop();
                var prop = node.GetType().GetProperty(
                    "ScreenType",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.PropertyType.IsEnum)
                {
                    try
                    {
                        var value = prop.GetValue(node);
                        if (value != null && value.ToString() != "None") return value.ToString();
                    }
                    catch { }
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return null;
    }

    public static Dictionary<string, object> Inspect()
    {
        try
        {
            var items = new List<object>();
            var seenLabels = new HashSet<string>();
            var screenType = CurrentScreenType();
            foreach (var entry in Clickables())
            {
                var c = entry.Control;
                var item = new Dictionary<string, object>
                {
                    ["uid"] = c.GetInstanceId(),
                    ["kind"] = entry.Kind,
                    ["text"] = Clean(TextOf(c)),
                    ["x"] = c.GlobalPosition.X,
                    ["y"] = c.GlobalPosition.Y,
                    ["sx"] = c.GetScreenPosition().X,
                    ["sy"] = c.GetScreenPosition().Y,
                    ["w"] = c.Size.X,
                    ["h"] = c.Size.Y,
                };

                // Card-choice screens render cards as textless controls; expose the card itself
                // so the agent can tell 打击 from 防御 instead of guessing by position.
                try
                {
                    var card = Choices.CardOf(c);
                    if (card != null)
                    {
                        item["card"] = card.Title;
                        item["upgraded"] = card.IsUpgraded;
                    }
                }
                catch { }

                // Shop slots and character-select tiles are textless too. Resolve a readable
                // label from the model behind them so the agent knows what it is buying or who
                // it is picking instead of guessing by position.
                if (!item.ContainsKey("text") || string.IsNullOrEmpty(item["text"] as string))
                {
                    var label = ModelLabel(c);
                    if (!string.IsNullOrEmpty(label))
                    {
                        // 只对「角色选择页」做去重：那一页最后一格是随机，背后挂的角色模型会重名。
                        // 手牌/牌堆里同名卡是正常的（两张打击），绝不能改名。
                        if (c.GetType().Name.Contains("Character", StringComparison.OrdinalIgnoreCase)
                            && seenLabels.Contains(label)) label = "random";
                        seenLabels.Add(label);
                        item["label"] = label;
                    }
                }

                items.Add(item);
            }

            // 画布外的控件标出来：agent 以前分不清「叠放的另一层」和「同一层的屏幕外元素」。
            var vpSize = new Vector2(1920, 1080);
            try
            {
                if (Engine.GetMainLoop() is SceneTree st)
                {
                    var size = st.Root.Size;
                    vpSize = new Vector2(size.X, size.Y);
                }
            }
            catch { }
            foreach (var raw in items)
            {
                if (raw is not Dictionary<string, object> item) continue;
                try
                {
                    var x = Convert.ToDouble(item.TryGetValue("x", out var xv) ? xv : 0);
                    var y = Convert.ToDouble(item.TryGetValue("y", out var yv) ? yv : 0);
                    var w = Convert.ToDouble(item.TryGetValue("w", out var wv) ? wv : 0);
                    var h = Convert.ToDouble(item.TryGetValue("h", out var hv) ? hv : 0);
                    if (x + w < 0 || y + h < 0 || x > vpSize.X || y > vpSize.Y) item["offscreen"] = true;
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["screen"] = CurrentScreen(),
                ["screenType"] = screenType,
                ["layerStack"] = LayerStack(),
                ["canvas"] = new Dictionary<string, object> { ["w"] = vpSize.X, ["h"] = vpSize.Y },
                ["count"] = items.Count,
                ["items"] = items,
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// True when the game is showing a card-selection overlay (choice screen). Playing cards
    /// like True Grit+ opens one mid-combat: the action queue is paused until a card is picked,
    /// so the batch must hand control back instead of treating the play as failed.
    /// </summary>
    public static bool HasCardSelectScreen()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return false;

            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 30000)
            {
                var node = stack.Pop();
                // 必须沿继承链看：NDeckUpgradeSelectScreen / NDeckCardSelectScreen /
                // NSimpleCardSelectScreen 这些派生类名本身并不含 "CardGridSelectionScreen"，
                // 只匹配运行时类名会全部漏掉（升级选牌就是这样漏的）。
                for (var t = node.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    var name = t.Name;
                    if (name.Contains("CardSelectScreen", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("ChooseACardSelection", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("ChooseABundleSelection", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("CardGridSelectionScreen", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("CardRewardSelectionScreen", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("DeckUpgradeSelectScreen", StringComparison.OrdinalIgnoreCase))
                    {
                        if (node.IsInsideTree()) return true;
                    }
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Leaves the current room through the game's own action (RunManager.Proceed) instead of
    /// clicking the 前进 button, which only reacts while the room is finished and in view.
    /// </summary>
    /// <summary>
    /// 离开当前房间（前进 / 继续 / 跳过）。
    /// 只认一种东西：<see cref="MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton"/> 这个类型，
    /// 找到就调它自己的 <c>ForceClick()</c>（= 玩家点一下）。不按文字猜、不按类名模糊匹配。
    /// </summary>
    public static Dictionary<string, object> Proceed()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return Fail("没有场景树");

            MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton proceed = null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000)
            {
                var node = stack.Pop();
                if (node is MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton button)
                {
                    proceed = button;
                    break;
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }

            if (proceed == null) return Fail("当前界面没有 NProceedButton（这个房间不是用前进按钮离开的）");
            if (!proceed.IsInsideTree()) return Fail("NProceedButton 不在场景树里");
            if (!proceed.Visible) return Fail("NProceedButton 当前不可见");
            if (!proceed.IsEnabled)
            {
                // 商店面板开着时前进键是禁用的（面板是模态的）。给一句能直接照做的话，
                // 别让 agent 以为「条件还没满足」只能干等。
                var shopOpen = GameApi.ShopInventoryOpen();
                return Fail("NProceedButton 当前不可用（条件还没满足）"
                    + (shopOpen
                        ? "：商店面板还开着 → 先 sts2_respond picks:[]（或选「离开商店#1」）关掉面板，再推进"
                        : ""));
            }

            proceed.ForceClick();
            BridgeLog.Info("proceed via NProceedButton.ForceClick");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["method"] = "NProceedButton.ForceClick",
                ["text"] = Clean(TextOf(proceed)),
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// 打开地图页：点顶栏的 <c>NTopBarMapButton</c>（玩家开地图就是点它）。
    /// `move` 只在地图页有效，所以离开房间之后的正确顺序是「关面板 → 开地图页 → move」。
    /// </summary>
    internal static Dictionary<string, object> OpenMapPage()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return Fail("没有场景树");

            var map = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            if (map != null && GodotObject.IsInstanceValid(map) && map.IsOpen)
                return new Dictionary<string, object> { ["ok"] = true, ["method"] = "already-open" };

            MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarMapButton button = null;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000)
            {
                var node = stack.Pop();
                if (node is MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarMapButton b)
                {
                    button = b;
                    break;
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }

            if (button == null) return Fail("没有 NTopBarMapButton（顶栏地图按钮）");
            if (!button.IsInsideTree()) return Fail("NTopBarMapButton 不在场景树里");
            try { if (!button.IsVisibleInTree()) return Fail("NTopBarMapButton 不可见"); } catch { }

            button.ForceClick();
            BridgeLog.Info("open map via NTopBarMapButton.ForceClick");
            return new Dictionary<string, object> { ["ok"] = true, ["method"] = "NTopBarMapButton.ForceClick" };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>地图页是不是开着（给 /proceed 的两段式确认用）。</summary>
    internal static bool IsMapOpen()
    {
        try
        {
            var map = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            return map != null && GodotObject.IsInstanceValid(map) && map.IsOpen;
        }
        catch { return false; }
    }

    /// <summary>当前房间名（给 /proceed 判断「点了到底有没有变化」）。</summary>
    internal static string CurrentRoomName()
    {
        try
        {
            var room = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom;
            return room?.RoomType.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 栈上有哪些「界面层」（按场景树顺序，不是严格 z 序）：地图 / 奖励屏 / 商店面板 / 牌库 / 选牌网格。
    /// 只报**可见且确实打开**的，压缩掉不可见的旧节点。
    /// </summary>
    internal static List<string> LayerStack()
    {
        var layers = new List<string>();
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return layers;
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 40000)
            {
                var node = stack.Pop();
                var name = LayerName(node);
                if (name != null && !layers.Contains(name)) layers.Add(name);
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return layers;
    }

    private static string LayerName(Node node)
    {
        try
        {
            if (node is not CanvasItem item || !item.IsInsideTree() || !item.IsVisibleInTree()) return null;
            if (node is MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen map && map.IsOpen) return "NMapScreen";
            if (node is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen) return "NRewardsScreen";
            if (node is MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory inv && inv.IsOpen) return "NMerchantInventory";
            if (node is MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen) return "NDeckViewScreen";
            if (node is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardGridSelectionScreen) return "NCardGridSelectionScreen";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 关掉栈顶那一层覆层（地图 / 奖励屏 / 商店面板 / 牌库）。走游戏自己的入口，不点坐标：
    /// 商店→面板返回键、奖励屏→它自己的跳过/继续键、地图→再点一次顶栏地图按钮、牌库→capstone.Close()。
    /// </summary>
    internal static Dictionary<string, object> CloseOverlay()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return Fail("没有场景树");
            var layers = LayerStack();
            if (layers.Count == 0) return Fail("没有检测到开着的覆层（layerStack 为空）");

            var top = layers[layers.Count - 1];
            if (top == "NMerchantInventory")
            {
                var inv = FindFirstNode<MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory>(tree.Root);
                var back = inv == null ? null : FindBackButton(inv);
                if (back != null)
                {
                    back.ForceClick();
                    return OkResult("NMerchantInventory", "back-button");
                }
                return Fail("商店面板开着，但没找到它的返回键");
            }
            if (top == "NRewardsScreen")
            {
                var screen = FindFirstNode<MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen>(tree.Root);
                var btn = screen == null ? null : RequestApi.FindRewardsLeaveControl(screen);
                if (btn != null)
                {
                    btn.ForceClick();
                    return OkResult("NRewardsScreen", "skip-button");
                }
                return Fail("奖励屏开着，但没找到跳过/继续键");
            }
            if (top == "NMapScreen")
            {
                var r = OpenMapPage();        // 顶栏地图按钮是 toggle：再点一次就关
                return r;
            }
            if (top == "NDeckViewScreen")
            {
                var container = FindFirstNode<MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer>(tree.Root);
                if (container != null)
                {
                    container.Close();
                    return OkResult("NDeckViewScreen", "capstone-close");
                }
                return Fail("牌库开着，但没有 NCapstoneContainer");
            }
            if (top == "NCardGridSelectionScreen")
            {
                PressKey(4194305);            // ESC：让游戏自己取消这层选择界面
                return OkResult("NCardGridSelectionScreen", "escape");
            }
            return Fail("不知道怎么关这一层：" + top);
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    private static Dictionary<string, object> OkResult(string layer, string how) => new()
    {
        ["ok"] = true,
        ["closed"] = layer,
        ["how"] = how,
        ["layerStack"] = LayerStack(),
    };

    private static T FindFirstNode<T>(Node root) where T : Node
    {
        if (root == null) return null;
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node is T hit) return hit;
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>某棵子树里第一个可见的 NBackButton（面板自己的返回键）。</summary>
    internal static NClickableControl FindBackButton(Node root)
    {
        if (root == null) return null;
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();
            if (node is MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton back)
            {
                try { if (back.IsVisibleInTree()) return back; } catch { }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }

    /// <summary>Press and release a key (by Godot keycode) on the game viewport.</summary>
    public static Dictionary<string, object> PressKey(int keycode)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return Fail("no scene tree");
            var viewport = tree.Root;

            var down = new InputEventKey
            {
                Keycode = (Key)keycode,
                PhysicalKeycode = (Key)keycode,
                Pressed = true,
            };
            Input.ParseInputEvent(down);

            var timer = tree.CreateTimer(0.08);
            timer.Timeout += () =>
            {
                try
                {
                    Input.ParseInputEvent(new InputEventKey
                    {
                        Keycode = (Key)keycode,
                        PhysicalKeycode = (Key)keycode,
                        Pressed = false,
                    });
                }
                catch { }
            };

            BridgeLog.Info($"pressed key {keycode}");
            return new Dictionary<string, object> { ["ok"] = true, ["key"] = keycode };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    public static Dictionary<string, object> Tap(ulong uid)
    {
        try
        {
            foreach (var entry in Clickables())
            {
                var c = entry.Control;
                if (c.GetInstanceId() != uid) continue;

                // Screen coordinates include canvas-layer transforms; GlobalPosition does not,
                // which is why clicks on bottom-anchored cards landed outside the viewport.
                var origin = c.GetScreenPosition();
                var rect = new Rect2(origin, c.Size);
                var screen = c.GetViewportRect();
                var visible = rect.Intersection(screen);

                // 有些按钮（右侧的 ✓、前进）停在画布外一点点，用原始坐标点左上角是有效的：
                // 不要拒绝，改成贴边点击。
                if (visible.Size.Length() < 4f)
                {
                    var label2 = Clean(TextOf(c));
                    var p2 = new Vector2(origin.X + 4f, origin.Y + 4f);
                    UiClicks.ClickAt(p2.X, p2.Y);
                    BridgeLog.Info($"tapped offscreen {entry.Kind} uid={uid} '{label2}' at {p2}");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["tapped"] = label2,
                        ["kind"] = entry.Kind,
                        ["method"] = "edge",
                        ["x"] = p2.X,
                        ["y"] = p2.Y,
                        // 画布外的按钮贴边点不一定生效（实测两次 ok 但什么都没发生）。
                        // 不再假装成功：把事实标出来，让 agent 知道要自己确认结果。
                        ["offscreen"] = true,
                        ["warning"] = "控件在画布外，这次贴边点击可能无效；点完请重读 sts2_request / sts2_view 确认",
                    };
                }

                var center = visible.GetCenter();

                // Some confirm buttons sit just outside the 1920-wide canvas; clamp the click
                // into the viewport so it still lands on the visible edge of the control.
                var vp = c.GetViewportRect().Size;
                center = new Vector2(
                    Math.Clamp(center.X, 8f, vp.X - 8f),
                    Math.Clamp(center.Y, 8f, vp.Y - 8f));

                // Controls that are not BaseButtons (character tiles, merchant slots, pile cards)
                // have no meaningful ForceClick: they only react to a real mouse press.
                if (c is not BaseButton)
                {
                    ClickControl(c);
                    BridgeLog.Info($"tapped {entry.Kind} uid={uid} via mouse");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["tapped"] = Clean(TextOf(c)),
                        ["kind"] = entry.Kind,
                        ["method"] = "mouse",
                    };
                }

                // StS2's NButton subclasses (the rest site's 前进, room proceed buttons) react to
                // their own ForceClick signal, not to a synthetic mouse press.
                var force = c.GetType().GetMethod("ForceClick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (force != null && force.GetParameters().Length == 0)
                {
                    force.Invoke(c, null);
                    BridgeLog.Info($"tapped {entry.Kind} uid={uid} '{Clean(TextOf(c))}' via ForceClick");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["tapped"] = Clean(TextOf(c)),
                        ["kind"] = entry.Kind,
                        ["method"] = "ForceClick",
                    };
                }

                UiClicks.ClickAt(center.X, center.Y);
                BridgeLog.Info($"tapped {entry.Kind} uid={uid} '{Clean(TextOf(c))}' at {center}");
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["tapped"] = Clean(TextOf(c)),
                    ["kind"] = entry.Kind,
                    ["x"] = center.X,
                    ["y"] = center.Y,
                };
            }
            return Fail($"no clickable control with uid {uid}");
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>
    /// Card-choice flow: tap the card, then press the confirm control (the checkmark only moves
    /// into view once something is selected). Used for "put a card on top of the draw pile",
    /// removals, upgrades and transforms.
    /// </summary>
    public static Dictionary<string, object> SelectCard(ulong uid)
    {
        try
        {
            Control card = null;
            foreach (var entry in Clickables())
            {
                if (entry.Control.GetInstanceId() != uid) continue;
                card = entry.Control;
                break;
            }
            if (card == null) return Fail($"no card control with uid {uid}");

            ClickControl(card);

            if (Engine.GetMainLoop() is not SceneTree tree) return Fail("no scene tree");

            // The checkmark sits off-screen until a card is picked, then slides into view.
            // Poll for it rather than clicking a stale position.
            ScheduleConfirm(tree, uid, 0);

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["card"] = Clean(TextOf(card)),
                ["confirmScheduled"] = true,
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    private const int MaxConfirmAttempts = 8;

    /// <summary>
    /// Waits for the checkmark to slide into view before pressing it. A missed card tap leaves
    /// the button off-screen, so the card is re-tapped once halfway through.
    /// </summary>
    private static void ScheduleConfirm(SceneTree tree, ulong cardUid, int attempt)
    {
        var timer = tree.CreateTimer(0.2);
        timer.Timeout += () =>
        {
            try
            {
                var vp = tree.Root.GetVisibleRect().Size;

                // Some screens commit immediately (exhaust/discard): the tapped card is simply
                // gone and there is no checkmark at all.
                var stillThere = false;
                foreach (var entry in Clickables())
                {
                    if (entry.Control.GetInstanceId() == cardUid) { stillThere = true; break; }
                }
                if (!stillThere)
                {
                    BridgeLog.Info("select: card committed without a confirm button");
                    return;
                }

                var confirm = FindConfirm();

                var ready = false;
                if (confirm != null)
                {
                    var center = confirm.GetScreenPosition() + confirm.Size / 2f;
                    ready = center.X < vp.X - 60f && center.Y < vp.Y - 60f;
                }

                if (!ready)
                {
                    if (attempt >= MaxConfirmAttempts)
                    {
                        BridgeLog.Warn($"select: no in-view confirm after {attempt} tries (card {cardUid})");
                        return;
                    }

                    if (attempt == 2)
                    {
                        foreach (var entry in Clickables())
                        {
                            if (entry.Control.GetInstanceId() != cardUid) continue;
                            ClickControl(entry.Control);
                            BridgeLog.Info("select: re-tapped the card");
                            break;
                        }
                    }

                    ScheduleConfirm(tree, cardUid, attempt + 1);
                    return;
                }

                ClickControl(confirm);
                BridgeLog.Info($"select: confirmed at {confirm.GetScreenPosition()} (attempt {attempt})");
            }
            catch (Exception e)
            {
                BridgeLog.Warn("select confirm failed: " + e.Message);
            }
        };
    }

    private static string ModelLabel(Control c)
    {
        try
        {
            var type = c.GetType();

            // 角色选择卡：官方节点自带 IsRandom / IsLocked，直接读，别猜名字。
            if (TypeChainContains(c, "CharacterSelectButton"))
            {
                const System.Reflection.BindingFlags CF =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                var isRandom = type.GetProperty("IsRandom", CF)?.GetValue(c) is true;
                if (isRandom) return "random";
                var character = type.GetProperty("Character", CF)?.GetValue(c);
                var charTitle = character?.GetType().GetProperty("Title", CF)?.GetValue(character) as string;
                if (!string.IsNullOrEmpty(charTitle))
                {
                    var locked = type.GetProperty("IsLocked", CF)?.GetValue(c) is true;
                    return locked ? charTitle + "（未解锁）" : charTitle;
                }
            }
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

            object FindTitle(object target, int depth)
            {
                if (target == null || depth > 2) return null;
                var t = target.GetType();
                if (t.IsPrimitive || t == typeof(string)) return null;
                var title = t.GetProperty("Title", F)?.GetValue(target) as string;
                if (!string.IsNullOrEmpty(title)) return target;
                foreach (var p in t.GetProperties(F))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string)) continue;
                    object v;
                    try { v = p.GetValue(target); } catch { continue; }
                    if (v == null) continue;
                    var found = FindTitle(v, depth + 1);
                    if (found != null) return found;
                }
                return null;
            }

            object entry = null;
            if (TypeChainContains(c, "MerchantSlot"))
                entry = type.GetProperty("Entry", F)?.GetValue(c);

            var model = entry == null ? FindTitle(c, 0) : FindTitle(entry, 0);
            if (model == null) return null;

            // 角色页最后一格是「随机」：它背后也挂着一个角色模型，绝不能当成铁甲标出来，
            // 否则 agent 点「ironclad」就会点到随机。
            var modelType = model.GetType().Name;
            if (modelType.Contains("Random", StringComparison.OrdinalIgnoreCase)) return "random";

            var title = model.GetType().GetProperty("Title", F)?.GetValue(model) as string;
            if (string.IsNullOrEmpty(title)) return null;

            if (entry != null)
            {
                foreach (var p in entry.GetType().GetProperties(F))
                {
                    var n = p.Name;
                    if (!n.Contains("Cost") && !n.Contains("Price")) continue;
                    object raw;
                    try { raw = p.GetValue(entry); } catch { continue; }
                    if (raw is int cost) return $"{title} {cost}金";
                }
            }
            return title;
        }
        catch { return null; }
    }

    /// <summary>
    /// 多选界面（old /select）已经删除：选牌统一走 /request + /respond。
    /// 这里只保留只读的「还剩几张可点」计数，供 <see cref="SelectWait"/>（fallback 通道）判定。
    /// </summary>
    private static int CountCardControls()
    {
        // Only count actually selectable cards. The big preview pane (kind "holder") also carries
        // a card model and must not make a finished selection look unfinished.
        var n = 0;
        foreach (var entry in Clickables())
        {
            if (entry.Kind == "holder") continue;
            try { if (Choices.CardOf(entry.Control) != null) n++; } catch { }
        }
        return n;
    }

    /// <summary>
    /// Taps a card, then waits for the confirm control to become clickable. Some screens commit
    /// immediately (the card simply disappears), some need the checkmark, and multi-select
    /// screens keep the checkmark parked off-screen until enough cards are picked.
    /// </summary>
    public static async System.Threading.Tasks.Task<Dictionary<string, object>> SelectWait(ulong uid)
    {
        try
        {
            var tap = await BridgePump.OnMain(() => SelectCard(uid));
            if (tap.TryGetValue("ok", out var okVal) && okVal is false) return tap;

            var before = await BridgePump.OnMain(CountCardControls);

            for (var i = 0; i < 8; i++)
            {
                await System.Threading.Tasks.Task.Delay(300);
                var state = await BridgePump.OnMain(() => SelectionState(uid));

                if (state == "gone")
                    return new Dictionary<string, object>
                    {
                        ["confirmed"] = true,
                        ["how"] = "screen-closed",
                    };

                if (state == "ready")
                {
                    await BridgePump.OnMain(ClickConfirm);
                    await System.Threading.Tasks.Task.Delay(450);
                    var after = await BridgePump.OnMain(CountCardControls);

                    // 丢弃/消耗这类界面不开新浮层，确认后只是「手牌少一张」；
                    // 所以「变少」也算成功，不能只认「全消失」。
                    if (after < before)
                        return new Dictionary<string, object>
                        {
                            ["confirmed"] = true,
                            ["how"] = "confirm",
                            ["remaining"] = after,
                        };
                    if (after == 0)
                        return new Dictionary<string, object> { ["confirmed"] = true, ["how"] = "confirm" };
                }
            }

            return new Dictionary<string, object>
            {
                ["confirmed"] = false,
                ["hint"] = "确认键一直没出现在屏幕内（这个界面可能还要再选一张）",
            };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    private static string SelectionState(ulong uid)
    {
        // 界面真的关了才算完成：没有任何可选择的牌了。
        if (CountCardControls() == 0) return "gone";

        var exists = false;
        foreach (var entry in Clickables())
            if (entry.Control.GetInstanceId() == uid) { exists = true; break; }

        var confirm = FindConfirm();
        if (confirm == null) return "waiting";

        // 选中后 ✓ 会滑入可视区；此时允许它稍微探出右边缘（点它的左半部分）。
        var origin = confirm.GetScreenPosition();
        var vp = confirm.GetViewportRect().Size;
        var reachable = origin.X < vp.X - 20f && origin.Y < vp.Y - 20f;
        if (!reachable) return "waiting";

        return exists ? "ready" : "ready";
    }

    private static Dictionary<string, object> ClickConfirm()
    {
        var confirm = FindConfirm();
        if (confirm == null) return Fail("no confirm control");
        ClickControl(confirm);
        return new Dictionary<string, object> { ["clicked"] = Clean(TextOf(confirm)) };
    }

    /// <summary>
    /// 给 RequestApi 用：按一次当前的确认控件。内部就是既有的 ClickConfirm（先按确认词找文字，
    /// 找不到再按右侧「✓」的形状找）。只多开一个入口，不改既有逻辑。
    /// </summary>
    internal static Dictionary<string, object> ClickConfirmInternal() => ClickConfirm();

    /// <summary>
    /// 给 RequestApi 用：确认控件现在是否已经滑进可视区（和 SelectionState 里用的是同一个判定）。
    /// </summary>
    internal static bool ConfirmReachable()
    {
        try
        {
            var confirm = FindConfirm();
            if (confirm == null) return false;
            var origin = confirm.GetScreenPosition();
            var vp = confirm.GetViewportRect().Size;
            return origin.X < vp.X - 20f && origin.Y < vp.Y - 20f;
        }
        catch { return false; }
    }

    /// <summary>Text match first, then the right-side checkmark shape.</summary>
    private static Control FindConfirm()
    {
        var all = Clickables();
        foreach (var entry in all)
        {
            var text = Clean(TextOf(entry.Control));
            if (text.Length == 0) continue;
            foreach (var word in ConfirmWords)
                if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return entry.Control;
        }

        foreach (var entry in all)
        {
            var c = entry.Control;
            if (Clean(TextOf(c)).Length > 0) continue;
            var p = c.GetScreenPosition();
            var size = c.Size;
            if (p.X < 1200f) continue;
            if (p.Y < 480f || p.Y > 950f) continue;
            if (size.X < 120f || size.X > 340f) continue;
            if (size.Y < 60f || size.Y > 200f) continue;
            return c;
        }
        return null;
    }

    private static void ClickControl(Control c)
    {
        // Several StS2 controls (character tiles, merchant slots) put the real hit area on a
        // child "Hitbox"; clicking the container itself does nothing.
        try
        {
            var hitbox = c.GetType()
                .GetProperty("Hitbox", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(c) as Control;
            if (hitbox != null && hitbox.IsInsideTree()) c = hitbox;
        }
        catch { }

        var vp = c.GetViewportRect().Size;
        var origin = c.GetScreenPosition();
        var center = origin + c.Size / 2f;

        // 画布外一点的按钮（右侧 ✓）用原始坐标点左上角才有效；硬夹到 1912 会落到按钮外。
        if (origin.X > vp.X - 40f || origin.Y > vp.Y - 40f)
        {
            var edge = new Vector2(origin.X + 4f, origin.Y + 4f);
            UiClicks.ClickAt(edge.X, edge.Y);
            return;
        }

        center = new Vector2(
            Math.Clamp(center.X, 8f, vp.X - 8f),
            Math.Clamp(center.Y, 8f, vp.Y - 8f));
        UiClicks.ClickAt(center.X, center.Y);
    }

    private sealed class Entry
    {
        public Control Control;
        public string Kind;
    }

    private static List<Entry> Clickables()
    {
        var found = new List<Entry>();
        if (Engine.GetMainLoop() is not SceneTree tree) return found;

        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 40000)
        {
            var node = stack.Pop();

            if (node is Control control && control.IsVisibleInTree() && control.Size.Y > 4f && control.Size.X > 4f)
            {
                var kind = KindOf(node);
                if (kind != null) found.Add(new Entry { Control = control, Kind = kind });
            }

            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        found.Sort((a, b) =>
        {
            var dy = a.Control.GlobalPosition.Y.CompareTo(b.Control.GlobalPosition.Y);
            return dy != 0 ? dy : a.Control.GlobalPosition.X.CompareTo(b.Control.GlobalPosition.X);
        });
        return found;
    }

    /// <summary>
    /// 判断某个节点的类型（含所有基类）名字里是否包含某段文字。
    /// 官方很多控件是派生类：NMerchantCard : NMerchantSlot、NDeckUpgradeSelectScreen :
    /// NCardGridSelectionScreen、NGuardBuy… 只匹配运行时类名会大量漏判。
    /// </summary>
    internal static bool TypeChainContains(Node node, string fragment)
    {
        for (var t = node.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            if (t.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>收集当前界面上所有可见文字（提示语、标题、说明）。</summary>
    internal static List<object> ScreenTexts()
    {
        var list = new List<object>();
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return list;
            var seen = new HashSet<string>();
            var stack = new Stack<Node>();
            stack.Push(tree.Root);
            var visited = 0;
            while (stack.Count > 0 && visited++ < 20000 && list.Count < 30)
            {
                var node = stack.Pop();
                string raw = null;
                if (node is Label label) raw = label.Text;
                else if (node is RichTextLabel rich) raw = rich.Text;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var text = Clean(raw);
                    if (text.Length > 1 && seen.Add(text))
                        list.Add(new Dictionary<string, object> { ["text"] = text });
                }
                foreach (var child in node.GetChildren()) stack.Push(child);
            }
        }
        catch { }
        return list;
    }

    private static string KindOf(Node node)
    {
        if (node is NButton) return "button";
        if (node is NClickableControl) return "clickable";

        if (TypeChainContains(node, "Button")) return "button-like";
        if (TypeChainContains(node, "Holder")) return "holder";
        return null;
    }

    private static string CurrentScreen()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return "?";

            // The deepest visible screen-ish ancestor of the focused control is what the
            // player is looking at; fall back to the current scene name.
            var scene = tree.CurrentScene;
            return scene?.Name.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>去掉 BBCode、压平空白；RequestApi 兜底读 prompt 时也用它。</summary>
    internal static string Clean(string text) => Clean(text, 60);

    /// <summary>
    /// 同样是去 BBCode / 压空白，但**不截断**到 60 字。
    /// 卡牌、遗物、事件选项的说明经常超过 60 字，截断会让 agent 看不到关键效果，
    /// 所以这类「给人读的说明文字」用这个。
    /// </summary>
    internal static string CleanLong(string text) => Clean(text, 400);

    private static string Clean(string text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // Buttons carry BBCode markup like "[gold][b]继续[/b][/gold]"; strip it so the agent
        // reads "继续" instead of tag soup.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\[\]]{1,20}\]", " ");
        cleaned = cleaned.Replace("\n", " ").Replace("\r", " ").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Length > limit ? cleaned.Substring(0, limit) : cleaned;
    }

    private static string TextOf(Node n)
    {
        if (n is Label l) return l.Text;
        if (n is RichTextLabel r) return r.Text;
        if (n is Button btn) return btn.Text;
        foreach (var c in n.GetChildren())
        {
            var t = TextOf(c);
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }
        return string.Empty;
    }

    private static Dictionary<string, object> Fail(string message) => new() { ["ok"] = false, ["error"] = message };
}
