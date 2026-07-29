using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace RdpsMeter;

/// <summary>
/// The live in-combat damage meter. A self-owned CanvasLayer parented to the scene root (rather than the game's own UI
/// tree) so it draws on top of everything without inheriting the game's layout or theme. It shows one row per player in
/// the combat - every player from the start, at zero, so the window's width is fixed and its height depends only on the
/// party size - with the player's name, a bar tinted to their class colour, and their number. The panel is a bordered
/// window that starts near the top-right and can be dragged by its header; only the header's controls and the rows
/// (hover) take the mouse, so the rest never intercepts a click meant for the game underneath. Hovering a row pops an
/// instant styled breakdown of that player's damage - the same table-with-bars look. In a solo run there is nobody to
/// credit, so the row and its hover collapse into one: the panel shows the breakdown directly and carries the total in
/// its header.
///
/// The header carries two independent choices. The arrows either side of the title page between meters (see
/// <see cref="MeterMode"/>) - rDPS, which moves damage to whoever's buffs bought it; aDPS, the damage the player
/// themselves dealt; and Blocked, the damage their block stopped - and the button on the right picks which tally to read
/// it over: this combat, the run total, or one earlier fight. The meter is remembered between sessions; the tally resets
/// to the run total with each run. The title between them names the meter and carries its number - the player's own
/// alone, the party's summed in co-op - so the header states the answer and the body breaks it down.
///
/// Hard against the right edge, the minus collapses the window and becomes the plus that opens it again. Collapsed,
/// the window is that plus and nothing else: a small square, half again the header's height so it is easy to spot and
/// to hit, which is also still the drag handle. Everything else goes - the body, the arrows, the tally picker, and the
/// title too, since a title the window is too narrow to finish is worse than none. That state is remembered between
/// sessions, safely, because what is left on screen is the button that brings it all back.
///
/// Nothing short of an empty run takes the window away - not ending a fight, not leaving the run for the main menu - so
/// the numbers stay readable after the match; see <see cref="ShouldShow"/>.
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

    /// <summary>
    /// Whether the window should be on screen. Nothing about leaving a fight, a room or the run itself takes it away:
    /// once the loaded run has recorded anybody, the meter stays up so its numbers can still be read at the shop, on the
    /// map or back at the main menu. Only a run nothing has been recorded for yet - a fresh install, or a brand-new run
    /// before its first fight - draws no window, since there would be nothing in it. Deliberately asks the run rather
    /// than the picked view, so picking an empty fight cannot make the whole window vanish.
    ///
    /// The run history page is the one place an empty meter is worth drawing: a fight there whose numbers we do not
    /// have is answered with an empty window under that fight's name, which says "nothing recorded" where no window at
    /// all would just look broken.
    /// </summary>
    internal static bool ShouldShow(bool inCombat)
    {
        return inCombat || RunLedger.HasData || RunHistoryView.Fight != null;
    }
}

internal sealed partial class RdpsOverlayNode : CanvasLayer
{
    // One fixed width for both windows, so nothing reflows as names or numbers change and the hover breakdown lines up
    // with the panel it came from; only the row count drives height. Anything too long for its column is cut short
    // rather than allowed to widen the window, so every label in a row must either reserve a column or clip.
    private const float Width = 320f;

    // Reserved columns for the two right-hand numbers. Generous enough that realistic tallies never reach the trim,
    // since the name beside them is the cheaper thing to shorten.
    private const float ValueColumn = 56f;
    private const float PercentColumn = 44f;

    // The header's zones, measured in from the right edge: the minimize button hard against the edge, the view picker
    // left of it, and left of that the meter title flanked by its arrows. Fixed sizes rather than a container, so
    // neither a long fight name nor a long title can push the other around - or the window wider (see the Width note
    // above). The picker is what gave up the room the minimize button needed, rather than the title: a fight's name
    // already clips happily, while the title is the one thing here meant to be read at a glance.
    private const float MenuWidth = 96f;
    private const float ArrowWidth = 24f;
    private const float MinimizeWidth = 22f;

    // The right-hand edge everything but the minimize button is measured in from: the window's own margin, the button,
    // and a hair of space so the picker's chip does not touch it.
    private const float MenuInset = 6f + MinimizeWidth + 2f;

    private const float HeaderHeight = 28f;

    // Collapsed, the whole window is this square and nothing else. Comfortably larger than the header it replaces
    // (~1.7x) on purpose: a collapsed window has no title to catch the eye, so the one thing left has to be big enough
    // to find and to hit without aiming.
    private const float MinimizedSide = 48f;

    // Warm blue, bright enough to read on the translucent header at the title's size.
    private static readonly Color TitleColor = new(0.541f, 0.706f, 0.973f);

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

    // Which tally the meter is showing: the whole run's total, the active/most-recent combat, or one picked fight.
    private enum ViewKind
    {
        Total,
        Current,
        Combat,
    }

    private readonly Dictionary<ulong, Row> _rows = new();
    private readonly Dictionary<ulong, PlayerVisual> _visuals = new();
    private PanelContainer _panel = null!;
    private DragHandle _header = null!;
    private Label _title = null!;
    private Button _prev = null!;
    private Button _next = null!;
    private MenuButton _menu = null!;
    private Button _minimize = null!;
    private MinimizeGlyph _glyph = null!;
    private MarginContainer _body = null!;
    private VBoxContainer _list = null!;
    private PanelContainer _tooltip = null!;
    private VBoxContainer _tooltipList = null!;
    private IReadOnlyDictionary<ulong, RdpsRow> _snapshot = new Dictionary<ulong, RdpsRow>();
    private ulong? _hovered;
    private string? _tooltipSignature;

