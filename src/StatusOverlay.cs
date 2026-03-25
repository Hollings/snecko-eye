using Godot;

namespace SneckoEye;

/// <summary>
/// A simple in-game overlay that shows the API server status.
/// Displayed as a small label in the top-right corner of the screen.
/// </summary>
public partial class StatusOverlay : CanvasLayer
{
    private static StatusOverlay? _instance;
    private Label? _label;

    public static void Create()
    {
        if (_instance != null) return;

        _instance = new StatusOverlay();
        _instance.Layer = 100; // Above everything
        _instance.Name = "SneckoEyeOverlay";

        var label = new Label();
        label.Text = "AutoPlay API: http://localhost:9000\n/help for docs";
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Top;

        // Position in top-right corner with padding
        label.AnchorLeft = 1.0f;
        label.AnchorRight = 1.0f;
        label.AnchorTop = 0.0f;
        label.AnchorBottom = 0.0f;
        label.OffsetLeft = -320;
        label.OffsetRight = -10;
        label.OffsetTop = 10;
        label.OffsetBottom = 50;
        label.GrowHorizontal = Control.GrowDirection.Begin;

        // Style
        label.AddThemeColorOverride("font_color", new Color(0.6f, 0.9f, 0.6f, 0.8f));
        label.AddThemeFontSizeOverride("font_size", 14);

        _instance._label = label;
        _instance.AddChild(label);

        // Add to scene tree
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root.CallDeferred("add_child", _instance);

        ModEntry.Log("Status overlay created");
    }

    public static void UpdateStatus(string text)
    {
        if (_instance?._label != null)
            _instance._label.Text = text;
    }
}
