using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Umbra.CharacterSelectWidget.Services;
using Umbra.Common;
using Umbra.Widgets;
using Una.Drawing;

namespace Umbra.CharacterSelectWidget.Widgets;

/// <summary>
/// A toolbar widget that shows the active Character Select+ character and
/// design, and lets the user pick a different character/design and apply it
/// from a popup menu.
///
/// This widget declares a dependency on Character Select+'s internal plugin
/// name ("CharacterSelectPlugin") via <see cref="InteropToolbarWidgetAttribute"/>,
/// so Umbra will only let users add it to their toolbar when Character
/// Select+ is actually installed.
/// </summary>
[InteropToolbarWidget(
    "CharacterSelectWidget",
    "Character Select",
    "Shows the active Character Select+ character and design, and lets you switch designs from the toolbar.",
    "CharacterSelectPlugin"
)]
public sealed class CharacterSelectWidget(
    WidgetInfo                  info,
    string?                     guid         = null,
    Dictionary<string, object>? configValues = null
) : StandardToolbarWidget(info, guid, configValues)
{
    protected override StandardWidgetFeatures Features =>
        StandardWidgetFeatures.Text |
        StandardWidgetFeatures.SubText |
        StandardWidgetFeatures.Icon |
        StandardWidgetFeatures.CustomizableIcon;

    /// <inheritdoc/>
    public override MenuPopup Popup { get; } = new();

    private CharacterSelectIpc Ipc      { get; } = Framework.Service<CharacterSelectIpc>();
    private IToastGui          ToastGui { get; } = Framework.Service<IToastGui>();

    // Popup building blocks. Created once in OnLoad; contents refreshed on demand.
    private readonly MenuPopup.Group _charactersGroup = new("Character");
    private readonly MenuPopup.Group _designsGroup    = new("Design");

    private SelectionStatusItem _selectionStatus = null!;
    private MenuPopup.Button    _applyButton     = null!;
    private MenuPopup.Separator _bottomSeparator = null!;

    // Buttons currently shown in each group, keyed by character/design name
    // so we can toggle their "Selected" highlight without needing to
    // re-query the group. Umbra's Una.Drawing Node.Id must match
    // "^[A-Za-z]{1}[A-Za-z0-9_-]+$", but character and design names are
    // free-form user text (almost always containing a space) - so names
    // can't be used as Node IDs and are tracked here instead.
    private readonly Dictionary<string, MenuPopup.Button> _characterButtons = [];
    private readonly Dictionary<string, MenuPopup.Button> _designButtons    = [];

    /// <summary>
    /// Wraps the "Character" and "Design" groups in a single popup menu item
    /// so they render side by side instead of one on top of the other.
    /// </summary>
    private sealed class ColumnsRow : MenuPopup.IMenuItem
    {
        public Node Node { get; } = new() {
            ClassList = ["character-select-columns"],
        };

        public ColumnsRow(Node leftColumn, Node rightColumn)
        {
            Node.Style.Flow     = Flow.Horizontal;
            Node.Style.Gap      = 8;
            Node.Style.AutoSize = (AutoSize.Grow, AutoSize.Fit);

            Node.AppendChild(leftColumn);
            Node.AppendChild(rightColumn);
        }
    }

    /// <summary>
    /// A non-interactive popup label showing the current selection. Renders
    /// as two text spans sharing a single row - a "Selected:" prefix in red,
    /// followed by the character/design summary in the same color as the
    /// other menu items (i.e. not greyed out).
    /// </summary>
    private sealed class SelectionStatusItem : MenuPopup.IMenuItem
    {
        public bool IsVisible {
            get => Node.IsVisible;
            set => Node.Style.IsVisible = value;
        }

        public Node Node { get; } = new() {
            ClassList = ["button"],
            ChildNodes = [
                new() { ClassList = ["text"] }, // "Selected:" prefix.
                new() { ClassList = ["text"] }, // Character/design summary.
            ],
        };

        private Node PrefixNode => Node.ChildNodes[0];
        private Node RestNode   => Node.ChildNodes[1];

        public SelectionStatusItem()
        {
            // Explicit rather than relying on stylesheet cascade/defaults, so
            // this can never pick up the ".button:disabled" (greyed-out) look.
            Node.IsDisabled = false;

            PrefixNode.Style.AutoSize = (AutoSize.Fit, AutoSize.Fit);
            PrefixNode.Style.Color    = new Color(255, 0, 0);

            // Matches the same named theme color "Apply Design" (and every
            // other non-hovered menu item) uses for its ".text" node.
            RestNode.Style.Color = new Color("Widget.PopupMenuText");
        }

        public void SetLabel(string prefix, string rest)
        {
            PrefixNode.Style.IsVisible = !string.IsNullOrEmpty(prefix);
            PrefixNode.NodeValue       = prefix;
            RestNode.NodeValue         = rest;
        }
    }

    // Live state.
    private string _currentCharacter  = string.Empty; // What Character Select+ says is active right now.
    private string _currentDesign     = string.Empty; // Best-effort: last design we know was applied.
    private string _selectedCharacter = string.Empty; // What's highlighted in the popup right now.
    private string _selectedDesign    = string.Empty;

    // IPC calls are cheap in-process delegate invocations, but there's no
    // need to poll every single frame - refresh the toolbar label a few
    // times a second instead.
    private long _nextLabelRefreshAt;

    /// <inheritdoc/>
    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        return [
            ..base.GetConfigVariables(),

            new BooleanWidgetConfigVariable(
                "ApplyOnDesignClick",
                "Apply immediately when clicking a design",
                "When enabled, clicking a design in the list applies it right away instead of requiring the Apply button, and the Selected/Apply Design section is hidden from the menu.",
                false
            ),

            new BooleanWidgetConfigVariable(
                "ClosePopupAfterApply",
                "Close popup after applying",
                "Whether the popup should close automatically after a design has been applied.",
                true
            ),

            new BooleanWidgetConfigVariable(
                "MatchDesignsHeightToCharacters",
                "Scroll the design list within the character list's height",
                "When enabled, the design list is capped to the height of the character list and scrolls instead of growing the popup taller.",
                false
            ),

            new IntegerWidgetConfigVariable(
                "MaxLabelLength",
                "Cut off long character/design names",
                "Truncates character and design names longer than this many characters, appending an ellipsis. Set to 0 to always show the full name (the popup will size itself to fit).",
                0,
                0,
                100
            ),
        ];
    }

    /// <inheritdoc/>
    protected override void OnLoad()
    {
        SetGameIconId(64024u); // Generic "appearance/mirror" style icon.
        SetText("Character Select");

        _selectionStatus = new();
        UpdateStatusLabel();

        _applyButton = new("Apply Design") {
            OnClick           = ApplySelectedDesign,
            ClosePopupOnClick = false, // We close manually - see ApplySelectedDesign.
        };

        // No icon, and the label centered in the button's full width.
        _applyButton.Node.QuerySelector(".icon")!.Style.IsVisible = false;
        _applyButton.Node.QuerySelector(".text")!.Style.TextAlign = Anchor.MiddleCenter;

        _bottomSeparator = new();

        // The built-in ".group" class auto-sizes horizontally with
        // AutoSize.Grow, which - per Una.Drawing's own docs - splits
        // available width *equally* between multiple Grow siblings. With
        // two side-by-side columns that squeezes the (often longer) design
        // column down to the character column's width and truncates it.
        // Override both to Fit so each column instead hugs its own content.
        _charactersGroup.Node.Style.AutoSize = (AutoSize.Fit, AutoSize.Fit);
        _designsGroup.Node.Style.AutoSize    = (AutoSize.Fit, AutoSize.Fit);

        Popup.Add(new ColumnsRow(_charactersGroup.Node, _designsGroup.Node));
        Popup.Add(_bottomSeparator);
        Popup.Add(_selectionStatus);
        Popup.Add(_applyButton);

        Popup.OnPopupOpen    += RefreshCharacters;
        Ipc.CharacterChanged += OnCharacterChangedByIpc;

        RefreshCurrentFromIpc();
    }

    /// <inheritdoc/>
    protected override void OnDraw()
    {
        long now = Environment.TickCount64;

        if (now >= _nextLabelRefreshAt) {
            RefreshCurrentFromIpc();
            _nextLabelRefreshAt = now + 500;
        }

        SetText(_currentCharacter.Length > 0 ? _currentCharacter : "Character Select");
        SetSubText(_currentDesign);

        _applyButton.IsDisabled = _selectedCharacter.Length == 0 || _selectedDesign.Length == 0;

        bool applyOnDesignClick    = GetConfigValue<bool>("ApplyOnDesignClick");
        _bottomSeparator.IsVisible = !applyOnDesignClick;
        _selectionStatus.IsVisible = !applyOnDesignClick;
        _applyButton.IsVisible     = !applyOnDesignClick;

        if (Popup.IsOpen) {
            SyncDesignListHeight();
        }
    }

    /// <inheritdoc/>
    protected override void OnUnload()
    {
        Popup.OnPopupOpen    -= RefreshCharacters;
        Ipc.CharacterChanged -= OnCharacterChangedByIpc;
    }

    /// <summary>
    /// Pulls the currently-active character from Character Select+. We don't
    /// have a live "current design" getter from the IPC surface, so the
    /// design label reflects the last design we know was applied (either by
    /// us, or reported through the OnCharacterChanged broadcast).
    /// </summary>
    private void RefreshCurrentFromIpc()
    {
        string liveCharacter = Ipc.GetCurrentCharacter();

        if (liveCharacter != _currentCharacter) {
            // Active character changed behind our back (e.g. via Character
            // Select+'s own UI) - we no longer know its design for certain.
            _currentDesign = string.Empty;
        }

        _currentCharacter = liveCharacter;
    }

    private void OnCharacterChangedByIpc(string characterName, string designName)
    {
        _currentCharacter  = characterName;
        _currentDesign     = designName;
        _selectedCharacter = characterName;
        _selectedDesign    = designName;
    }

    /// <summary>
    /// <see cref="MenuPopup.Group"/> doesn't expose a bulk "Clear" - only
    /// Add/Remove/RemoveById - so we remove every button we previously added
    /// ourselves, using the tracking dictionary.
    ///
    /// This runs from inside <c>OnClick</c> handlers while Una.Drawing is
    /// mid-render, and Umbra's popup render loop does not cleanly unwind its
    /// ImGui Begin/End stack if an exception escapes from here - so every
    /// removal is isolated and the tracking dictionary is always cleared
    /// (via try/finally) even if a single button fails to remove cleanly.
    /// Leaving the dictionary out of sync with the actual child nodes is
    /// what would otherwise compound into worse failures on the next rapid
    /// click.
    /// </summary>
    private static void ClearGroup(MenuPopup.Group group, Dictionary<string, MenuPopup.Button> trackedButtons)
    {
        try {
            foreach (var button in trackedButtons.Values) {
                try {
                    group.Remove(button, dispose: true);
                } catch (Exception e) {
                    Logger.Warning($"[CharacterSelectWidget] Failed to remove a popup button cleanly: {e.Message}");
                }
            }
        } finally {
            trackedButtons.Clear();
        }
    }

    /// <summary>Rebuilds the "Character" group. Called every time the popup is opened.</summary>
    private void RefreshCharacters()
    {
        try {
            ClearGroup(_charactersGroup, _characterButtons);

            string current = Ipc.GetCurrentCharacter();

            if (_selectedCharacter.Length == 0) {
                _selectedCharacter = current;
            }

            foreach (var name in Ipc.GetCharacterList()) {
                var button = new MenuPopup.Button(TruncateLabel(name)) {
                    Selected          = name == _selectedCharacter,
                    ClosePopupOnClick = false,
                };

                button.OnClick = () => OnCharacterSelected(name);

                _characterButtons[name] = button;
                _charactersGroup.Add(button);
            }

            RefreshDesigns();
            UpdateStatusLabel();
        } catch (Exception e) {
            Logger.Error($"[CharacterSelectWidget] RefreshCharacters failed: {e.Message}");
        }
    }

    private void OnCharacterSelected(string name)
    {
        try {
            if (_selectedCharacter == name) return;

            _selectedCharacter = name;
            _selectedDesign    = string.Empty;

            foreach (var (buttonName, button) in _characterButtons) {
                button.Selected = buttonName == name;
            }

            RefreshDesigns();
            UpdateStatusLabel();
        } catch (Exception e) {
            // Never let an exception escape from inside a Node.OnClick handler -
            // Umbra's popup render loop doesn't unwind its ImGui Begin/End stack
            // cleanly if we throw here, which can crash the game after repeated
            // rapid clicks.
            Logger.Error($"[CharacterSelectWidget] OnCharacterSelected('{name}') failed: {e.Message}");
        }
    }

    /// <summary>Rebuilds the "Design" group for whichever character is currently selected.</summary>
    private void RefreshDesigns()
    {
        try {
            ClearGroup(_designsGroup, _designButtons);

            if (_selectedCharacter.Length == 0) return;

            foreach (var name in Ipc.GetCharacterDesigns(_selectedCharacter)) {
                var button = new MenuPopup.Button(TruncateLabel(name)) {
                    Selected          = name == _selectedDesign,
                    ClosePopupOnClick = false,
                };

                button.OnClick = () => OnDesignSelected(name);

                _designButtons[name] = button;
                _designsGroup.Add(button);
            }
        } catch (Exception e) {
            Logger.Error($"[CharacterSelectWidget] RefreshDesigns failed: {e.Message}");
        }
    }

    /// <summary>
    /// Truncates a character/design name to the configured
    /// "MaxLabelLength" (0 = unlimited), appending an ellipsis. Only affects
    /// the displayed button label - callers still use the untruncated name
    /// for identity (dictionary keys, OnClick closures, IPC calls).
    /// </summary>
    private string TruncateLabel(string name)
    {
        int maxLength = GetConfigValue<int>("MaxLabelLength");

        if (maxLength <= 0 || name.Length <= maxLength) return name;

        return name[..maxLength] + "…";
    }

    /// <summary>
    /// When enabled via config, caps the "Design" list's content node to the
    /// rendered height of the "Character" list and makes it scroll instead of
    /// growing the popup taller than the character column.
    /// </summary>
    private void SyncDesignListHeight()
    {
        try {
            Node? designsContent = _designsGroup.Node.QuerySelector(".content");
            if (designsContent is null) return;

            float charactersHeight = _charactersGroup.Node.OuterHeight;

            if (GetConfigValue<bool>("MatchDesignsHeightToCharacters") && charactersHeight > 0) {
                designsContent.ToggleClass("scrollbars", true);
                designsContent.Overflow      = false;
                designsContent.Style.Size    = new Size(0, charactersHeight);
                designsContent.Style.Padding = new EdgeSize(0, 10, 0, 0); // Room for the scrollbar.
            } else {
                designsContent.ToggleClass("scrollbars", false);
                designsContent.Overflow      = true;
                designsContent.Style.Size    = null;
                designsContent.Style.Padding = null;
            }
        } catch (Exception e) {
            // Runs every frame while the popup is open - must never throw and
            // interrupt Umbra's render loop mid-frame.
            Logger.Warning($"[CharacterSelectWidget] SyncDesignListHeight failed: {e.Message}");
        }
    }

    private void OnDesignSelected(string name)
    {
        try {
            _selectedDesign = name;

            foreach (var (buttonName, button) in _designButtons) {
                button.Selected = buttonName == name;
            }

            UpdateStatusLabel();

            if (GetConfigValue<bool>("ApplyOnDesignClick")) {
                ApplySelectedDesign();
            }
        } catch (Exception e) {
            // See the comment on OnCharacterSelected - this must never throw.
            Logger.Error($"[CharacterSelectWidget] OnDesignSelected('{name}') failed: {e.Message}");
        }
    }

    /// <summary>The "third button" - applies whatever character/design is currently selected.</summary>
    private void ApplySelectedDesign()
    {
        try {
            if (_selectedCharacter.Length == 0 || _selectedDesign.Length == 0) {
                ToastGui.ShowError("Pick a character and a design first.");
                return;
            }

            bool success = Ipc.SwitchToCharacterDesign(_selectedCharacter, _selectedDesign);

            if (success) {
                _currentCharacter = _selectedCharacter;
                _currentDesign    = _selectedDesign;

                ToastGui.ShowNormal($"Applied '{_selectedDesign}' to {_selectedCharacter}.");

                if (GetConfigValue<bool>("ClosePopupAfterApply")) {
                    Popup.Close();
                }
            } else {
                ToastGui.ShowError($"Failed to apply '{_selectedDesign}' to {_selectedCharacter}.");
            }

            UpdateStatusLabel();
        } catch (Exception e) {
            // See the comment on OnCharacterSelected - this must never throw.
            Logger.Error($"[CharacterSelectWidget] ApplySelectedDesign failed: {e.Message}");
        }
    }

    private void UpdateStatusLabel()
    {
        var (prefix, rest) = GetStatusLabelParts();
        _selectionStatus.SetLabel(prefix, rest);
    }

    private (string Prefix, string Rest) GetStatusLabelParts()
    {
        if (_selectedCharacter.Length == 0) return (string.Empty, "No character selected");
        if (_selectedDesign.Length == 0) return (string.Empty, $"{_selectedCharacter} — pick a design");

        return ("Selected:", $"{_selectedCharacter} — {_selectedDesign}");
    }
}