    // The breakdown currently drawn in the panel body (solo only), so a steady tally costs nothing to redraw.
    private string? _bodySignature;
    private bool _clampPending;

    // The picked view, which starts on the run total. When Combat, _viewKey is the chosen fight's combat key; it falls
    // back to the total if that fight is no longer in the run (e.g. a new run wiped it).
    private ViewKind _viewKind = ViewKind.Total;
    private string? _viewKey;

    // Which meter the arrows have landed on. Restored from the config on the first frame, so the window comes back on
    // whichever one was last read rather than always on rDPS.
    private MeterMode _mode = OverlayLayout.LoadMode();

    // Whether the window is collapsed to its bare header, restored from the config the same way the meter is. Collapsed
    // it keeps only the title and the button that opens it again - so it is never lost, only got out of the way.
    private bool _minimized = OverlayLayout.LoadMinimized();

    // Whether the run being shown has nobody to credit, recomputed each frame. With no teammates, rDPS and aDPS are the
    // same number, so a solo run is offered one of them, drawn under the name they share, and the arrows page between
    // that and Blocked rather than through all three.
    private bool _solo;

    // The run generation the cached rows/visuals belong to; a change means a new run, so they must be rebuilt.
    private int _generation = -1;

    // The translation revision the drawn text belongs to; a change means the player switched language, so every label
    // must be redrawn in the new one (and given that language's font).
    private int _locale = -1;

    // Menu item ids: the combat's index for each fight, and two out-of-range ids for the fixed views. These must be
    // non-negative: PopupMenu.AddItem reassigns any negative id to the item's own index, which would collide the fixed
    // views with the first fights (Total onto fight 0, Live onto fight 1) and send every pick to the wrong view.
    private const int IdTotal = int.MaxValue;
    private const int IdCurrent = int.MaxValue - 1;

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

