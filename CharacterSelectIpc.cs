using System;
using Dalamud.Plugin.Ipc;
using Umbra.Common;

namespace Umbra.CharacterSelectWidget.Services;

/// <summary>
/// Thin wrapper around Character Select+'s IPC surface (see
/// <c>CharacterSelectPlugin.IPCProvider</c> in the Character-Select-+ repository:
/// https://github.com/IcarusXIV/Character-Select-/blob/master/CharacterSelectPlugin/IPCProvider.cs).
///
/// Every call is guarded because this widget can technically still be
/// instantiated even if Character Select+ isn't installed, hasn't finished
/// loading yet, or ships a different IPC contract in a future update.
/// </summary>
[Service]
public sealed class CharacterSelectIpc : IDisposable
{
    private const string SigGetCharacterList    = "CharacterSelect.GetCharacterList";
    private const string SigGetCurrentCharacter = "CharacterSelect.GetCurrentCharacter";
    private const string SigGetCharacterDesigns = "CharacterSelect.GetCharacterDesigns";
    private const string SigSwitchToCharacter   = "CharacterSelect.SwitchToCharacter";
    private const string SigSwitchToDesign      = "CharacterSelect.SwitchToCharacterDesign";
    private const string SigOnCharacterChanged  = "CharacterSelect.OnCharacterChanged";

    private readonly ICallGateSubscriber<string[]>             _getCharacterList;
    private readonly ICallGateSubscriber<string>                _getCurrentCharacter;
    private readonly ICallGateSubscriber<string, string[]>      _getCharacterDesigns;
    private readonly ICallGateSubscriber<string, bool>          _switchToCharacter;
    private readonly ICallGateSubscriber<string, string, bool>  _switchToDesign;
    private readonly ICallGateSubscriber<string, string, object> _onCharacterChanged;

    private readonly Action<string, string> _changedHandler;
    private          bool                    _isSubscribed;

    /// <summary>
    /// Raised whenever Character Select+ reports (via its own IPC broadcast)
    /// that the active character/design changed - including changes made
    /// from Character Select+'s own UI, not just through this widget.
    /// </summary>
    public event Action<string, string>? CharacterChanged;

    public CharacterSelectIpc()
    {
        var pi = Framework.DalamudPlugin;

        _getCharacterList    = pi.GetIpcSubscriber<string[]>(SigGetCharacterList);
        _getCurrentCharacter = pi.GetIpcSubscriber<string>(SigGetCurrentCharacter);
        _getCharacterDesigns = pi.GetIpcSubscriber<string, string[]>(SigGetCharacterDesigns);
        _switchToCharacter   = pi.GetIpcSubscriber<string, bool>(SigSwitchToCharacter);
        _switchToDesign      = pi.GetIpcSubscriber<string, string, bool>(SigSwitchToDesign);
        _onCharacterChanged  = pi.GetIpcSubscriber<string, string, object>(SigOnCharacterChanged);

        _changedHandler = (character, design) => CharacterChanged?.Invoke(character, design);

        TrySubscribe();
    }

    /// <summary>
    /// Best-effort check for whether Character Select+ is currently responding
    /// to IPC calls. Does not guarantee every endpoint below will succeed.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try {
                _getCharacterList.InvokeFunc();
                return true;
            } catch {
                return false;
            }
        }
    }

    /// <summary>Returns the names of every character saved in Character Select+.</summary>
    public string[] GetCharacterList()
    {
        try {
            return _getCharacterList.InvokeFunc() ?? [];
        } catch (Exception e) {
            Logger.Warning($"[CharacterSelectIpc] GetCharacterList failed: {e.Message}");
            return [];
        }
    }

    /// <summary>Returns the name of the currently active character, or an empty string if none.</summary>
    public string GetCurrentCharacter()
    {
        try {
            return _getCurrentCharacter.InvokeFunc() ?? string.Empty;
        } catch (Exception e) {
            Logger.Warning($"[CharacterSelectIpc] GetCurrentCharacter failed: {e.Message}");
            return string.Empty;
        }
    }

    /// <summary>Returns the names of every design belonging to the given character.</summary>
    public string[] GetCharacterDesigns(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return [];

        try {
            return _getCharacterDesigns.InvokeFunc(characterName) ?? [];
        } catch (Exception e) {
            Logger.Warning($"[CharacterSelectIpc] GetCharacterDesigns failed: {e.Message}");
            return [];
        }
    }

    /// <summary>Switches to the given character without applying a specific design.</summary>
    public bool SwitchToCharacter(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return false;

        try {
            return _switchToCharacter.InvokeFunc(characterName);
        } catch (Exception e) {
            Logger.Warning($"[CharacterSelectIpc] SwitchToCharacter failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Switches to the given character and applies the given design.</summary>
    public bool SwitchToCharacterDesign(string characterName, string designName)
    {
        if (string.IsNullOrEmpty(characterName) || string.IsNullOrEmpty(designName)) return false;

        try {
            return _switchToDesign.InvokeFunc(characterName, designName);
        } catch (Exception e) {
            Logger.Warning($"[CharacterSelectIpc] SwitchToCharacterDesign failed: {e.Message}");
            return false;
        }
    }

    private void TrySubscribe()
    {
        if (_isSubscribed) return;

        try {
            _onCharacterChanged.Subscribe(_changedHandler);
            _isSubscribed = true;
        } catch (Exception e) {
            Logger.Warning($"[CharacterSelectIpc] Failed to subscribe to OnCharacterChanged: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (_isSubscribed) {
            try {
                _onCharacterChanged.Unsubscribe(_changedHandler);
            } catch {
                // Character Select+ may have already unloaded - nothing to do.
            }

            _isSubscribed = false;
        }

        CharacterChanged = null;
    }
}
