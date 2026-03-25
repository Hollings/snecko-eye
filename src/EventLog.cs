using System;
using System.Collections.Generic;
using Godot;

namespace SneckoEye;

/// <summary>
/// In-game sidebar that displays scrolling log messages.
/// Messages can come from the API client (POST /log), mod auto-logging,
/// or any other source. Displayed as a semi-transparent panel on the
/// right side of the screen.
/// </summary>
public partial class EventLog : CanvasLayer
{
    private static EventLog? _instance;
    private static readonly List<string> _messages = new();
    private const int MaxMessages = 50;

    private RichTextLabel? _textLabel;
    private PanelContainer? _panel;

    public static void Create()
    {
        if (_instance != null) return;

        _instance = new EventLog();
        _instance.Layer = 99; // Below status overlay, above game
        _instance.Name = "SneckoEyeEventLog";

        // Panel background
        var panel = new PanelContainer();
        panel.AnchorLeft = 1.0f;
        panel.AnchorRight = 1.0f;
        panel.AnchorTop = 0.15f;
        panel.AnchorBottom = 0.85f;
        panel.OffsetLeft = -340;
        panel.OffsetRight = -5;
        panel.OffsetTop = 0;
        panel.OffsetBottom = 0;
        panel.GrowHorizontal = Control.GrowDirection.Begin;

        // Semi-transparent background style
        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.0f, 0.0f, 0.0f, 0.55f);
        style.CornerRadiusTopLeft = 6;
        style.CornerRadiusTopRight = 6;
        style.CornerRadiusBottomLeft = 6;
        style.CornerRadiusBottomRight = 6;
        style.ContentMarginLeft = 8;
        style.ContentMarginRight = 8;
        style.ContentMarginTop = 6;
        style.ContentMarginBottom = 6;
        panel.AddThemeStyleboxOverride("panel", style);

        // Scrollable text
        var text = new RichTextLabel();
        text.BbcodeEnabled = true;
        text.ScrollFollowing = true;
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        text.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        text.AddThemeColorOverride("default_color", new Color(0.85f, 0.85f, 0.85f, 0.95f));
        text.AddThemeFontSizeOverride("normal_font_size", 13);

        panel.AddChild(text);
        _instance._textLabel = text;
        _instance._panel = panel;
        _instance.AddChild(panel);

        // Mouse passthrough so it doesn't block game input
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        text.MouseFilter = Control.MouseFilterEnum.Ignore;

        // Add to scene tree
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root.CallDeferred("add_child", _instance);

        ModEntry.Log("Event log created");
    }

    /// <summary>
    /// Add a message to the event log. Thread-safe -- can be called from any thread.
    /// </summary>
    public static void Log(string message)
    {
        lock (_messages)
        {
            _messages.Add(message);
            if (_messages.Count > MaxMessages)
                _messages.RemoveAt(0);
        }

        // Schedule UI update on main thread
        if (_instance != null)
        {
            Godot.Callable.From(() => _instance.RefreshText()).CallDeferred();
        }
    }

    /// <summary>
    /// Add a message with a colored tag prefix.
    /// </summary>
    public static void Log(string tag, string message)
    {
        string color = tag.ToLowerInvariant() switch
        {
            "action" => "#88cc88",   // green
            "phase" => "#88aadd",    // blue
            "think" => "#ddaa55",    // gold
            "error" => "#dd5555",    // red
            "info" => "#aaaaaa",     // gray
            _ => "#cccccc",
        };
        Log($"[color={color}][{tag}][/color] {message}");
    }

    /// <summary>
    /// Clear all messages.
    /// </summary>
    public static void Clear()
    {
        lock (_messages)
        {
            _messages.Clear();
        }
        if (_instance?._textLabel != null)
        {
            Godot.Callable.From(() =>
            {
                _instance._textLabel.Clear();
            }).CallDeferred();
        }
    }

    public static new void SetVisible(bool visible)
    {
        if (_instance?._panel != null)
            _instance._panel.Visible = visible;
    }

    private void RefreshText()
    {
        if (_textLabel == null) return;
        _textLabel.Clear();
        lock (_messages)
        {
            foreach (var msg in _messages)
            {
                _textLabel.AppendText(msg + "\n");
            }
        }
    }
}