        _header = new DragHandle { CustomMinimumSize = new Vector2(0f, HeaderHeight) };
        _header.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.06f),
            BorderColor = new Color(1f, 1f, 1f, 0.12f),
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
        });
        _header.Init(_panel, _ => OverlayLayout.SavePosition(OpenPosition));

        // Which meter is being read, between the two arrows that page through them: the header's headline, so it is the
        // one thing in the window drawn large and in colour. It sits in the space left of the view picker and clips
        // rather than growing, like everything else here.
        _title = new Label
        {
            Text = ModeName(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true,
            AnchorLeft = 0f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 6f + ArrowWidth,
            OffsetRight = -(MenuWidth + MenuInset + ArrowWidth),
        };
        _title.AddThemeFontSizeOverride("font_size", 19);
        _title.AddThemeColorOverride("font_color", TitleColor);
        _header.AddChild(_title);

        // The arrows page between meters. They take the mouse, so a click switches instead of starting a header drag.
        _prev = ArrowButton("◀", left: true);
        _next = ArrowButton("▶", left: false);
        _prev.Pressed += () => StepMode(-1);
        _next.Pressed += () => StepMode(1);
        _header.AddChild(_prev);
        _header.AddChild(_next);

        // View picker, pinned to the right of the header: Total, Live, then one entry per fight. It takes the mouse (so
        // a click opens the menu rather than starting a drag) while the rest of the header stays a drag surface. The
        // menu is rebuilt each time it opens, so it always lists the fights seen so far.
        _menu = new MenuButton
        {
            Text = Loc.T("view.total"),

            // Godot's MenuButton constructs itself flat, and a flat button draws no stylebox at all - which is why the
            // chip below was invisible however firmly it was styled. Turn that off before styling it.
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ClipText = true,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -(MenuWidth + MenuInset),
            OffsetRight = -MenuInset,
            OffsetTop = -11f,
            OffsetBottom = 11f,
        };
        _menu.AddThemeFontSizeOverride("font_size", 12);
        _menu.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.9f));
        _menu.AddThemeColorOverride("font_hover_color", Colors.White);
        _menu.AddThemeStyleboxOverride("normal", ToggleStyle(0.18f));
        _menu.AddThemeStyleboxOverride("hover", ToggleStyle(0.26f));
        _menu.AddThemeStyleboxOverride("pressed", ToggleStyle(0.32f));

        PopupMenu popup = _menu.GetPopup();
        popup.AddThemeStyleboxOverride("panel", WindowStyle(contentMargin: true));
        popup.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.85f));
        popup.AddThemeColorOverride("font_hover_color", Colors.White);
        ApplyLocaleFonts();
        popup.AboutToPopup += RebuildMenu;
        popup.IdPressed += OnViewPicked;
        _header.AddChild(_menu);

        // The dropdown caret, drawn inside the button's right padding rather than appended to its caption: the caption
        // is the fight's name, which the self-test and the clipping both read, and neither wants a glyph glued to it.
        var caret = new Label
        {
            Text = "▾",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetRight = -5f,
        };
        caret.AddThemeFontSizeOverride("font_size", 12);
        caret.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.75f));
        _menu.AddChild(caret);

        // Collapse the window and open it again. It shows the inverse of whatever it just did - the minus that took
        // the window away becomes the plus that brings it back.
        //
        // The button carries no text: its mark is drawn as bars by MinimizeGlyph, centred on the button's own middle.
        // Set as a font glyph it was not centred and could not straightforwardly be made so - a font centres a line
        // box, not the ink inside it, and "+" and "-" sit differently within theirs, which is why the plus read as
        // high while the minus looked settled. Drawing it also sidesteps the question of whether the font has a real
        // minus sign at all.
        _minimize = new Button
        {
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorTop = 0f,
            AnchorBottom = 1f,
        };
        _minimize.Pressed += ToggleMinimized;
        _glyph = new MinimizeGlyph { MouseFilter = Control.MouseFilterEnum.Ignore };
        _minimize.AddChild(_glyph);
        _glyph.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _minimize.MouseEntered += () => _glyph.Tint(Colors.White);
        _minimize.MouseExited += () => _glyph.Tint(TitleColor);
        _header.AddChild(_minimize);

        _body = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("margin_left", 10);
        _body.AddThemeConstantOverride("margin_right", 10);
        _body.AddThemeConstantOverride("margin_top", 6);
        _body.AddThemeConstantOverride("margin_bottom", 6);

        _list = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _list.AddThemeConstantOverride("separation", 4);
        _body.AddChild(_list);

        root.AddChild(_header);
        root.AddChild(_body);
        _panel.AddChild(root);
        AddChild(_panel);
        ApplyMinimized();

        // Restore the last-used spot if there is one; otherwise the default top-right anchoring stands.
        if (OverlayLayout.LoadPosition() is Vector2 saved)
        {
            FreePosition(_panel, saved + ShiftFrom(_minimized));
            _header.MarkDetached();
            _clampPending = true;
        }

        // The hover breakdown: a matching floating window of the same table-with-bars rows, positioned by hand and
        // shown only while a row is hovered.
        _tooltip = new PanelContainer
        {
            CustomMinimumSize = new Vector2(Width, 0f),
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
        // A new (or reloaded) run replaces the roster: drop the cached rows and player visuals so they rebuild for the
        // character being played now instead of lingering as the previous run's, and return the picker to the total.
        int generation = RunLedger.Generation;
        if (generation != _generation)
        {
            _generation = generation;
            _viewKind = ViewKind.Total;
            _viewKey = null;
            _visuals.Clear();
            foreach (Node child in _list.GetChildren())
            {
                _list.RemoveChild(child);
                child.QueueFree();
            }

            _rows.Clear();
            _bodySignature = null;
        }

        // A language switch leaves every drawn label in the old language - the cached signatures only track the
        // numbers, so nothing would otherwise redraw. Drop them and the rows so the next pass rebuilds the text, and
        // re-font the header, which is built once and lives across the change.
        int locale = Loc.Revision;
        if (locale != _locale)
        {
            _locale = locale;
            ApplyLocaleFonts();
            foreach (Row row in _rows.Values)
            {
                _list.RemoveChild(row.Container);
                row.Container.QueueFree();
            }

            _rows.Clear();
            _bodySignature = null;
            _tooltipSignature = null;
        }

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

        _snapshot = SelectedView().ToDictionary(r => r.NetId);

        bool visible = RdpsOverlay.ShouldShow(inCombat);
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
            // Against the size the window is about to be, not the one it still has. Godot takes a frame to grow a
            // control to a minimum size that just went up, and opening a collapsed window puts it up by 272px - so
            // clamping against the square's own size would leave the reopened window hanging off the screen edge.
            Vector2 extent = _panel.Size.Max(_panel.GetCombinedMinimumSize());
            Vector2 view = _panel.GetViewportRect().Size;
            _panel.Position = new Vector2(
                Mathf.Clamp(_panel.Position.X, 0f, Mathf.Max(0f, view.X - extent.X)),
                Mathf.Clamp(_panel.Position.Y, 0f, Mathf.Max(0f, view.Y - extent.Y)));
            _clampPending = false;
        }

        // Godot grows a control to fit its minimum size but never shrinks it back when that minimum falls - and the
        // panel hangs off a CanvasLayer, so no container does it for us. Left alone, the window keeps the height of the
        // longest breakdown it has ever shown, and stays 320px wide after collapsing to a square. Assigning the size
        // every frame is what makes it track: Godot clamps the value back up to the minimum, so this can only ever
        // take away space nothing is asking for. It is not a second opinion on the width - the minimum is still the
        // panel's own CustomMinimumSize, which no content can reach - only the means of letting it fall.
        Vector2 wanted = _panel.GetCombinedMinimumSize();
        if (_panel.Size.X > wanted.X || _panel.Size.Y > wanted.Y)
        {
            _panel.Size = wanted;
        }

        // Show every player with a tally, plus any live player yet to deal damage, so the party appears at zero from
        // the start of a fight.
        var netIds = new HashSet<ulong>(_snapshot.Keys);
        foreach (Player player in livePlayers)
        {
            netIds.Add(player.NetId);
        }

        List<ulong> ordered = netIds
            .OrderByDescending(id => Value(_snapshot.GetValueOrDefault(id)))
            .ThenBy(id => id)
            .ToList();

        decimal max = 1m;
        decimal team = 0m;
        foreach (ulong id in ordered)
        {
            decimal value = Value(_snapshot.GetValueOrDefault(id));
            team += value;
            if (value > max)
            {
                max = value;
            }
        }

        // Solo: there is nobody to credit, so a one-row table hiding the interesting part behind a hover is just in the
        // way. The panel becomes the breakdown itself, and the row that would have carried the number is gone - which
        // is why the header carries it. In a party it carries the same number for the whole party, so collapsing the
        // window to that header still leaves something being reported.
        _solo = RunContext.IsSingleplayer && ordered.Count <= 1;
        _title.Text = HeaderTitle(team);

        // Collapsed, the header is the whole window: no rows to lay out and nothing to hover.
        if (_minimized)
        {
            _hovered = null;
            _tooltip.Visible = false;
            _tooltipSignature = null;
            return;
        }

        if (_solo)
        {
            RenderBreakdownBody(ordered.Count > 0 ? ordered[0] : null);
            return;
        }

        var seen = new HashSet<ulong>();
        int index = 0;
        foreach (ulong id in ordered)
        {
            seen.Add(id);
            Row widget = Ensure(id);
            decimal value = Value(_snapshot.GetValueOrDefault(id));
            widget.Rdps.Text = Round(value).ToString();
            widget.Percent.Text = team > 0m ? $"{Round(value / team * 100m)}%" : "0%";
            widget.Bar.Value = (double)Math.Clamp(value / max, 0m, 1m);
            _list.MoveChild(widget.Container, index++);
        }

        foreach (ulong netId in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows[netId].Container.QueueFree();
            _rows.Remove(netId);
        }

        UpdateTooltip();
    }

    // The rows for the currently-picked view, updating the picker's caption to match. A picked fight that has since left
    // the run (a new run wiped it) silently falls back to the total, the same view the picker starts on.
    private IReadOnlyList<RdpsRow> SelectedView()
    {
        // The run history page drives the meter while it is up: the fight being looked at wins over the picked view,
        // and one whose numbers are not in memory shows as an empty meter under its own name rather than as somebody
        // else's damage.
        if (RunHistoryView.Fight is HistoryFight fight)
        {
            _menu.Text = fight.Caption;
            return fight.Key is string fightKey ? RunLedger.SnapshotOf(fightKey) : Array.Empty<RdpsRow>();
        }

        switch (_viewKind)
        {
            case ViewKind.Current:
                _menu.Text = Loc.T("view.live");
                return RunLedger.CurrentSnapshot();
            case ViewKind.Combat when _viewKey is string key && RunLedger.HasCombat(key):
                _menu.Text = CaptionFor(key);
                return RunLedger.SnapshotOf(key);
            default:
                _viewKind = ViewKind.Total;
                _viewKey = null;
                _menu.Text = Loc.T("view.total");
                return RunLedger.TotalSnapshot();
        }
    }

