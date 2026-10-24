using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Sts2AgentBridge;

/// <summary>
/// <b>调试用</b>：让 agent 看见并按下游戏自己的按钮（主菜单、角色选择、奖励…），不靠像素猜。
///
/// 正常流程**不要**用这里的东西：所有决策都走 <c>GET /request</c> + <c>POST /respond</c>
/// （语义请求协议，见 <see cref="RequestApi"/>）。这里只保留两条调试入口：
/// <c>POST /click</c>（按 uid / 文本 / 下标点按钮）和 <c>POST /click_at</c>（按坐标点）。
/// 老的 <c>GET /buttons</c>（按钮枚举）已经删除。
/// </summary>
public static class UiClicks
{
    /// <summary>按可见按钮的顺序点第 index 个（调试用）。</summary>
    public static Dictionary<string, object> Click(int index)
    {
        var buttons = Visible();
        if (index < 0 || index >= buttons.Count)
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"button index {index} out of range 0..{buttons.Count - 1}" };

        var button = buttons[index];
        var text = TextOf(button);
        button.ForceClick();
        BridgeLog.Info($"clicked button {index} '{text}'");
        return new Dictionary<string, object> { ["ok"] = true, ["clicked"] = text, ["index"] = index };
    }

    public static Dictionary<string, object> ClickByUid(ulong uid)
    {
        foreach (var button in Visible())
        {
            if (button.GetInstanceId() != uid) continue;
            var text = TextOf(button);
            button.ForceClick();
            BridgeLog.Info($"clicked button uid={uid} '{text}'");
            return new Dictionary<string, object> { ["ok"] = true, ["clicked"] = text, ["uid"] = uid };
        }
        return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"no visible button with uid {uid}" };
    }

    public static Dictionary<string, object> ClickText(string text)
    {
        var buttons = Visible();
        for (var i = 0; i < buttons.Count; i++)
        {
            if (TextOf(buttons[i]) != text) continue;
            buttons[i].ForceClick();
            BridgeLog.Info($"clicked button {i} '{text}'");
            return new Dictionary<string, object> { ["ok"] = true, ["clicked"] = text, ["index"] = i };
        }
        return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"no visible button with text '{text}'" };
    }

    /// <summary>
    /// Press and release across frames. Controls that handle _gui_input (cards, choice slots)
    /// ignore a press+release delivered inside the same frame, so the release is deferred by
    /// a SceneTreeTimer instead of being pushed immediately.
    /// </summary>
    /// <summary>
    /// Press on (x1,y1), glide to (x2,y2) across several frames, release. StS2 uses real drags
    /// for some interactions (dragging a card out of the hand / onto the discard pile), where a
    /// plain click is ignored.
    /// </summary>
    public static Dictionary<string, object> DragAt(float x1, float y1, float x2, float y2)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var viewport = tree.Root;
        var from = new Vector2(x1, y1);
        var to = new Vector2(x2, y2);

        void Motion(Vector2 p, int mask)
        {
            viewport.PushInput(new InputEventMouseMotion
            {
                Position = p,
                GlobalPosition = p,
                ButtonMask = (MouseButtonMask)mask,
                Relative = Vector2.Zero,
            }, true);
        }

        Motion(from, (int)MouseButtonMask.Left);
        viewport.PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left,
            Pressed = true,
            Position = from,
            GlobalPosition = from,
        }, true);

        // Glide in a few steps so the game sees a real drag, not a teleport.
        for (var step = 1; step <= 4; step++)
        {
            var t = step / 4f;
            var p = from.Lerp(to, t);
            var timer = tree.CreateTimer(0.05 * step);
            timer.Timeout += () =>
            {
                try { Motion(p, (int)MouseButtonMask.Left); } catch { }
            };
        }

        var release = tree.CreateTimer(0.3);
        release.Timeout += () =>
        {
            try
            {
                Motion(to, 0);
                viewport.PushInput(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    ButtonMask = 0,
                    Pressed = false,
                    Position = to,
                    GlobalPosition = to,
                }, true);
            }
            catch { }
        };

        BridgeLog.Info($"injected drag ({x1},{y1}) -> ({x2},{y2})");
        return new Dictionary<string, object> { ["ok"] = true, ["from"] = new[] { x1, y1 }, ["to"] = new[] { x2, y2 } };
    }

    public static Dictionary<string, object> ClickAt(float x, float y)
    {
        var position = new Vector2(x, y);
        var tree = (SceneTree)Engine.GetMainLoop();
        var viewport = tree.Root;

        viewport.PushInput(new InputEventMouseMotion
        {
            Position = position,
            GlobalPosition = position,
        }, true);

        viewport.PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left,
            Pressed = true,
            Position = position,
            GlobalPosition = position,
        }, true);

        var timer = tree.CreateTimer(0.08);
        timer.Timeout += () =>
        {
            try
            {
                viewport.PushInput(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    ButtonMask = 0,
                    Pressed = false,
                    Position = position,
                    GlobalPosition = position,
                }, true);
            }
            catch (Exception e)
            {
                BridgeLog.Warn("click release failed: " + e.Message);
            }
        };

        BridgeLog.Info($"injected click at ({x},{y})");
        return new Dictionary<string, object> { ["ok"] = true, ["x"] = x, ["y"] = y };
    }

    private static List<NButton> Visible()
    {
        var found = new List<NButton>();
        if (Engine.GetMainLoop() is not SceneTree tree) return found;

        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 20000)
        {
            var node = stack.Pop();
            if (node is NButton b && b.IsVisibleInTree() && b.Size.Y > 5f) found.Add(b);
            foreach (var child in node.GetChildren()) stack.Push(child);
        }

        found.Sort((a, b) => a.GlobalPosition.Y.CompareTo(b.GlobalPosition.Y));
        return found;
    }

    private static string TextOf(Node n)
    {
        if (n is Label l) return l.Text;
        if (n is RichTextLabel r) return r.Text;
        if (n is Button btn) return btn.Text;
        foreach (var c in n.GetChildren())
        {
            var t = TextOf(c);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return string.Empty;
    }
}
