using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace RdpsMeter;

/// <summary>
/// The live in-combat rDPS meter. A self-owned CanvasLayer parented to the scene root (rather than the game's own UI
/// tree) so it draws on top of everything without inheriting the game's layout or theme. It shows one row per player in
/// the combat - every player from the start, at zero, so the window's width is fixed and its height depends only on the
/// party size - with the player's name, a bar tinted to their class colour, and their rDPS. The panel is a bordered
/// window that starts near the top-right and can be dragged by its header; only the header (drag) and the rows (hover)
/// take the mouse, so the rest never intercepts a click meant for the game underneath. Hovering a row pops an instant
/// styled breakdown of that player's damage - the same table-with-bars look. Hidden whenever a combat is not running.
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
    // Fixed widths so neither window reflows as names or numbers change; only the row count drives height.
    private const float Width = 300f;
    private const float TooltipWidth = 320f;

    private sealed class Row
    {
        public required HBoxContainer Container { get; init; }
        public required Label Name { get; init; }
        public required ProgressBar Bar { get; init; }
        public required Label Rdps { get; init; }
        public required Color Color { get; init; }
    }

    private readonly Dictionary<ulong, Row> _rows = new();
    private PanelContainer _panel = null!;
    private VBoxContainer _list = null!;
    private PanelContainer _tooltip = null!;
    private VBoxContainer _tooltipList = null!;
    private IReadOnlyDictionary<ulong, RdpsRow> _snapshot = new Dictionary<ulong, RdpsRow>();
    private ulong? _hovered;
    private string? _tooltipSignature;

    public override void _Ready()
    {
        Layer = 128;

        // Anchor to the top-right, dropped down past the build/version text and a little in off the edge. Dragging the
        // header detaches it to free positioning. The panel ignores the mouse so clicks fall through to the game.
        _panel = new PanelContainer
        {
            AnchorLeft = 1f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.End,
            OffsetTop = 144f,
            OffsetRight = -40f,
            CustomMinimumSize = new Vector2(Width, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _panel.AddThemeStyleboxOverride("panel", WindowStyle());

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
        _list.AddThemeConstantOverride("separation", 4);
        body.AddChild(_list);

        root.AddChild(header);
        root.AddChild(body);
        _panel.AddChild(root);
        AddChild(_panel);

        // The hover breakdown: a matching floating window of the same table-with-bars rows, positioned by hand and
        // shown only while a row is hovered.
        _tooltip = new PanelContainer
        {
            CustomMinimumSize = new Vector2(TooltipWidth, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _tooltip.AddThemeStyleboxOverride("panel", WindowStyle(contentMargin: true));
        _tooltipList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _tooltipList.AddThemeConstantOverride("separation", 4);
        _tooltip.AddChild(_tooltipList);
        AddChild(_tooltip);
    }

    public override void _Process(double delta)
    {
        bool inCombat = CombatManager.Instance is { IsInProgress: true };
        _panel.Visible = inCombat;
        if (!inCombat)
        {
            _tooltip.Visible = false;
            return;
        }

        _snapshot = CombatLedger.Instance.Snapshot().ToDictionary(r => r.NetId);

        IReadOnlyList<Player> players = CombatManager.Instance?.DebugOnlyGetState()?.Players ?? Array.Empty<Player>();
        List<Player> ordered = players
            .OrderByDescending(p => _snapshot.TryGetValue(p.NetId, out RdpsRow? r) ? r.Rdps : 0m)
            .ThenBy(p => p.NetId)
            .ToList();

        decimal max = 1m;
        foreach (Player player in ordered)
        {
            if (_snapshot.TryGetValue(player.NetId, out RdpsRow? r) && r.Rdps > max)
            {
                max = r.Rdps;
            }
        }

        var seen = new HashSet<ulong>();
        int index = 0;
        foreach (Player player in ordered)
        {
            seen.Add(player.NetId);
            Row widget = Ensure(player);
            decimal rdps = _snapshot.TryGetValue(player.NetId, out RdpsRow? row) ? row.Rdps : 0m;
            widget.Rdps.Text = Round(rdps).ToString();
            widget.Bar.Value = (double)Math.Clamp(rdps / max, 0m, 1m);
            _list.MoveChild(widget.Container, index++);
        }

        foreach (ulong netId in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows[netId].Container.QueueFree();
            _rows.Remove(netId);
        }

        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_hovered is not { } netId || !_rows.TryGetValue(netId, out Row? widget))
        {
            _tooltip.Visible = false;
            _tooltipSignature = null;
            return;
        }

        RdpsRow? row = _snapshot.GetValueOrDefault(netId);

        // Rebuild the breakdown rows only when the content actually changes, so a still hover costs nothing.
        string signature = Signature(netId, row);
        if (signature != _tooltipSignature)
        {
            RebuildTooltip(row, widget.Color);
            _tooltipSignature = signature;
        }

        _tooltip.Visible = true;

        // Sit to the right of the main window, level with the hovered row, flipping to the left if there is no room.
        Vector2 viewport = _panel.GetViewportRect().Size;
        float x = _panel.GlobalPosition.X + _panel.Size.X + 6f;
        if (x + _tooltip.Size.X > viewport.X)
        {
            x = _panel.GlobalPosition.X - _tooltip.Size.X - 6f;
        }

        float y = Mathf.Clamp(widget.Container.GlobalPosition.Y, 0f, Mathf.Max(0f, viewport.Y - _tooltip.Size.Y));
        _tooltip.Position = new Vector2(Mathf.Max(0f, x), y);
    }

    // The hover breakdown, FFXIV-style but as a table of bars: this player's raw damage by card, then the buffs they
    // gave other players, then the buffs other players gave them. Each section's bars are scaled to that section's own
    // biggest entry, and tinted to the player's class colour. Name and rDPS are omitted - the hovered row shows them.
    private void RebuildTooltip(RdpsRow? row, Color color)
    {
        while (_tooltipList.GetChildCount() > 0)
        {
            Node child = _tooltipList.GetChild(0);
            _tooltipList.RemoveChild(child);
            child.QueueFree();
        }

        if (row == null)
        {
            _tooltipList.AddChild(SectionHeader("No damage yet."));
            return;
        }

        List<(string Card, decimal Amount, decimal Buff)> dealt = row.Dealt.Where(d => Round(d.Amount) != 0m).ToList();
        if (dealt.Count > 0)
        {
            decimal max = Math.Max(1m, dealt.Max(d => d.Amount));
            _tooltipList.AddChild(SectionHeader("Damage Breakdown"));
            foreach ((string card, decimal amount, decimal buff) in dealt)
            {
                _tooltipList.AddChild(SplitRow(card, amount, buff, max, color));
            }
        }

        AddEffectSection("Given", Combine(row.GivenBy), "+", color);
        AddEffectSection("Received", Combine(row.ReceivedBy), "-", color);
    }

    // Sum an effect list across the players it went to / came from, so the breakdown shows one bar per effect rather
    // than one per teammate.
    private static List<(string Effect, decimal Amount)> Combine(IReadOnlyList<(string Effect, ulong Other, decimal Amount)> source)
    {
        return source
            .GroupBy(e => e.Effect)
            .Select(g => (g.Key, g.Sum(e => e.Amount)))
            .Where(e => Round(e.Item2) != 0m)
            .OrderByDescending(e => e.Item2)
            .ToList();
    }

    private void AddEffectSection(string title, List<(string Effect, decimal Amount)> items, string sign, Color color)
    {
        if (items.Count == 0)
        {
            return;
        }

        decimal max = Math.Max(1m, items.Max(i => i.Amount));
        _tooltipList.AddChild(SectionHeader(title));
        foreach ((string effect, decimal amount) in items)
        {
            _tooltipList.AddChild(BreakdownRow(effect, sign + Round(amount), (double)Math.Clamp(amount / max, 0m, 1m), color));
        }
    }

    private string Signature(ulong netId, RdpsRow? row)
    {
        if (row == null)
        {
            return $"{netId}:none";
        }

        var text = new System.Text.StringBuilder();
        text.Append(netId);
        foreach ((string card, decimal amount, decimal buff) in row.Dealt)
        {
            text.Append('|').Append(card).Append(Round(amount)).Append('b').Append(Round(buff));
        }

        foreach ((string effect, decimal amount) in Combine(row.GivenBy))
        {
            text.Append("|g").Append(effect).Append(Round(amount));
        }

        foreach ((string effect, decimal amount) in Combine(row.ReceivedBy))
        {
            text.Append("|r").Append(effect).Append(Round(amount));
        }

        return text.ToString();
    }

    private Row Ensure(Player player)
    {
        ulong netId = player.NetId;
        if (_rows.TryGetValue(netId, out Row? existing))
        {
            return existing;
        }

        Color color = player.Character.NameColor;

        var name = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        name.AddThemeColorOverride("font_color", Colors.White);
        name.Text = PlayerIdentity.Name(player);

        ProgressBar bar = MakeBar(color, new Vector2(120f, 14f));

        var rdps = new Label
        {
            CustomMinimumSize = new Vector2(56f, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rdps.AddThemeColorOverride("font_color", Colors.White);

        // The row takes the mouse so hovering it drives the breakdown; its children ignore it so the whole row is one
        // hover target.
        var container = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        container.AddThemeConstantOverride("separation", 8);
        container.AddChild(name);
        container.AddChild(bar);
        container.AddChild(rdps);
        container.MouseEntered += () => _hovered = netId;
        container.MouseExited += () =>
        {
            if (_hovered == netId)
            {
                _hovered = null;
            }
        };
        _list.AddChild(container);

        var widget = new Row { Container = container, Name = name, Bar = bar, Rdps = rdps, Color = color };
        _rows[netId] = widget;
        return widget;
    }

    private static HBoxContainer BreakdownRow(string label, string valueText, double fraction, Color color)
    {
        ProgressBar bar = MakeBar(color, new Vector2(96f, 12f));
        bar.Value = fraction;
        return Row3(RowLabel(label, expand: true), bar, RowLabel(valueText, expand: false));
    }

    // A Damage Breakdown row whose bar is split: a solid segment for the card's own damage and a fainter segment for
    // the part teammates' buffs added, together spanning the card's total (scaled to the section's biggest card).
    private static HBoxContainer SplitRow(string label, decimal total, decimal buff, decimal max, Color color)
    {
        return Row3(RowLabel(label, expand: true), SplitBar(total - buff, total, max, color), RowLabel(Round(total).ToString(), expand: false));
    }

    private static Control SplitBar(decimal own, decimal total, decimal max, Color color)
    {
        var holder = new Control
        {
            CustomMinimumSize = new Vector2(96f, 12f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        // Back: neutral track with a faint fill up to the card's whole contribution.
        ProgressBar back = MakeBar(new Color(color.R, color.G, color.B, 0.35f), Vector2.Zero);
        back.Value = (double)Math.Clamp(total / max, 0m, 1m);
        holder.AddChild(back);
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Front: no track, a solid fill up to the card's own damage, overlaying the left of the faint segment.
        ProgressBar front = MakeBar(color, Vector2.Zero);
        front.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0f) });
        front.Value = (double)Math.Clamp(own / max, 0m, 1m);
        holder.AddChild(front);
        front.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        return holder;
    }

    private static Label RowLabel(string text, bool expand)
    {
        var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeColorOverride("font_color", Colors.White);
        label.Text = text;
        if (expand)
        {
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            label.ClipText = true;
        }
        else
        {
            label.CustomMinimumSize = new Vector2(52f, 0f);
            label.HorizontalAlignment = HorizontalAlignment.Right;
        }

        return label;
    }

    private static HBoxContainer Row3(Control left, Control middle, Control right)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(left);
        row.AddChild(middle);
        row.AddChild(right);
        return row;
    }

    private static Label SectionHeader(string title)
    {
        var header = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        header.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.55f));
        header.Text = title;
        return header;
    }

    private static ProgressBar MakeBar(Color fill, Vector2 minSize)
    {
        var bar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 1d,
            ShowPercentage = false,
            CustomMinimumSize = minSize,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = fill });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.12f) });
        return bar;
    }

    private static StyleBoxFlat WindowStyle(bool contentMargin = false)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.06f, 0.9f),
            BorderColor = new Color(1f, 1f, 1f, 0.22f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5,
        };

        if (contentMargin)
        {
            style.ContentMarginLeft = 8f;
            style.ContentMarginRight = 8f;
            style.ContentMarginTop = 6f;
            style.ContentMarginBottom = 6f;
        }

        return style;
    }

    private static decimal Round(decimal value)
    {
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The overlay's title bar: an empty strip that grabs the mouse and drags the whole window while the left button is
/// held, clamped so it can't be dragged off-screen. Kept separate from the panel so it is the one and only part of the
/// overlay that intercepts input.
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

    // On the first drag, freeze the window's current anchored spot and switch to free positioning, so drags move it by
    // Position instead of fighting the anchors.
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
