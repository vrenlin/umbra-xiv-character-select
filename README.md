# Umbra.CharacterSelectWidget

An [Umbra](https://github.com/una-xiv/umbra) toolbar widget that connects to
[Character Select+](https://github.com/IcarusXIV/Character-Select-) over
Dalamud IPC. It shows the active character/design on the toolbar and lets you
pick a different character and design from a popup, then apply it with a
dedicated **Apply Design** button.

## What it does

- **Toolbar button** — shows the currently active Character Select+ character
  as the main label, and the last-known applied design as the sub-label.
- **Popup → "Character" group** — one button per saved character. Clicking one
  selects it (highlighted) and refreshes the design list below.
- **Popup → "Design" group** — one button per design belonging to the selected
  character. Clicking one selects it (highlighted).
- **Status line** — a plain (non-clickable) line summarizing the current
  selection, e.g. `Selected: Aymeric — Formal Attire`.
- **Apply Design button** — the third, distinct action: calls Character
  Select+'s `SwitchToCharacterDesign` IPC method with whatever character/design
  is currently selected, shows a toast on success/failure, and (optionally)
  closes the popup.

Two widget config options are exposed in Umbra's widget settings:
- **Apply immediately when clicking a design** — skips the Apply button and
  applies as soon as you click a design (off by default).
- **Close popup after applying** — auto-closes the popup once Apply succeeds
  (on by default).

If Character Select+ isn't installed, Umbra won't even let this widget be
added to the toolbar — see "How the dependency works" below.

## Project layout

```
Umbra.CharacterSelectWidget/
├── Umbra.CharacterSelectWidget.csproj
├── Services/
│   └── CharacterSelectIpc.cs   # Wraps Character Select+'s IPC calls
└── Widgets/
    └── CharacterSelectWidget.cs # The toolbar widget + popup UI
```

This follows the same project shape as
[Umbra.SamplePlugin](https://github.com/una-xiv/Umbra.SamplePlugin): it's a
plain class library, not a standalone Dalamud plugin. Umbra loads the built
DLL directly and scans it for `[Service]` and `[ToolbarWidget]`/
`[InteropToolbarWidget]` classes — there's no `Plugin.cs`/`IDalamudPlugin`
entry point or plugin manifest JSON needed.

## How the IPC integration works

Character Select+'s `IPCProvider.cs` registers these call gates (see
`CharacterSelectPlugin/IPCProvider.cs` in that repo):

| IPC name                                    | Signature                          |
|----------------------------------------------|-------------------------------------|
| `CharacterSelect.GetCharacterList`             | `() -> string[]`                    |
| `CharacterSelect.GetCurrentCharacter`          | `() -> string`                      |
| `CharacterSelect.GetCharacterDesigns`          | `(string) -> string[]`              |
| `CharacterSelect.SwitchToCharacter`            | `(string) -> bool`                  |
| `CharacterSelect.SwitchToCharacterDesign`      | `(string, string) -> bool`          |
| `CharacterSelect.OnCharacterChanged`           | broadcast `(string, string)`        |

`Services/CharacterSelectIpc.cs` wraps each of these behind an
`ICallGateSubscriber<...>`, obtained via `Framework.DalamudPlugin` (Umbra's
static accessor for the `IDalamudPluginInterface`). Every call is wrapped in a
try/catch so the widget degrades gracefully if Character Select+ hasn't
finished loading yet or changes its IPC contract in a future update, instead
of crashing the toolbar.

It also subscribes to the `OnCharacterChanged` broadcast so the toolbar label
updates immediately if the character/design changes from *outside* this
widget (e.g. from Character Select+'s own Quick Switch window).

## How the dependency works

The widget is registered with:

```csharp
[InteropToolbarWidget(
    "CharacterSelectWidget",
    "Character Select",
    "Shows the active Character Select+ character and design, and lets you switch designs from the toolbar.",
    "CharacterSelectPlugin"   // Character Select+'s InternalName, from its .json manifest
)]
```

`InteropToolbarWidgetAttribute`'s fourth parameter is the *internal name* of
the plugin this widget depends on. Umbra checks that a plugin with that
internal name is installed and loaded before it allows the widget to be added
via the "Add Widget" window — the same mechanism `Umbra.Glamourer` uses for
its dependency on Glamourer.

## Building

1. Install [Umbra](https://github.com/una-xiv/umbra) and
   [Character Select+](https://github.com/IcarusXIV/Character-Select-)
   through Dalamud's plugin installer at least once, so their DLLs exist on
   disk.
2. Open `Umbra.CharacterSelectWidget.sln` in Visual Studio / Rider, or build
   from the CLI:
   ```
   dotnet build -c Release
   ```
   The `.csproj` locates `Umbra.dll`/`Umbra.Common.dll` automatically from
   `%AppData%\XIVLauncher\installedPlugins\Umbra\<version>\`, and Dalamud's
   assemblies from `%AppData%\XIVLauncher\addon\Hooks\dev\`. If you're on a
   local Umbra dev build instead, edit the commented-out `UmbraLibPath` line
   in the `.csproj`.
3. The built DLL will be in `out\Release\Umbra.CharacterSelectWidget.dll`.
4. In-game, open Umbra's Settings → **Plugins**, and point it at the folder
   containing the built DLL.
5. Add the "Character Select" widget to your toolbar from the "Add Widget"
   window (it'll only show up if Character Select+ is installed).

## Customizing

- **Icon**: `OnLoad()` calls `SetGameIconId(64024u)` as a placeholder — swap
  in whatever game icon ID, FontAwesome icon, or bitmap font icon you'd
  rather use (`SetFontAwesomeIcon`, `SetGfdIcon`, etc. are all available since
  `StandardWidgetFeatures.CustomizableIcon` is enabled).
- **Apply-on-click design list**: if you'd rather designs apply the moment
  they're clicked (no separate Apply button), toggle "Apply immediately when
  clicking a design" in the widget's config, or just flip the default in
  `GetConfigVariables()`.
- **Switching character only (no design)**: `CharacterSelectIpc` also exposes
  `SwitchToCharacter(name)` if you want to wire up a "switch character,
  keep current design" action too.