#if RDPS_HARNESS
    /// <summary>
    /// The caption the view picker is currently showing, so the self-test can assert which view the meter opens on.
    /// Harness-only: a shipped build has no reason to expose the picker's internals.
    /// </summary>
    internal string HarnessPickerCaption => _menu.Text;

    /// <summary>The panel's laid-out width, so the self-test can check that content never reflows it.</summary>
    internal float HarnessPanelWidth => _panel.Size.X;

    /// <summary>The panel's laid-out height, which is the one dimension collapsing the window is allowed to change.</summary>
    internal float HarnessPanelHeight => _panel.Size.Y;

    /// <summary>The meter being read, the headline it draws, and the arrows that page between them.</summary>
    internal MeterMode HarnessMode => _mode;

    /// <summary>Whether the view picker is drawing its chip. Flat is Godot's default for a MenuButton and draws none.</summary>
    internal bool HarnessPickerDrawsChip => !_menu.Flat;

    internal string HarnessTitle => _title.Text;

    /// <summary>What the meter calls itself for a party run and for a solo one, without waiting for a frame to draw.</summary>
    internal string HarnessModeName(bool solo)
    {
        return ModeName(solo);
    }

    /// <summary>Drives the arrows against a run of the given shape, the way _Process would have decided it.</summary>
    internal void HarnessStepMode(int step, bool solo = false)
    {
        _solo = solo;
        StepMode(step);
    }

    /// <summary>The headline the window would draw for this player - or, for a null row, for nobody at all.</summary>
    internal string HarnessHeaderTitle(RdpsRow? row)
    {
        return HeaderTitle(Value(row));
    }

    /// <summary>Whether the window is collapsed to its header, and the button that collapses it.</summary>
    internal bool HarnessMinimized => _minimized;

    internal bool HarnessGlyphIsPlus => _glyph.IsPlus;

    /// <summary>Where the mark is on screen, which a toggle must not move.</summary>
    internal Vector2 HarnessMarkCentre => _minimize.GlobalPosition + _minimize.Size / 2f;

    internal Vector2 HarnessPanelPosition => _panel.Position;

    /// <summary>Puts the window somewhere known, so a position-sensitive check is not at the mercy of the real config.</summary>
    internal void HarnessPlace(Vector2 position)
    {
        FreePosition(_panel, position);
        _header.Detach();
    }

    /// <summary>How far the drawn mark sits from the middle of the button it is drawn on - which must be nowhere.</summary>
    internal Vector2 HarnessGlyphOffCentre =>
        (_glyph.GlobalPosition + _glyph.Middle) - (_minimize.GlobalPosition + _minimize.Size / 2f);

    internal void HarnessToggleMinimized()
    {
        ToggleMinimized();
    }

    /// <summary>The header's own height, which is all a collapsed window is allowed to be (bar the window's border).</summary>
    internal float HarnessHeaderHeight => _header.Size.Y;

    /// <summary>What a collapsed window has to have given up: the body, and the header's two choices.</summary>
    internal (bool Body, bool Arrows, bool Picker, bool Title) HarnessVisibleParts =>
        (_body.Visible, _prev.Visible && _next.Visible, _menu.Visible, _title.Visible);

    /// <summary>What one player's bar is worth on the meter currently being read.</summary>
    internal decimal HarnessValue(RdpsRow row)
    {
        return Value(row);
    }

    /// <summary>
    /// The breakdown this meter would draw for one player: the sections it lists, and whether its damage bars carry the
    /// fainter teammate-buff segment. Built through the real rebuild into a throwaway list, so the self-test reads what
    /// a hover would actually show rather than a description of it.
    /// </summary>
    internal (IReadOnlyList<string> Sections, bool SplitBars) HarnessBreakdown(RdpsRow row)
    {
        var list = new VBoxContainer();
        RebuildBreakdown(list, row, Colors.White, damageHeader: true);

        var sections = new List<string>();
        bool split = false;
        foreach (Node child in list.GetChildren())
        {
            if (child is Panel strip)
            {
                sections.AddRange(strip.GetChildren().OfType<Label>().Select(l => l.Text));
            }
            else if (child is Control bar && bar.GetChildCount() > 0 && bar.GetChild(0) is Control holder)
            {
                // A row's background is its first child. A split damage bar is a holder of two ProgressBars, the one
                // behind reaching further than the one in front; that overhang is the faded part the eye picks up.
                List<ProgressBar> bars = holder.GetChildren().OfType<ProgressBar>().ToList();
                split |= bars.Count == 2 && bars[0].Value > bars[1].Value;
            }
        }

        list.Free();
        return (sections, split);
    }

    /// <summary>
    /// The rows the meter would draw right now, picking the view the same way a frame does (and captioning the picker
    /// with it), so the self-test can check what is driving the meter rather than only what the ledger holds.
    /// </summary>
    internal IReadOnlyList<RdpsRow> HarnessSelectedView()
    {
        return SelectedView();
    }

    /// <summary>The overlay living in the scene tree, or null if it has not been installed yet.</summary>
    internal static RdpsOverlayNode? HarnessInstance =>
        Engine.GetMainLoop() is SceneTree { Root: not null } tree
            ? tree.Root.GetChildren().OfType<RdpsOverlayNode>().FirstOrDefault()
            : null;
