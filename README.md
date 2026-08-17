# BeamSplit

**Release 1 · v1.0.0**

Local splitscreen for **BeamNG.drive** — two to four players on one PC, each on their
own screen (or their own slice of one), each with their own controller.

- **Solo** — every player gets an independent world. Any BeamNG version.
- **BeamMP** — everyone shares one world through a BeamMP server on your own machine.

Portable `BeamSplit.exe`, nothing to install, no .NET or PowerShell needed. It doesn't
modify your BeamNG install; everything it creates lives in its own folders.

---

## Start here

1. Run **`BeamSplit.exe`**.
2. On **Play**, choose the player count and press **Check setup & launch**. BeamSplit
   fixes everything it safely can, then starts all player pipelines in parallel.
3. Only open **Screens** if you want to change which controller/display belongs to a
   player or divide one monitor into regions.
4. BeamMP only: in each window, **Multiplayer → Direct Connect → `127.0.0.1:30814`**.

First launch builds one game folder per player and BeamNG compiles shaders per profile,
so it's slow once. After that it's quick.

### Requirements

- Windows 10/11 64-bit, BeamNG.drive, one XInput pad per player
- Shared world also needs BeamMP installed and a free AuthKey from
  <https://keymaster.beammp.com> — required even for a private LAN server
- ~500 MB disk per player

---

## Controllers and focus

Proto Input gives every controller instance a private XInput slot and simulated focus.
You can click a game, use its menus, and move focus between windows without the pads
merging. The older XInput proxy and devreorder remain deployed as fallback layers.

Controller-per-instance is the supported reliable arrangement. A keyboard player still
uses Windows' normal focused-keyboard path; BeamSplit does not yet split one keyboard
from the controllers independently.

---

## Tabs

**Play** — the normal starting point: player count, one-button setup/launch, plus the
detailed prerequisite checklist if something needs attention.

**Screens** — a map of your actual displays in Windows display-number order. Split any screen 1 / 2 stacked /
2 side-by-side / 4, drag pads onto regions, hit **Identify** if you're not sure which
pad is which. Layout buttons retile running games immediately, and **Apply to running
session** pushes both pads and placement live. The map
re-renders if you plug or unplug a display.

**Server** — the BeamMP server settings (name, port, players, cars, map, AuthKey).

**Session** — the launch dashboard opens automatically and replaces the loose launcher
terminals. It shows CPU, GPU, RAM, displays, server state, ports, controller assignment,
per-instance CPU/RAM, BeamMP client health, and launcher/game log previews. Live state:
`Idle → Building → Launching → Waiting for launcher → Game running → Connected → Synced`.
A failing card names the actual missing signal — e.g. *connected to the launcher, not
synced into the world* — instead of a generic error.

**Console** (`Ctrl+\``) — app, server and every instance's logs in one place, with source
filters, search and **Copy diagnostics**. There's a command bar too:
`launch 2 · stop · retile · park · assign 0 1 · server start|stop · logs · guard`.

---

## How it works

| Piece | Approach |
|---|---|
| Separate profile | `<instance>\game\startup.ini` → its own userpath |
| Separate game folder | junctions for content, hardlinks for root files, real copy of `Bin64` only |
| Separate controller | Proto Input XInput/focus hooks, plus devreorder and the XInput proxy as fallbacks |
| Separate BeamMP port | one launcher per instance, ports spaced by 2 |
| Window placement | `SetWindowPos`, borderless, per monitor or per region |

It forces every profile to windowed mode, keeps audio/input alive in the background,
and applies the same configurable FPS cap to foreground and background windows:

```
AudioMuteOnWindowLoseFocus  true  -> false
unfocusedInput              false -> true
GraphicDisplayModes         *     -> Window
fpsLimitEnabled             *     -> true
fpsLimit                    *     -> 60 (default)
fpsLimitBackgroundEnabled   *     -> true
fpsLimitBackground          *     -> 60 (default)
```

**Version matching (BeamMP).** The BeamMP launcher always downloads the *newest* client,
which silently deactivates itself on an older BeamNG — you get no Multiplayer button.
BeamSplit lets the launcher complete its update pass, then installs the release built
for *your* game version immediately before BeamNG starts. Starting a new session first
stops only processes under BeamSplit's instance folder, preventing stale launchers from
holding ports or replacing one player's compatible client.

---

## Troubleshooting

**Both pads drive one car** — Settings → Input isolation should have **Proto Input**
enabled, and Play should show Proto Input as installed. Stop and relaunch after changing
input settings. The Console should say `Proto Input pad N, fake focus` for every player.

**No Multiplayer button** — client/game version mismatch. Setup → *BeamMP client version*
→ Fetch.

**Server won't start** — it needs an AuthKey even privately. Setup → *Server AuthKey*.

**Connected but players can't see each other** — the Session card will say
*not synced into the world yet*. Usually resolves on its own; if not, Stop and relaunch.

**An instance dies instantly with no log, or "DLL was not found"** — antivirus quarantined
a game file while the instance was being copied. BeamSplit warns about this during the
build. Add an exclusion for your instances folder and the game folder, then rebuild.

**Wrong game launches** — if you have more than one BeamNG install, Setup says so. Pick
the right one on Settings.

Logs and config: `%LOCALAPPDATA%\BeamSplit`.
