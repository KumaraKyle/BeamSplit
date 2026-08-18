# BeamSplit

**Release 1 · v1.6.1**

Local splitscreen for **BeamNG.drive** — two to four players on one PC, each on their
own screen (or their own slice of one), each with their own controller.

- **Solo** — every player gets an independent world. Any BeamNG version.
- **BeamMP** — everyone shares one world through a BeamMP server on your own machine.

Portable `BeamSplit.exe`, nothing to install, no .NET or PowerShell needed. It doesn't
modify your BeamNG install; everything it creates lives in its own folders.

---

## Start here

1. Run **`BeamSplit.exe`**. The first-run **Guide** walks through session type,
   players, prerequisites, screens, server and audio in a few short steps.
2. Let **Fix setup** repair the safe automatic items. Anything requiring your choice
   gets a direct button to the right screen.
3. Choose a screen preset, review the final summary, and launch. All player pipelines
   start in parallel.
4. Next time, use the redesigned **Play** dashboard: choose mode/players and launch;
   its readiness meter tells you what is missing before you wait.
5. BeamMP sessions guest-login and direct-connect themselves when the local server is ready.

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

**Guide** — the first-run tour. It remembers your place, repairs downloadable setup
items, offers useful screen presets, and leaves advanced choices on their proper pages.
You can reopen it whenever a new player needs the tour.

**Cockpit tour** — a reusable six-stop walkaround over the real Play, Screens, Server,
Mods, Session and Settings pages. Start it from the lower-left **Take cockpit tour**
button; it explains what every screen is for without changing the rig.

**Play** — the single starting point: switch between the first-time Setup guide and
Quick play without changing pages, then choose player count, repair setup or launch. The
detailed prerequisite checklist remains directly below whenever something needs attention.

**Screens** — a map of your actual displays in Windows display-number order. Split any screen 1 / 2 stacked /
2 side-by-side / 4, drag pads onto regions, hit **Identify** if you're not sure which
pad is which. Layout buttons retile running games immediately, and **Apply to running
session** pushes both pads and placement live. The map
re-renders if you plug or unplug a display.

**Server** — the BeamMP server settings (name, port, players, cars, map, AuthKey).

**Mods** — points at your normal BeamNG `mods\repo` library and mounts that same folder
inside every player profile using Windows directory junctions. There are no duplicate
personal mod copies and every local player sees the same library immediately. Per-package
checkboxes are only for choosing the optional BeamMP server mod pack copied into
`Resources\Client`. BeamSplit tracks those server files, so hand-installed packages and
the pinned BeamMP client are left alone. Restart a running server after changing its pack.

**Session** — the launch dashboard opens automatically and replaces the loose launcher
terminals. Its two car-style gauges show whole-system load and RAM; compact cards show
running instances and world sync. Driver cards show ports, controllers, per-process CPU/RAM, BeamMP client
health, and launcher/game log previews. Live state:
`Idle → Building → Launching → Waiting for launcher → Game running → Connected → Synced`.
A failing card names the actual missing signal — e.g. *connected to the launcher, not
synced into the world* — instead of a generic error.

Launch begins behind a skippable borderless-fullscreen film while every instance pipeline
runs in parallel. One beam splits into a live pane for each player, the panes pulse with
their individual launch progress, then resolve into the actual monitor and screen-region
layout when every game window has been created, tiled and stabilized. The film can sustain
indefinitely on a cold launch and resolves immediately when the real work finishes. Disable
it or preview it safely under Settings → Session behaviour.

In BeamMP mode, each instance automatically requests guest login and Direct Connects to
`127.0.0.1` on the port in `ServerConfig.toml` as soon as its launcher is authenticated.
There is no Multiplayer/login/Direct Connect menu routine to repeat. Disable **Automatically
guest-login and join the local BeamMP server** under Settings → Session behaviour if you
want to choose a different server manually.

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

---

## Portable updates

Settings → **BeamSplit updates** checks the official GitHub release channel. BeamSplit
downloads only a release asset named `BeamSplit.exe`, `BeamSplit-portable.zip`, or
`BeamSplit.zip`, verifies the SHA-256 digest supplied by GitHub, keeps the previous EXE
as `BeamSplit.exe.previous`, and restarts in place. Config, profiles and instances are
not replaced.

The release repository and release must be public for unauthenticated installs. An
update without GitHub's SHA-256 digest is shown but will not be installed automatically.