#endif

    // Rebuilt each time the menu opens so it lists whatever fights the run has reached: Total, Live, then each fight by
    // name, in the order they were fought.
    private void RebuildMenu()
    {
        PopupMenu popup = _menu.GetPopup();
        popup.Clear();
        popup.AddItem(Loc.T("view.total"), IdTotal);
        popup.AddItem(Loc.T("view.live"), IdCurrent);

        IReadOnlyList<CombatInfo> fights = RunLedger.Fights();
        for (int i = 0; i < fights.Count; i++)
        {
            popup.AddItem(FightName(fights[i]), i);
        }
    }

    /// <summary>
    /// Collapse the window to its header, or open it again, and remember which. Restoring re-clamps a window that was
    /// dragged against the bottom of the screen while collapsed, since the body it grows back would otherwise hang off
    /// the edge - but only one that was dragged there, since a window still on its default anchors grows downward from
    /// a fixed corner and writing a position would only fight those anchors.
    /// </summary>
    private void ToggleMinimized()
    {
        // Free positioning first, so the move below is one subtraction in one coordinate system. Anchored, the panel's
        // Position is derived from its size - collapsing pins the right edge and slides the left one 272px across - so
        // reading a position, changing the size and writing the position back would be three different meanings of the
        // same number.
        _header.Detach();

        Vector2 before = MarkOffset(_minimized);
        _minimized = !_minimized;
        OverlayLayout.SaveMinimized(_minimized);
        ApplyMinimized();

        // Move the window by exactly as much as the mark moved within it, so the mark itself does not move at all: the
        // plus opens where the minus was, under the pointer that just clicked it, rather than the window keeping a
        // corner and the button jumping most of the window's width away.
        _panel.Position += before - MarkOffset(_minimized);
        _clampPending = true;
        OverlayLayout.SavePosition(OpenPosition);
    }

    /// <summary>
    /// Where the mark sits inside the panel, measured from the panel's top-left. Collapsed the button is the whole
    /// square; open it is a slot at the right end of the header. The window border's thickness is read from the
    /// stylebox rather than assumed, since it is the difference between the two states landing on each other and
    /// landing a pixel apart.
    /// </summary>
    private Vector2 MarkOffset(bool minimized)
    {
        StyleBox border = _panel.GetThemeStylebox("panel");
        float left = border.GetMargin(Side.Left);
        float right = border.GetMargin(Side.Right);
        float top = border.GetMargin(Side.Top);
        float bottom = border.GetMargin(Side.Bottom);

        return minimized
            ? new Vector2((left + MinimizedSide - right) / 2f, (top + MinimizedSide - bottom) / 2f)
            : new Vector2(Width - right - 6f - MinimizeWidth / 2f, top + HeaderHeight / 2f);
    }

    /// <summary>How far a window in this state sits from where the same window would sit open.</summary>
    private Vector2 ShiftFrom(bool minimized)
    {
        return minimized ? MarkOffset(false) - MarkOffset(true) : Vector2.Zero;
    }

    /// <summary>
    /// Where the open window would be right now. This, not the panel's own position, is what gets remembered: the two
    /// states stand in different places, so storing whichever one happened to be showing would move the window a few
    /// hundred pixels every time the game was quit collapsed and reopened.
    /// </summary>
    private Vector2 OpenPosition => _panel.Position - ShiftFrom(_minimized);

    // Collapsed, the window is a square holding one plus and nothing else - not the body, not the two header choices
    // that mean nothing without a body, and not the title either. Everything a 320px-wide strip would still have been
    // is given up, because a collapsed meter's whole job is to be small and findable, and a title it is too narrow to
    // finish reading is neither. The square is the drag handle as well, so it can still be moved where it suits.
    private void ApplyMinimized()
    {
        _body.Visible = !_minimized;
        _prev.Visible = !_minimized;
        _next.Visible = !_minimized;
        _menu.Visible = !_minimized;
        _title.Visible = !_minimized;

        // Both the panel and the header are pinned, so the square holds whichever of the two the layout settles from,
        // and neither is allowed to decide the shape alone. The header's share is the square less the window border,
        // read from the stylebox so the square is exactly MinimizedSide however thick that border is.
        StyleBox border = _panel.GetThemeStylebox("panel");
        _panel.CustomMinimumSize = _minimized ? new Vector2(MinimizedSide, MinimizedSide) : new Vector2(Width, 0f);
        _header.CustomMinimumSize = new Vector2(
            0f,
            _minimized ? MinimizedSide - border.GetMargin(Side.Top) - border.GetMargin(Side.Bottom) : HeaderHeight);

        // Open, the button is a slot at the right end of the header; collapsed, it is the whole square.
        _minimize.AnchorLeft = _minimized ? 0f : 1f;
        _minimize.AnchorRight = 1f;
        _minimize.OffsetLeft = _minimized ? 0f : -(MinimizeWidth + 6f);
        _minimize.OffsetRight = _minimized ? 0f : -6f;

        _glyph.Configure(plus: _minimized, arm: _minimized ? 22f : 13f, thickness: _minimized ? 3f : 2f);
    }

    // Page to the next meter and remember it, wrapping at either end so both arrows always reach everything.
    private void StepMode(int step)
    {
        MeterMode[] modes = Modes(_solo);
        int index = Math.Max(0, Array.IndexOf(modes, _mode));
        _mode = modes[((index + step) % modes.Length + modes.Length) % modes.Length];
        OverlayLayout.SaveMode(_mode);

        // The rows and both breakdowns are drawn from the meter that was showing, so none of them survives the switch.
        _bodySignature = null;
        _tooltipSignature = null;
    }

    // The meters the arrows page through. Alone, rDPS and aDPS are the same number - there are no teammates to move
    // damage between - so only one of them is offered, and the pair that would otherwise sit side by side collapses
    // into the single meter they both describe.
    private static MeterMode[] Modes(bool solo)
    {
        return solo
            ? new[] { MeterMode.Rdps, MeterMode.Blocked }
            : new[] { MeterMode.Rdps, MeterMode.ADps, MeterMode.Blocked };
    }

    /// <summary>What one player's bar is worth on the meter being read.</summary>
    private decimal Value(RdpsRow? row)
    {
        if (row == null)
        {
            return 0m;
        }

        return _mode switch
        {
            MeterMode.ADps => row.ADps,
            MeterMode.Blocked => row.RBlock,
            _ => row.Rdps,
        };
    }

    private string ModeName()
    {
        return ModeName(_solo);
    }

    // What the meter on screen is called. rDPS and aDPS are only worth telling apart when there are teammates to move
    // damage between; alone, both are simply the damage you did, so the run says DPS and means it.
    private string ModeName(bool solo)
    {
        return _mode switch
        {
            MeterMode.Blocked => Loc.T("mode.block"),
            MeterMode.ADps when !solo => Loc.T("mode.adps"),
            _ => Loc.T(solo ? "mode.dps" : "mode.rdps"),
        };
    }

    private void OnViewPicked(long id)
    {
        // Picking by hand outranks the run history page: whatever it had pinned steps aside until the next map point
        // is focused, so the picker never looks stuck.
        RunHistoryView.Release();

        if (id == IdTotal)
        {
            _viewKind = ViewKind.Total;
            _viewKey = null;
        }
        else if (id == IdCurrent)
        {
            _viewKind = ViewKind.Current;
            _viewKey = null;
        }
        else
        {
            IReadOnlyList<CombatInfo> fights = RunLedger.Fights();
            if (id >= 0 && id < fights.Count)
            {
                _viewKind = ViewKind.Combat;
                _viewKey = fights[(int)id].Key;
            }
        }
    }

    // A fight goes by its own name, in both the menu and the chip - the name is what anyone recognizes a fight by, so
    // numbering it as well would only be noise. A fight with no name at all (a tally saved before the meter named
    // them) falls back to a generic one.
    private static string FightName(CombatInfo fight)
    {
        return string.IsNullOrEmpty(fight.Label) ? Loc.T("combat") : fight.Label;
    }

    // The chip caption for the picked fight.
    private static string CaptionFor(string key)
    {
        IReadOnlyList<CombatInfo> fights = RunLedger.Fights();
        for (int i = 0; i < fights.Count; i++)
        {
            if (fights[i].Key == key)
            {
                return FightName(fights[i]);
            }
        }

        return Loc.T("view.total");
    }

    // The solo body: the lone player's breakdown drawn straight into the panel, with no "Damage Breakdown" strip, since
    // the window's own title - which by then is already carrying their number - says it.
    private void RenderBreakdownBody(ulong? netId)
    {
        _hovered = null;
        _tooltip.Visible = false;
        _tooltipSignature = null;

        // Coming from a party table, the body still holds its rows; forget them and let the rebuild free the children.
        if (_rows.Count > 0)
        {
            _rows.Clear();
            _bodySignature = null;
        }

        RdpsRow? row = netId is ulong id ? _snapshot.GetValueOrDefault(id) : null;
        string signature = netId is ulong key ? Signature(key, row) : "empty";
        if (signature == _bodySignature)
        {
            return;
        }

        _bodySignature = signature;
        Color color = netId is ulong owner && _visuals.TryGetValue(owner, out PlayerVisual visual)
            ? visual.Color
            : new Color(0.7f, 0.7f, 0.7f);
        RebuildBreakdown(_list, row, color, damageHeader: false);
    }

    /// <summary>
    /// The header, which names the meter being read and carries its number: the lone player's alone, the whole party's
    /// summed in co-op, where it is the one place the team's own total is stated rather than split across the rows.
    ///
    /// It carries it at zero too, rather than falling back to the bare meter name: a window reading "Damage: 0" is
    /// telling you there has been no damage, while one reading "Damage" looks like it has not finished loading. The
    /// zero is the answer, so it is shown - which is what a fight nobody has swung in yet, or a shop between fights,
    /// looks like. It is also what makes the window worth collapsing to: the header alone still reports.
    /// </summary>
    private string HeaderTitle(decimal value)
    {
        return Loc.T("title.value", ModeName(), Round(value));
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
            RebuildBreakdown(_tooltipList, row, widget.Color, damageHeader: true);
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
    // biggest entry, and tinted to the player's class colour. Name and value are omitted - the hovered row shows them.
    //
    // On the aDPS meter the last two sections are gone and the damage bars are solid: teammate buffs neither add to nor
    // subtract from that meter, so a section itemizing them, or the fainter segment marking the part of a card's damage
    // they paid for, would be describing an adjustment the number on screen never had.
    //
    // The Blocked meter is the same table over the other tally: where the block that stopped something came from, then
    // the block this player put on teammates and the block teammates put on them. It credits like rDPS - a Defend you
    // played for somebody else is yours - so it keeps both sections and the split bars, which alone simply come out
    // empty and unsplit, there being nobody else involved.
    private void RebuildBreakdown(VBoxContainer list, RdpsRow? row, Color color, bool damageHeader)
    {
        while (list.GetChildCount() > 0)
        {
            Node child = list.GetChild(0);
            list.RemoveChild(child);
            child.QueueFree();
        }

        bool block = _mode == MeterMode.Blocked;
        if (row == null)
        {
            list.AddChild(SectionHeader(Loc.T(block ? "empty.block" : "empty")));
            return;
        }

        if (block)
        {
            AddItemSection(list, Items(row.Blocked), color, damageHeader ? Loc.T("section.block") : null, split: true);
            AddEffectSection(list, Loc.T("section.block.given"), Combine(row.BlockGivenBy), "+", color);
            AddEffectSection(list, Loc.T("section.block.received"), Combine(row.BlockReceivedBy), "-", color);
            return;
        }

        bool credited = _mode == MeterMode.Rdps;
        AddItemSection(list, Items(row.Dealt), color, damageHeader ? Loc.T("section.damage") : null, credited);
        if (!credited)
        {
            return;
        }

        AddEffectSection(list, Loc.T("section.given"), Combine(row.GivenBy), "+", color);
        AddEffectSection(list, Loc.T("section.received"), Combine(row.ReceivedBy), "-", color);
    }

    // Drop the entries that round to nothing, so a sliver of a share never takes a row to say "0".
    private static List<(string Name, decimal Amount, decimal Buff)> Items(
        IReadOnlyList<(string Name, decimal Amount, decimal Buff)> items)
    {
        return items.Where(i => Round(i.Amount) != 0m).ToList();
    }

    private static void AddItemSection(
        VBoxContainer list, List<(string Name, decimal Amount, decimal Buff)> items, Color color, string? header, bool split)
    {
        if (items.Count == 0)
        {
            return;
        }

        decimal max = Math.Max(1m, items.Max(i => i.Amount));
        decimal total = items.Sum(i => i.Amount);
        if (header != null)
        {
            list.AddChild(SectionHeader(header));
        }

        foreach ((string name, decimal amount, decimal buff) in items)
        {
            // Always the split bar, with nothing split off it on the aDPS meter. Drawing that one solid instead would
            // tint it differently: the split bar's own segment sits over the fainter one behind it, and two translucent
            // layers of a colour do not composite to the same shade as one.
            Control bar = SplitBackground(split ? amount - buff : amount, amount, max, color);
            list.AddChild(BarRow(SourceName(name), Round(amount).ToString(), Percent(amount, total), bar));
        }
    }

    // A row's label. Most sources arrive already localized from the game (a card, potion or relic title); a power the
    // ledger stored under its own name is translated here, and the placeholder for a source nothing identified reads as
    // "(none)" in the player's language.
    private static string SourceName(string source)
    {
        return Loc.SourceName(Loc.PowerName(source));
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

    private static void AddEffectSection(
        VBoxContainer list, string title, List<(string Effect, decimal Amount)> items, string sign, Color color)
    {
        if (items.Count == 0)
        {
            return;
        }

        decimal max = Math.Max(1m, items.Max(i => i.Amount));
        decimal total = items.Sum(i => i.Amount);
        list.AddChild(SectionHeader(title));
        foreach ((string effect, decimal amount) in items)
        {
            list.AddChild(BarRow(SourceName(effect), sign + Round(amount), Percent(amount, total), EffectBackground(amount, max, color)));
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
            return $"{_mode}:{netId}:none";
        }

        var text = new System.Text.StringBuilder();
        text.Append(_mode).Append(':').Append(netId);

        // Only the meter on screen is described: the breakdown is rebuilt whenever the arrows move, and folding the
        // other tally in would redraw this one every time a number it isn't showing changed.
        bool block = _mode == MeterMode.Blocked;
        foreach ((string name, decimal amount, decimal buff) in block ? row.Blocked : row.Dealt)
        {
            text.Append('|').Append(name).Append(Round(amount)).Append('b').Append(Round(buff));
        }

        foreach ((string effect, decimal amount) in Combine(block ? row.BlockGivenBy : row.GivenBy))
        {
            text.Append("|g").Append(effect).Append(Round(amount));
        }

        foreach ((string effect, decimal amount) in Combine(block ? row.BlockReceivedBy : row.ReceivedBy))
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
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        // The numbers clip too. Their reserved column is wide enough that they realistically never do, but without it
        // a long tally would set the row's minimum width and drag the whole window wider as the fight went on.
        Label rdps = OverlayLabel(string.Empty);
        rdps.CustomMinimumSize = new Vector2(ValueColumn, 0f);
        rdps.HorizontalAlignment = HorizontalAlignment.Right;
        rdps.ClipText = true;

        Label percent = OverlayLabel(string.Empty);
        percent.CustomMinimumSize = new Vector2(PercentColumn, 0f);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        percent.ClipText = true;
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
        Loc.ApplyFont(label, "font");
        return label;
    }

    // The header's own controls, which are built once in _Ready and outlive a language change - unlike the rows and
    // breakdown, which are rebuilt from scratch and pick their font up as they are made.
    // The arrows and the caret are deliberately left out: they carry glyphs rather than words, and a language's font is
    // picked for its script, not for arrowheads - swapping one in risks drawing them as boxes in the languages that
    // need it most, while the default font has them everywhere.
    private void ApplyLocaleFonts()
    {
        Loc.ApplyFont(_title, "font");
        Loc.ApplyFont(_menu, "font");
        Loc.ApplyFont(_menu.GetPopup(), "font");
    }

    // The header's Total/Live button: a rounded chip that brightens on hover and press. Filled and outlined firmly
    // enough to read as a control rather than as a second label, since what it carries - a fight's name - is text.
    // The right margin leaves room for the caret drawn over it, so a long name clips before reaching the glyph.
    private static StyleBoxFlat ToggleStyle(float alpha)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, alpha),
            BorderColor = new Color(1f, 1f, 1f, 0.35f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginLeft = 6,
            ContentMarginRight = 16,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
    }

    // One of the two glyphs flanking the title. Flat - no chip of its own, so the header keeps one control in it - and
    // pinned to its end of the title's space: the left one to the window's edge, the right one to the picker's.
    private Button ArrowButton(string glyph, bool left)
    {
        var button = new Button
        {
            Text = glyph,
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            AnchorLeft = left ? 0f : 1f,
            AnchorRight = left ? 0f : 1f,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = left ? 6f : -(MenuWidth + MenuInset + ArrowWidth),
            OffsetRight = left ? 6f + ArrowWidth : -(MenuWidth + MenuInset),
        };
        button.AddThemeFontSizeOverride("font_size", 17);
        button.AddThemeColorOverride("font_color", TitleColor);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        return button;
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
        text.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        Label value = OverlayLabel(valueText);
        value.CustomMinimumSize = new Vector2(ValueColumn, 0f);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.ClipText = true;

        Label percent = OverlayLabel(percentText);
        percent.CustomMinimumSize = new Vector2(PercentColumn, 0f);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        percent.ClipText = true;
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
        Loc.ApplyFont(label, "font");
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
/// The minimize button's mark, drawn rather than typed: a horizontal bar for the minus, plus a vertical one for the
/// plus, both centred on the control's own middle.
///
/// Drawn because a font would not centre it. Text is placed by its line box - ascent above the baseline, descent
/// below - and the ink of "+" and "-" sits differently within that box, so centring the box leaves the mark itself
/// off-centre by an amount that depends on the glyph and the font, which is exactly what showed up as a high plus over
/// a settled-looking minus. Two rects measured from <see cref="Control.Size"/> have no such gap between what is
/// centred and what is seen, and they carry no risk of the font lacking a true minus sign and drawing a box instead.
/// </summary>
internal sealed partial class MinimizeGlyph : Control
{
    private bool _plus;
    private float _arm = 12f;
    private float _thickness = 2f;
    private Color _color = new(0.541f, 0.706f, 0.973f);

    public void Configure(bool plus, float arm, float thickness)
    {
        _plus = plus;
        _arm = arm;
        _thickness = thickness;
        QueueRedraw();
    }

    public void Tint(Color color)
    {
        _color = color;
        QueueRedraw();
    }

    /// <summary>Which mark is drawn, for the self-test - the button itself carries no text to read back.</summary>
    public bool IsPlus => _plus;

    /// <summary>The point the mark is drawn around, so the self-test can check it against the button's own centre.</summary>
    public Vector2 Middle => (Size / 2f).Round();

    public override void _Draw()
    {
        // Rounded to whole pixels: a bar landing on a half-pixel is drawn blurred across two, which on a 2px-thick
        // mark is the difference between a crisp line and a grey smudge.
        Vector2 middle = (Size / 2f).Round();
        float arm = Mathf.Round(_arm);
        float thickness = Mathf.Round(_thickness);

        DrawRect(new Rect2(middle.X - arm / 2f, middle.Y - thickness / 2f, arm, thickness), _color);
        if (_plus)
        {
            DrawRect(new Rect2(middle.X - thickness / 2f, middle.Y - arm / 2f, thickness, arm), _color);
        }
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

    /// <summary>
    /// Whether the window has left its anchors for a position of its own. Until it has, its spot is the anchors' to
    /// decide and writing a Position would only fight them.
    /// </summary>
    public bool IsDetached => _detached;

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

    // Freeze the window's current anchored spot and switch to free positioning, so moving it means writing Position
    // instead of fighting the anchors. Called on the first drag, and by the minimize button, which also moves it.
    public void Detach()
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
