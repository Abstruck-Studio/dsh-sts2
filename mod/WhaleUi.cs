using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Sts2AgentBridge;

/// <summary>
/// The little whale companion: a bottom-right avatar with a speech bubble. It shows whatever
/// short line the agent put into the command file.
/// </summary>
public static class WhaleUi
{
    private const string AssetName = "whale.png";
    private const float Margin = 28f;
    private const float WhaleSize = 252f;
    private const float BubbleWidth = 570f;
    public const int DefaultDurationMs = 15000;

    private static CanvasLayer _layer;
    private static Control _root;
    private static PanelContainer _bubble;
    private static Label _label;
    private static TextureRect _whale;
    private static long _hideAt;
    private static bool _hooked;
    private static string _lastText = "";

    public static bool IsReady => _root != null && GodotObject.IsInstanceValid(_root);

    public static void Install()
    {
        try
        {
            if (IsReady) return;
            if (Engine.GetMainLoop() is not SceneTree tree) return;

            _layer = new CanvasLayer { Layer = 128, Name = "WhaleCompanion" };
            _root = new Control { Name = "WhaleRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
            _layer.AddChild(_root);

            _bubble = BuildBubble(out _label);
            _root.AddChild(_bubble);

            _whale = new TextureRect
            {
                Name = "Whale",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Texture = LoadTexture(),
            };
            _root.AddChild(_whale);
            _bubble.Visible = false;

            if (!_hooked)
            {
                _hooked = true;
                tree.ProcessFrame += OnFrame;
            }

            Reposition();
            BridgeLog.Info("whale ui installed");
        }
        catch (Exception e)
        {
            BridgeLog.Error("whale install failed", e);
        }
    }

    private static PanelContainer BuildBubble(out Label label)
    {
        var panel = new PanelContainer
        {
            Name = "WhaleBubble",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(BubbleWidth, 0),
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.10f, 0.15f, 0.95f),
            BorderColor = new Color(0.45f, 0.62f, 0.95f, 0.95f),
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
        style.SetCornerRadiusAll(12);
        style.SetBorderWidthAll(2);
        panel.AddThemeStyleboxOverride("panel", style);

        label = new Label
        {
            Name = "WhaleText",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 24);
        label.AddThemeColorOverride("font_color", new Color(0.93f, 0.96f, 1f));
        panel.AddChild(label);
        return panel;
    }

    private static Texture2D LoadTexture()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(WhaleUi).Assembly.Location) ?? "";
            var path = Path.Combine(dir, AssetName);
            if (!File.Exists(path)) path = Path.Combine(dir, "assets", AssetName);
            if (!File.Exists(path))
            {
                BridgeLog.Warn("whale asset missing at " + path);
                return null;
            }

            var image = Image.LoadFromFile(path);
            return image == null ? null : ImageTexture.CreateFromImage(image);
        }
        catch (Exception e)
        {
            BridgeLog.Warn("whale texture failed: " + e.Message);
            return null;
        }
    }

    private static Vector2 ViewportSize()
    {
        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null) return tree.Root.GetVisibleRect().Size;
        return new Vector2(1920, 1080);
    }

    private static void EnsureAttached()
    {
        try
        {
            if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
            if (Engine.GetMainLoop() is not SceneTree tree) return;

            var parent = tree.CurrentScene as Node ?? tree.Root;
            if (_layer.GetParent() != parent)
            {
                _layer.GetParent()?.RemoveChild(_layer);
                parent.AddChild(_layer);
                BridgeLog.Info($"whale layer attached to {parent.GetType().Name}");
            }
            parent.MoveChild(_layer, parent.GetChildCount() - 1);
        }
        catch { }
    }

    private static void Reposition()
    {
        if (!IsReady) return;

        var size = ViewportSize();
        _root.Position = Vector2.Zero;
        _root.Size = size;

        _whale.Position = new Vector2(size.X - WhaleSize - Margin, size.Y - WhaleSize - Margin);
        _whale.Size = new Vector2(WhaleSize, WhaleSize);

        var bubbleHeight = Math.Max(52f, _bubble.GetCombinedMinimumSize().Y);
        var maxHeight = size.Y * 0.62f;
        if (bubbleHeight > maxHeight) bubbleHeight = maxHeight;
        var bubbleTop = size.Y - WhaleSize - Margin - bubbleHeight - 12f;
        _bubble.Position = new Vector2(size.X - BubbleWidth - Margin, Math.Max(8f, bubbleTop));
        _bubble.Size = new Vector2(BubbleWidth, bubbleHeight);
    }

    /// <summary>Longer lines get a smaller font so the bubble never swallows the screen.</summary>
    private static int FontSizeFor(string text)
    {
        var length = text?.Length ?? 0;
        if (length > 320) return 18;
        if (length > 200) return 19;
        if (length > 120) return 21;
        return 24;
    }

    public static Dictionary<string, object> Say(string text, int durationMs)
    {
        try
        {
            Install();
            if (!IsReady) return Fail("whale not ready");

            _lastText = text ?? "";
            _label.Text = _lastText;
            _label.AddThemeFontSizeOverride("font_size", FontSizeFor(_lastText));
            _bubble.Visible = true;
            _bubble.ResetSize();
            Reposition();
            EnsureAttached();

            var seconds = Math.Clamp(durationMs <= 0 ? DefaultDurationMs : durationMs, 800, 300000) / 1000.0;
            _hideAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(seconds * 1000);
            BridgeLog.Info($"whale says: {_lastText} ({seconds:0.#}s)");
            return new Dictionary<string, object> { ["ok"] = true, ["text"] = _lastText, ["ms"] = (long)(seconds * 1000) };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    public static Dictionary<string, object> Hide()
    {
        try
        {
            if (_bubble != null && GodotObject.IsInstanceValid(_bubble)) _bubble.Visible = false;
            _hideAt = 0;
            return new Dictionary<string, object> { ["ok"] = true, ["hidden"] = true };
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    public static Dictionary<string, object> State() => new()
    {
        ["ok"] = true,
        ["installed"] = IsReady,
        ["visible"] = _bubble != null && GodotObject.IsInstanceValid(_bubble) && _bubble.Visible,
        ["text"] = _lastText,
        ["viewport"] = new[] { ViewportSize().X, ViewportSize().Y },
    };

    private static void OnFrame()
    {
        if (!IsReady) return;

        try
        {
            if (_hideAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _hideAt)
            {
                _hideAt = 0;
                if (_bubble != null && GodotObject.IsInstanceValid(_bubble)) _bubble.Visible = false;
            }

            if (_bubble.Visible) Reposition();
            EnsureAttached();
        }
        catch { }
    }

    private static Dictionary<string, object> Fail(string message) => new() { ["ok"] = false, ["error"] = message };
}

