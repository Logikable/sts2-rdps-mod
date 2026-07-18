using Godot;
using MegaCrit.Sts2.Core.Combat;

namespace RdpsMeter;

/// <summary>
/// The live in-combat rDPS meter. A self-owned CanvasLayer parented to the scene root (rather than the game's own UI
/// tree) so it draws on top of everything without inheriting the game's layout or theme, the same approach the
/// existing STS2 damage meters take. It polls <see cref="CombatLedger"/> each frame and shows one compact row per
/// player - name, an rDPS bar scaled to the current leader, and the rDPS number - in the top-right corner, hidden
/// whenever a combat is not in progress. The per-source breakdown is a follow-up (hover).
/// </summary>
internal static class RdpsOverlay
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }

        // Install runs during mod init while the root is still building its children and rejects a direct AddChild;
        // defer to the next idle frame as the engine requires.
        tree.Root.CallDeferred(Node.MethodName.AddChild, new RdpsOverlayNode());
        _installed = true;
        GD.Print("[RdpsMeter] Overlay installed");
    }
}

internal sealed partial class RdpsOverlayNode : CanvasLayer
{
    private sealed class Row
    {
        public required HBoxContainer Container { get; init; }
        public required Label Name { get; init; }
        public required ProgressBar Bar { get; init; }
        public required Label Rdps { get; init; }
    }

    private readonly Dictionary<ulong, Row> _rows = new();
    private PanelContainer _panel = null!;
    private VBoxContainer _list = null!;

    public override void _Ready()
    {
        Layer = 128;

        _panel = new PanelContainer
        {
            // Anchor to the top-right corner and grow left/down so the panel hugs the corner and sizes to content.
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.End,
            OffsetTop = 12f,
            OffsetRight = -12f,
        };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.55f),
            ContentMarginLeft = 8f,
            ContentMarginRight = 8f,
            ContentMarginTop = 6f,
            ContentMarginBottom = 6f,
        });

        _list = new VBoxContainer();
        _panel.AddChild(_list);
        AddChild(_panel);
    }

    public override void _Process(double delta)
    {
        bool inCombat = CombatManager.Instance is { IsInProgress: true };
        _panel.Visible = inCombat;
        if (!inCombat)
        {
            return;
        }

        IReadOnlyList<RdpsRow> snapshot = CombatLedger.Instance.Snapshot();
        decimal max = snapshot.Count > 0 ? Math.Max(snapshot.Max(r => r.Rdps), 1m) : 1m;

        var seen = new HashSet<ulong>();
        int index = 0;
        foreach (RdpsRow row in snapshot)
        {
            seen.Add(row.NetId);
            Row widget = Ensure(row.NetId);
            widget.Name.Text = row.Name;
            widget.Rdps.Text = Round(row.Rdps).ToString();
            widget.Bar.Value = (double)Math.Clamp(row.Rdps / max, 0m, 1m);
            _list.MoveChild(widget.Container, index++);
        }

        foreach (ulong netId in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows[netId].Container.QueueFree();
            _rows.Remove(netId);
        }
    }

    private Row Ensure(ulong netId)
    {
        if (_rows.TryGetValue(netId, out Row? existing))
        {
            return existing;
        }

        var name = new Label { CustomMinimumSize = new Vector2(96f, 0f) };
        name.AddThemeColorOverride("font_color", Colors.White);

        var bar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 1d,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(120f, 14f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("e0b341") });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.12f) });

        var rdps = new Label
        {
            CustomMinimumSize = new Vector2(44f, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rdps.AddThemeColorOverride("font_color", Colors.White);

        var container = new HBoxContainer();
        container.AddThemeConstantOverride("separation", 8);
        container.AddChild(name);
        container.AddChild(bar);
        container.AddChild(rdps);
        _list.AddChild(container);

        var widget = new Row { Container = container, Name = name, Bar = bar, Rdps = rdps };
        _rows[netId] = widget;
        return widget;
    }

    private static decimal Round(decimal value)
    {
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
