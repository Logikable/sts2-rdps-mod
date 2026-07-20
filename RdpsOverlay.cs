using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace RdpsMeter;

/// <summary>
/// The live in-combat rDPS meter. A self-owned CanvasLayer parented to the scene root (rather than the game's own UI
/// tree) so it draws on top of everything without inheriting the game's layout or theme. It shows one row per player in
/// the combat - every player from the start, at zero, so the window's width is fixed and its height depends only on the
/// party size - with the player's name, a bar tinted to their class colour, and their rDPS. The panel is a bordered
/// window that starts near the top-right and can be dragged by its header; only the header (drag), its Live/Total
/// button and the rows (hover) take the mouse, so the rest never intercepts a click meant for the game underneath.
/// Hovering a row pops an instant styled breakdown of that player's damage - the same table-with-bars look. It stays
/// up between fights, showing either this combat's damage or the running session total per the header toggle, and
/// hides only before the first fight or when the shown tally is empty out of combat.
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
        public required Control Container { get; init; }
        public required ProgressBar Bar { get; init; }
        public required Label Rdps { get; init; }
        public required Label Percent { get; init; }
        public required Color Color { get; init; }
    }

    // A player's look, captured while they are on-screen so their row keeps its class colour, icon and name after
    // combat ends and the live combat state (the only place these come from) is gone.
    private readonly record struct PlayerVisual(Color Color, Texture2D? Icon, string Name);

    private readonly Dictionary<ulong, Row> _rows = new();
    private readonly Dictionary<ulong, PlayerVisual> _visuals = new();
    private PanelContainer _panel = null!;
    private DragHandle _header = null!;
    private Button _toggle = null!;
    private VBoxContainer _list = null!;
    private PanelContainer _tooltip = null!;
    private VBoxContainer _tooltipList = null!;
    private IReadOnlyDictionary<ulong, RdpsRow> _snapshot = new Dictionary<ulong, RdpsRow>();
    private ulong? _hovered;
    private string? _tooltipSignature;
    private bool _clampPending;

    // false = show this combat's tally, true = show the running total across every fight this session.
    private bool _showTotal;

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

        _header = new DragHandle { CustomMinimumSize = new Vector2(0f, 24f) };
        _header.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.06f),
            BorderColor = new Color(1f, 1f, 1f, 0.12f),
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
        });
        _header.Init(_panel, OverlayLayout.SavePosition);

        var title = new Label
        {
            Text = "rDPS",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.85f));
        _header.AddChild(title);
        title.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Live/Total switch, pinned to the right of the header. It takes the mouse (so a click toggles rather than
        // starts a drag) while the rest of the header stays a drag surface.
        _toggle = new Button
        {
            Text = "Live",
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -60f,
            OffsetRight = -6f,
            OffsetTop = -10f,
            OffsetBottom = 10f,
        };
        _toggle.AddThemeFontSizeOverride("font_size", 12);
        _toggle.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.85f));
        _toggle.AddThemeColorOverride("font_hover_color", Colors.White);
        _toggle.AddThemeStyleboxOverride("normal", ToggleStyle(0.10f));
        _toggle.AddThemeStyleboxOverride("hover", ToggleStyle(0.18f));
        _toggle.AddThemeStyleboxOverride("pressed", ToggleStyle(0.24f));
        _toggle.Pressed += () =>
        {
            _showTotal = !_showTotal;
            _toggle.Text = _showTotal ? "Total" : "Live";
        };
        _header.AddChild(_toggle);

        var body = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddThemeConstantOverride("margin_left", 10);
        body.AddThemeConstantOverride("margin_right", 10);
        body.AddThemeConstantOverride("margin_top", 6);
        body.AddThemeConstantOverride("margin_bottom", 6);

        _list = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _list.AddThemeConstantOverride("separation", 4);
        body.AddChild(_list);

        root.AddChild(_header);
        root.AddChild(body);
        _panel.AddChild(root);
        AddChild(_panel);

        // Restore the last-used spot if there is one; otherwise the default top-right anchoring stands.
        if (OverlayLayout.LoadPosition() is Vector2 saved)
        {
            FreePosition(_panel, saved);
            _header.MarkDetached();
            _clampPending = true;
        }

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

        // Capture every live player's class colour, icon and name while combat is running, so their rows keep the
        // right look after the fight ends and the combat state is gone.
        IReadOnlyList<Player> livePlayers = inCombat
            ? CombatManager.Instance?.DebugOnlyGetState()?.Players ?? Array.Empty<Player>()
            : Array.Empty<Player>();
        foreach (Player player in livePlayers)
        {
            _visuals[player.NetId] =
                new PlayerVisual(player.Character.NameColor, player.Character.IconTexture, PlayerIdentity.Name(player));
        }

        _snapshot = (_showTotal ? CombatLedger.Total : CombatLedger.Current).Snapshot().ToDictionary(r => r.NetId);

        // Stay up between fights: visible during combat, and afterwards for as long as the shown tally still holds
        // damage. Only truly empty (before the first fight, or a wiped current tally out of combat) hides it.
        bool visible = inCombat || _snapshot.Count > 0;
        _panel.Visible = visible;
        if (!visible)
        {
            _tooltip.Visible = false;
            return;
        }

        // A restored position could be off-screen if the resolution shrank since; pull it back into view once, after
        // the window has a measured size.
        if (_clampPending && _panel.Size.X > 0f)
        {
            Vector2 view = _panel.GetViewportRect().Size;
            _panel.Position = new Vector2(
                Mathf.Clamp(_panel.Position.X, 0f, Mathf.Max(0f, view.X - _panel.Size.X)),
                Mathf.Clamp(_panel.Position.Y, 0f, Mathf.Max(0f, view.Y - _panel.Size.Y)));
            _clampPending = false;
        }

        // Show every player with a tally, plus any live player yet to deal damage, so the party appears at zero from
        // the start of a fight.
        var netIds = new HashSet<ulong>(_snapshot.Keys);
        foreach (Player player in livePlayers)
        {
            netIds.Add(player.NetId);
        }

        List<ulong> ordered = netIds
            .OrderByDescending(id => _snapshot.TryGetValue(id, out RdpsRow? r) ? r.Rdps : 0m)
            .ThenBy(id => id)
            .ToList();

        decimal max = 1m;
        decimal team = 0m;
        foreach (ulong id in ordered)
        {
            if (_snapshot.TryGetValue(id, out RdpsRow? r))
            {
                team += r.Rdps;
                if (r.Rdps > max)
                {
                    max = r.Rdps;
                }
            }
        }

        var seen = new HashSet<ulong>();
        int index = 0;
        foreach (ulong id in ordered)
        {
            seen.Add(id);
            Row widget = Ensure(id);
            decimal rdps = _snapshot.TryGetValue(id, out RdpsRow? row) ? row.Rdps : 0m;
            widget.Rdps.Text = Round(rdps).ToString();
            widget.Percent.Text = team > 0m ? $"{Round(rdps / team * 100m)}%" : "0%";
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

        AddDamageSection(row.Dealt.Where(d => Round(d.Amount) != 0m).ToList(), color);
        AddEffectSection("Given", Combine(row.GivenBy), "+", color);
        AddEffectSection("Received", Combine(row.ReceivedBy), "-", color);
    }

    private void AddDamageSection(List<(string Card, decimal Amount, decimal Buff)> items, Color color)
    {
        if (items.Count == 0)
        {
            return;
        }

        decimal max = Math.Max(1m, items.Max(i => i.Amount));
        decimal total = items.Sum(i => i.Amount);
        _tooltipList.AddChild(SectionHeader("Damage Breakdown"));
        foreach ((string card, decimal amount, decimal buff) in items)
        {
            _tooltipList.AddChild(BarRow(card, Round(amount).ToString(), Percent(amount, total), SplitBackground(amount - buff, amount, max, color)));
        }
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
        decimal total = items.Sum(i => i.Amount);
        _tooltipList.AddChild(SectionHeader(title));
        foreach ((string effect, decimal amount) in items)
        {
            _tooltipList.AddChild(BarRow(effect, sign + Round(amount), Percent(amount, total), EffectBackground(amount, max, color)));
        }
    }

    private static string Percent(decimal amount, decimal total)
    {
        return total > 0m ? $"{Round(amount / total * 100m)}%" : "0%";
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

    private Row Ensure(ulong netId)
    {
        if (_rows.TryGetValue(netId, out Row? existing))
        {
            return existing;
        }

        // Prefer the look captured while the player was live; fall back to a neutral tint and the ledger's resolved
        // name for a player we somehow never saw on-screen (e.g. a tally restored with no live combat).
        PlayerVisual visual = _visuals.TryGetValue(netId, out PlayerVisual cached)
            ? cached
            : new PlayerVisual(new Color(0.7f, 0.7f, 0.7f), null, _snapshot.GetValueOrDefault(netId)?.Name ?? netId.ToString());
        Color color = visual.Color;

        // The row takes the mouse so hovering it drives the breakdown; its children ignore it so the whole row is one
        // hover target.
        var container = new Control
        {
            CustomMinimumSize = new Vector2(0f, 22f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };

        // Background: a full-width bar behind the text, tinted to the class colour but translucent so text stays legible.
        var bar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 1d,
            ShowPercentage = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("fill", RowBarStyle(new Color(color.R, color.G, color.B, 0.55f)));
        bar.AddThemeStyleboxOverride("background", RowBarStyle(new Color(1f, 1f, 1f, 0.05f)));
        container.AddChild(bar);
        bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Foreground: class icon + name on the left, rDPS + team share on the right, over the bar.
        var icon = new TextureRect
        {
            Texture = visual.Icon,
            CustomMinimumSize = new Vector2(18f, 18f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        Label name = OverlayLabel(visual.Name);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.ClipText = true;

        Label rdps = OverlayLabel(string.Empty);
        rdps.CustomMinimumSize = new Vector2(48f, 0f);
        rdps.HorizontalAlignment = HorizontalAlignment.Right;

        Label percent = OverlayLabel(string.Empty);
        percent.CustomMinimumSize = new Vector2(40f, 0f);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        percent.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.7f));

        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 6);
        line.AddChild(icon);
        line.AddChild(name);
        line.AddChild(rdps);
        line.AddChild(percent);

        var overlay = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.AddThemeConstantOverride("margin_left", 6);
        overlay.AddThemeConstantOverride("margin_right", 6);
        overlay.AddChild(line);
        container.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        container.MouseEntered += () => _hovered = netId;
        container.MouseExited += () =>
        {
            if (_hovered == netId)
            {
                _hovered = null;
            }
        };
        _list.AddChild(container);

        var widget = new Row { Container = container, Bar = bar, Rdps = rdps, Percent = percent, Color = color };
        _rows[netId] = widget;
        return widget;
    }

    private static Label OverlayLabel(string text)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        label.AddThemeConstantOverride("outline_size", 4);
        return label;
    }

    // The header's Live/Total button: a faint rounded chip that brightens on hover and press.
    private static StyleBoxFlat ToggleStyle(float alpha)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, alpha),
            BorderColor = new Color(1f, 1f, 1f, 0.18f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
    }

    private static StyleBoxFlat RowBarStyle(Color color)
    {
        return new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };
    }

    private static void FreePosition(Control panel, Vector2 position)
    {
        panel.AnchorLeft = 0f;
        panel.AnchorTop = 0f;
        panel.AnchorRight = 0f;
        panel.AnchorBottom = 0f;
        panel.GrowHorizontal = Control.GrowDirection.End;
        panel.GrowVertical = Control.GrowDirection.End;
        panel.Position = position;
    }

    // A breakdown row in the same layered style as the main overlay: the given background bar spans the row with the
    // label (left), value (right) and its share of the section (right) drawn over it.
    private static Control BarRow(string label, string valueText, string percentText, Control background)
    {
        var container = new Control { CustomMinimumSize = new Vector2(0f, 20f), MouseFilter = Control.MouseFilterEnum.Ignore };
        container.AddChild(background);
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        Label text = OverlayLabel(label);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        text.ClipText = true;

        Label value = OverlayLabel(valueText);
        value.CustomMinimumSize = new Vector2(48f, 0f);
        value.HorizontalAlignment = HorizontalAlignment.Right;

        Label percent = OverlayLabel(percentText);
        percent.CustomMinimumSize = new Vector2(40f, 0f);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        percent.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.7f));

        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 6);
        line.AddChild(text);
        line.AddChild(value);
        line.AddChild(percent);

        var overlay = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.AddThemeConstantOverride("margin_left", 6);
        overlay.AddThemeConstantOverride("margin_right", 6);
        overlay.AddChild(line);
        container.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return container;
    }

    private static ProgressBar EffectBackground(decimal amount, decimal max, Color color)
    {
        var bar = new ProgressBar { MinValue = 0d, MaxValue = 1d, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("fill", RowBarStyle(new Color(color.R, color.G, color.B, 0.55f)));
        bar.AddThemeStyleboxOverride("background", RowBarStyle(new Color(1f, 1f, 1f, 0.05f)));
        bar.Value = (double)Math.Clamp(amount / max, 0m, 1m);
        return bar;
    }

    // The Damage Breakdown bar, split: a solid segment for the card's own damage and a fainter same-colour segment for
    // the part teammates' buffs added, together spanning the card's total (scaled to the section's biggest card).
    private static Control SplitBackground(decimal own, decimal total, decimal max, Color color)
    {
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };

        var back = new ProgressBar { MinValue = 0d, MaxValue = 1d, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        back.AddThemeStyleboxOverride("fill", RowBarStyle(new Color(color.R, color.G, color.B, 0.28f)));
        back.AddThemeStyleboxOverride("background", RowBarStyle(new Color(1f, 1f, 1f, 0.05f)));
        back.Value = (double)Math.Clamp(total / max, 0m, 1m);
        holder.AddChild(back);
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var front = new ProgressBar { MinValue = 0d, MaxValue = 1d, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        front.AddThemeStyleboxOverride("fill", RowBarStyle(new Color(color.R, color.G, color.B, 0.55f)));
        front.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0f) });
        front.Value = (double)Math.Clamp(own / max, 0m, 1m);
        holder.AddChild(front);
        front.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        return holder;
    }

    // A section title styled like the window header: a tinted strip with a centered, larger label.
    private static Control SectionHeader(string title)
    {
        var strip = new Panel { CustomMinimumSize = new Vector2(0f, 22f), MouseFilter = Control.MouseFilterEnum.Ignore };
        strip.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.08f),
            BorderColor = new Color(1f, 1f, 1f, 0.14f),
            BorderWidthBottom = 1,
        });

        var label = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 16);
        label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.85f));
        strip.AddChild(label);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return strip;
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
    private Action<Vector2>? _onDragEnd;
    private bool _dragging;
    private bool _detached;
    private Vector2 _grabOffset;

    public void Init(Control target, Action<Vector2> onDragEnd)
    {
        _target = target;
        _onDragEnd = onDragEnd;
        MouseFilter = MouseFilterEnum.Stop;
    }

    // Treat the window as already free-positioned (e.g. restored to a saved spot) so the next drag doesn't re-anchor it.
    public void MarkDetached()
    {
        _detached = true;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                Detach();
                _grabOffset = GetGlobalMousePosition() - _target.Position;
                _dragging = true;
            }
            else if (_dragging)
            {
                _dragging = false;
                _onDragEnd?.Invoke(_target.Position);
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
