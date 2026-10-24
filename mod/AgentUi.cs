using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2AgentBridge;

/// <summary>
/// Injects the three "肥鱼代理" toggles: main menu, pause (ESC) menu, and under the
/// multiplayer invite button. State persists to mod_configs/Sts2AgentBridge/ui.json.
/// </summary>
public static class AgentUi
{
    private static readonly Dictionary<string, bool> State = new()
    {
        ["main"] = false,
        ["pause"] = false,
        ["invite"] = false,
    };

    private static readonly Dictionary<string, Button> Buttons = new();
    private static readonly Dictionary<string, Control> Anchors = new();

    private static Control _mainButton, _pauseButton, _inviteButton;
    private static bool _pauseAttachedForRun;
    private static int _tick;
    private static string _savePath;

    public static bool AgentMode => State["main"];

    private static string SavePath
    {
        get
        {
            if (_savePath != null) return _savePath;
            var dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "mod_configs", "Sts2AgentBridge");
            Directory.CreateDirectory(dir);
            _savePath = System.IO.Path.Combine(dir, "ui.json");
            return _savePath;
        }
    }

    // ------------------------------------------------------------------ state

    public static void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SavePath));
            foreach (var key in new[] { "main", "pause", "invite" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                {
                    State[key] = v.GetBoolean();
                }
            }
            BridgeLog.Info($"ui state loaded main={State["main"]} pause={State["pause"]} invite={State["invite"]}");
        }
        catch (Exception e)
        {
            BridgeLog.Error("ui state load failed", e);
        }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(SavePath, JsonSerializer.Serialize(new Dictionary<string, bool>(State),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            BridgeLog.Error("ui state save failed", e);
        }
    }

    public static Dictionary<string, object> Snapshot() => new()
    {
        ["main"] = State["main"],
        ["pause"] = State["pause"],
        ["invite"] = State["invite"],
        ["mainButton"] = Alive(_mainButton),
        ["pauseButton"] = Alive(_pauseButton),
        ["inviteButton"] = Alive(_inviteButton),
        ["agentMode"] = AgentMode,
    };

    public static bool Set(string which, bool value)
    {
        if (!State.ContainsKey(which)) return false;
        Apply(which, value);
        return true;
    }

    // ------------------------------------------------------------------- tick

    public static void Tick()
    {
        if (++_tick % 90 != 0) return;
        try
        {
            AttachMainMenu();
            AttachPauseMenu();
            AttachInvite();
            RefreshLabels();
        }
        catch (Exception e)
        {
            BridgeLog.Error("ui tick failed", e);
        }
    }

    private static void AttachMainMenu()
    {
        if (Alive(_mainButton)) return;
        var menu = NGame.Instance?.MainMenu;
        if (menu == null) return;
        var anchor = FindVisibleButton(menu);
        if (anchor == null) return;
        _mainButton = CreateToggle(anchor, "AgentMainToggle", "main", "肥鱼代理");
        if (_mainButton != null) BridgeLog.Info("main menu toggle attached");
    }

    private static void AttachPauseMenu()
    {
        var rm = RunManager.Instance;
        if (rm == null || !rm.IsInProgress)
        {
            _pauseButton = null;
            _pauseAttachedForRun = false;
            return;
        }

        // Spec: the ESC toggle only applies to single player.
        if (!rm.IsSingleplayerOrFakeMultiplayer)
        {
            _pauseButton = null;
            return;
        }

        if (Alive(_pauseButton) || _pauseAttachedForRun) return;

        var pause = FindFirst<NPauseMenu>();
        if (pause == null) return;
        var anchor = FindFirstIn<NButton>(pause);
        if (anchor == null) return;

        _pauseButton = CreateToggle(anchor, "AgentPauseToggle", "pause", "肥鱼代理");
        _pauseAttachedForRun = _pauseButton != null;
        if (_pauseButton != null) BridgeLog.Info("pause menu toggle attached");
    }

    private static void AttachInvite()
    {
        if (Alive(_inviteButton)) return;
        var invite = FindFirst<NInvitePlayersButton>();
        if (invite == null) return;
        _inviteButton = CreateToggle(invite, "AgentInviteToggle", "invite", "邀请肥鱼");
        if (_inviteButton != null) BridgeLog.Info("invite toggle attached");
    }

    // ---------------------------------------------------------------- helpers

    private static bool Alive(Control c) => c != null && GodotObject.IsInstanceValid(c);

    /// <summary>
    /// Creates a native Button next to <paramref name="anchor"/>. A cloned NButton would have no
    /// intrinsic size or text (it is scene-driven and self-drawn), so a themed Button is used.
    /// </summary>
    private static Button CreateToggle(Control anchor, string nodeName, string key, string label)
    {
        var parent = anchor.GetParent();
        if (parent == null) return null;

        var button = new Button
        {
            Name = nodeName,
            Text = LabelText(label, State[key]),
            CustomMinimumSize = new Vector2(180, 40),
        };
        if (anchor.Theme != null) button.Theme = anchor.Theme;

        button.Pressed += () => Apply(key, !State[key]);
        parent.AddChild(button);

        Buttons[key] = button;
        Anchors[key] = anchor;

        if (parent is Container)
            parent.MoveChild(button, anchor.GetIndex() + 1);
        else
            button.Position = anchor.Position + new Vector2(0, anchor.Size.Y + 8);

        return button;
    }

    private static string LabelText(string label, bool on) => label + "：" + (on ? "开" : "关");

    private static void Apply(string key, bool value)
    {
        State[key] = value;
        Save();

        var label = key == "invite" ? "邀请肥鱼" : "肥鱼代理";
        if (Buttons.TryGetValue(key, out var button) && button != null && GodotObject.IsInstanceValid(button))
            button.Text = LabelText(label, value);

        BridgeLog.Info($"toggle {key} -> {(value ? "on" : "off")}");
        if (key == "main" && value)
            BridgeLog.Info("agent mode on: /state agentMode=true, control routed to the agent");
    }

    private static void RefreshLabels()
    {
        foreach (var kv in Buttons)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var label = kv.Key == "invite" ? "邀请肥鱼" : "肥鱼代理";
            var wanted = LabelText(label, State[kv.Key]);
            if (kv.Value.Text != wanted) kv.Value.Text = wanted;
        }
    }

    private static T FindFirst<T>() where T : Node
    {
        var root = Engine.GetMainLoop() is SceneTree tree ? tree.Root : null;
        return root == null ? null : FindFirstIn<T>(root);
    }

    /// <summary>
    /// The lowest on-screen button, i.e. the last entry of the main-menu list, so the clone
    /// lands at the end of that column rather than in a corner icon row.
    /// </summary>
    private static NButton FindVisibleButton(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        NButton best = null;
        var bestY = float.MinValue;
        while (stack.Count > 0 && visited++ < 20000)
        {
            var node = stack.Pop();
            if (node is NButton button && button.IsVisibleInTree() && button.Size.Y > 10f)
            {
                var y = button.GlobalPosition.Y;
                if (y > bestY) { bestY = y; best = button; }
            }
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return best;
    }

    private static T FindFirstIn<T>(Node root) where T : Node
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 20000)
        {
            var node = stack.Pop();
            if (node is T hit) return hit;
            foreach (var child in node.GetChildren()) stack.Push(child);
        }
        return null;
    }
}
