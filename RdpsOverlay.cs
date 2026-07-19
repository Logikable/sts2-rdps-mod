using Godot;
using MegaCrit.Sts2.Core.Combat;

namespace RdpsMeter;

/// <summary>
/// The live in-combat rDPS meter. A self-owned CanvasLayer parented to the scene root (rather than the game's own UI
/// tree) so it draws on top of everything without inheriting the game's layout or theme, the same approach the
/// existing STS2 damage meters take. It polls <see cref="CombatLedger"/> each frame and shows one compact row per
/// player - name, an rDPS bar scaled to the current leader, and the rDPS number - hidden whenever a combat is not in
/// progress. The panel is a bordered window that starts in the top-right corner and can be dragged by its header; only
/// that header takes the mouse, so the rest of the panel never intercepts a click meant for the game underneath. The
/// per-source breakdown is a follow-up (hover).
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

        // Anchor to the top-right corner and grow left/down so the window hugs the corner at any size. Dragging the
        // header detaches it to free positioning (see DragHandle). The panel ignores the mouse so clicks fall through
        // to the game; only the header (drag) and the rows (hover) take input.
        _panel = new PanelContainer
        {
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.End,
            OffsetTop = 12f,
            OffsetRight = -12f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.06f, 0.82f),
            BorderColor = new Color(1f, 1f, 1f, 0.22f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
        });

        var root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddThemeConstantOverride("separation", 0);

        var header = new DragHandle { CustomMinimumSize = new Vector2(0f, 18f) };
        header.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.06f),
            BorderColor = new Color(1f, 1f, 1f, 0.12f),
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
        });
        header.Init(_panel);

        var body = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddThemeConstantOverride("margin_left", 8);
        body.AddThemeConstantOverride("margin_right", 8);
        body.AddThemeConstantOverride("margin_top", 6);
        body.AddThemeConstantOverride("margin_bottom", 6);

        _list = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddChild(_list);

        root.AddChild(header);
        root.AddChild(body);
        _panel.AddChild(root);
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
            widget.Container.TooltipText = Breakdown(row);
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

        var name = new Label
        {
            CustomMinimumSize = new Vector2(96f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        name.AddThemeColorOverride("font_color", Colors.White);

        var bar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 1d,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(120f, 14f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("e0b341") });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.12f) });

        var rdps = new Label
        {
            CustomMinimumSize = new Vector2(44f, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rdps.AddThemeColorOverride("font_color", Colors.White);

        // The row takes the mouse so hovering it shows the per-source breakdown as a tooltip; its children ignore the
        // mouse so the whole row is one hover target.
        var container = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop };
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

    // The itemized rDPS the hover tooltip shows, read the way the summary reads: raw damage dealt by card, then the
    // damage this player's buffs gave to each teammate, then the damage teammates' buffs gave to this player's hits.
    private static string Breakdown(RdpsRow row)
    {
        var text = new System.Text.StringBuilder();
        text.Append(row.Name).Append("   aDPS ").Append(Round(row.ADps))
            .Append("  +given ").Append(Round(row.Given))
            .Append("  -recv ").Append(Round(row.Received))
            .Append("  =  rDPS ").Append(Round(row.Rdps));

        Section(text, "Dealt", row.Dealt.Select(d => ($"{d.Card}", d.Amount)));
        Section(text, "Given", row.GivenBy.Select(g => ($"{g.Effect} → {CombatLedger.Instance.NameOf(g.Other)}", g.Amount)));
        Section(text, "Received", row.ReceivedBy.Select(r => ($"{r.Effect} ← {CombatLedger.Instance.NameOf(r.Other)}", r.Amount)));
        return text.ToString();
    }

    private static void Section(System.Text.StringBuilder text, string title, IEnumerable<(string Label, decimal Amount)> rows)
    {
        var list = rows.Where(r => Round(r.Amount) != 0m).ToList();
        if (list.Count == 0)
        {
            return;
        }

        text.Append('\n').Append(title).Append(':');
        foreach ((string label, decimal amount) in list)
        {
            text.Append("\n  ").Append(label).Append(' ').Append(Round(amount));
        }
    }
}

/// <summary>
/// The overlay's title bar: an empty strip that grabs the mouse and drags the whole panel while the left button is
/// held, clamped so the window can't be dragged off-screen. Kept separate from the panel so it is the one and only
/// part of the overlay that intercepts input.
/// </summary>
internal sealed partial class DragHandle : Panel
{
    private Control _target = null!;
    private bool _dragging;
    private bool _detached;
    private Vector2 _grabOffset;

    public void Init(Control target)
    {
        _target = target;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (button.Pressed)
            {
                Detach();
                _grabOffset = GetGlobalMousePosition() - _target.Position;
            }

            AcceptEvent();
        }
        else if (@event is InputEventMouseMotion && _dragging)
        {
            Vector2 position = GetGlobalMousePosition() - _grabOffset;
            Vector2 viewport = GetViewportRect().Size;
            position.X = Mathf.Clamp(position.X, 0f, Mathf.Max(0f, viewport.X - _target.Size.X));
            position.Y = Mathf.Clamp(position.Y, 0f, Mathf.Max(0f, viewport.Y - _target.Size.Y));
            _target.Position = position;
            AcceptEvent();
        }
    }

    // On the first drag, freeze the window's current corner-anchored spot and switch to free positioning, so drags
    // move it by Position instead of fighting the anchors.
    private void Detach()
    {
        if (_detached)
        {
            return;
        }

        Vector2 position = _target.GlobalPosition;
        _target.AnchorLeft = 0f;
        _target.AnchorTop = 0f;
        _target.AnchorRight = 0f;
        _target.AnchorBottom = 0f;
        _target.GrowHorizontal = GrowDirection.End;
        _target.GrowVertical = GrowDirection.End;
        _target.Position = position;
        _detached = true;
    }
}
