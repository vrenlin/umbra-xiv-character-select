using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Umbra.CharacterSelectWidget.Services;
using Umbra.Common;
using Umbra.Widgets;

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
        StandardWidgetFeatures.Icon |
        StandardWidgetFeatures.CustomizableIcon;

    /// <inheritdoc/>
    public override MenuPopup Popup { get; } = new();

    private CharacterSelectIpc Ipc      { get; } = Framework.Service<CharacterSelectIpc>();
    private IToastGui          ToastGui { get; } = Framework.Service<IToastGui>();

    // Popup building blocks. Created once in OnLoad; contents refreshed on demand.
    private readonly MenuPopup.Group _charactersGroup = new("Character");
    private readonly MenuPopup.Group _designsGroup    = new("Design");

    private MenuPopup.Button _selectionStatus = null!;
    private MenuPopup.Button _applyButton     = null!;

    // Buttons currently shown in each group, kept around so we can toggle
    // their "Selected" highlight without needing to re-query the group.
    private readonly List<MenuPopup.Button> _characterButtons = [];
    private readonly List<MenuPopup.Button> _designButtons    = [];

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
                "When enabled, clicking a design in the list applies it right away instead of requiring the Apply button.",
                false
            ),

            new BooleanWidgetConfigVariable(
                "ClosePopupAfterApply",
                "Close popup after applying",
                "Whether the popup should close automatically after a design has been applied.",
                true
            ),
        ];
    }

    /// <inheritdoc/>
    protected override void OnLoad()
    {
        SetGameIconId(64024u); // Generic "appearance/mirror" style icon.
        SetText("Character Select");

        _selectionStatus = new(GetStatusLabel()) {
            IsDisabled        = true,
            ClosePopupOnClick = false,
        };

        _applyButton = new("Apply Design") {
            Icon              = 14u,
            OnClick           = ApplySelectedDesign,
            ClosePopupOnClick = false, // We close manually - see ApplySelectedDesign.
        };

        Popup.Add(_charactersGroup);
        Popup.Add(_designsGroup);
        Popup.Add(new MenuPopup.Separator());
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
    /// ourselves, using the tracking list.
    /// </summary>
    private static void ClearGroup(MenuPopup.Group group, List<MenuPopup.Button> trackedButtons)
    {
        foreach (var button in trackedButtons) {
            group.Remove(button, dispose: true);
        }

        trackedButtons.Clear();
    }

    /// <summary>Rebuilds the "Character" group. Called every time the popup is opened.</summary>
    private void RefreshCharacters()
    {
        ClearGroup(_charactersGroup, _characterButtons);

        string current = Ipc.GetCurrentCharacter();

        if (_selectedCharacter.Length == 0) {
            _selectedCharacter = current;
        }

        foreach (var name in Ipc.GetCharacterList()) {
            var button = new MenuPopup.Button(name) {
                Id                = $"char-{name}",
                Selected          = name == _selectedCharacter,
                ClosePopupOnClick = false,
            };

            button.OnClick = () => OnCharacterSelected(name);

            _characterButtons.Add(button);
            _charactersGroup.Add(button);
        }

        RefreshDesigns();
        UpdateStatusLabel();
    }

    private void OnCharacterSelected(string name)
    {
        if (_selectedCharacter == name) return;

        _selectedCharacter = name;
        _selectedDesign    = string.Empty;

        foreach (var button in _characterButtons) {
            button.Selected = button.Id == $"char-{name}";
        }

        RefreshDesigns();
        UpdateStatusLabel();
    }

    /// <summary>Rebuilds the "Design" group for whichever character is currently selected.</summary>
    private void RefreshDesigns()
    {
        ClearGroup(_designsGroup, _designButtons);

        if (_selectedCharacter.Length == 0) return;

        foreach (var name in Ipc.GetCharacterDesigns(_selectedCharacter)) {
            var button = new MenuPopup.Button(name) {
                Id                = $"design-{name}",
                Selected          = name == _selectedDesign,
                ClosePopupOnClick = false,
            };

            button.OnClick = () => OnDesignSelected(name);

            _designButtons.Add(button);
            _designsGroup.Add(button);
        }
    }

    private void OnDesignSelected(string name)
    {
        _selectedDesign = name;

        foreach (var button in _designButtons) {
            button.Selected = button.Id == $"design-{name}";
        }

        UpdateStatusLabel();

        if (GetConfigValue<bool>("ApplyOnDesignClick")) {
            ApplySelectedDesign();
        }
    }

    /// <summary>The "third button" - applies whatever character/design is currently selected.</summary>
    private void ApplySelectedDesign()
    {
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
    }

    private void UpdateStatusLabel()
    {
        _selectionStatus.Label = GetStatusLabel();
    }

    private string GetStatusLabel()
    {
        if (_selectedCharacter.Length == 0) return "No character selected";
        if (_selectedDesign.Length == 0) return $"{_selectedCharacter} — pick a design";

        return $"Selected: {_selectedCharacter} — {_selectedDesign}";
    }
}
