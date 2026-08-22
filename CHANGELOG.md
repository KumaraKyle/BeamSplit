# Changelog

## 1.9.1

- Fixed an immediate multi-instance startup crash after the configured BeamNG install
  moved or changed. BeamSplit now detects stale content junctions and rebuilds only the
  lightweight game-folder links while preserving every player's profile.

## 1.9.0

- Added an optional two-player, offline single-instance split-screen engine. It uses
  BeamNG's native multiseat device assignment, independent camera contexts, render
  viewports and dormant split-screen HUD inside one shared game process.
- Added a dedicated `Single` profile, generated per-launch viewport/device manifest,
  embedded Lua mod, capability gate, monitor-spanning borderless layout and seat-aware
  session dashboard. The stable multi-instance/BeamMP engine remains the default.
- Updated heavy-map memory advice so single-instance sessions are estimated as one map
  load plus two simulated vehicles instead of two complete game processes.
- Hardened BeamMP server-mod deployment against malformed ZIPs, non-UTF-8 mod metadata,
  and Windows filenames that corrupt BeamMP's generated `mods.json`. Managed server-mod
  filenames are now ASCII-normalized without touching their source files, and offending
  hand-installed packages are identified before launch.
- BeamMP launcher DNS failures now surface as an actionable authentication error instead
  of starting game instances that remain indefinitely at 0/2.

## 1.8.1

- Added per-player recovery controls to Session: a stopped, crashed, or unhealthy
  instance can relaunch with its existing profile, controller, BeamMP port and screen
  assignment without interrupting healthy players or the local server.
- Added a thumbnail map picker to Play using artwork read directly from the installed
  BeamNG archives. A live switch restarts only the local BeamMP server, leaving every
  BeamNG process open while BeamSplit auto-join, when enabled, reconnects them onto the
  new map.
- Replaced the nested map-strip scrollbar with a wheel/trackpad-aware carousel and
  large previous/next controls.
- Reduced multi-instance memory overhead by sharing immutable Bin64 files across
  instances and disposing recurring native process handles correctly.
- Added an optional low-memory graphics preset based on BeamNG 0.39's Lowest settings:
  quarter-resolution textures, minimum world detail, and expensive reflections,
  vegetation, clouds and post-processing disabled.
- Added a pre-launch memory advisor for Italy, Utah, West Coast USA and Johnson Valley.
  On constrained multi-player systems it shows installed/free RAM and offers to enable
  the low-memory preset without blocking users who prefer their current settings.
- Fixed the offline map picker so selecting a new map enables “Use for next launch,”
  then clearly reports when that choice has been saved.

## 1.8.0

- Split the crowded launch/setup workspace into dedicated Setup and Play pages. Setup
  now owns the detailed green/red readiness checklist, repairs, first-run guide and log;
  Play combines mode, player count, fast screen presets and common launch options.
- Added a controller configuration shortcut beneath Players and world.
- Fixed BeamMP server-mod checkboxes being cleared during Apply, made long mod lists
  independently scrollable, clarified selected versus deployed server packages, and
  separated Stop session from Stop all (including server).
- Added an official BeamNG repository browser to Mods with sorting, page navigation,
  thumbnails, local search, download progress, ZIP/path validation, atomic installs and
  a separate BeamSplit-managed zero-copy library that never writes into the user's
  existing mods.

## 1.7.0

- Replaced the initial zero-download MIT release with AGPL-3.0-or-later: anyone who
  distributes a modified BeamSplit must provide the corresponding source, and modified
  network-accessible versions must offer source to their users.
- Added public-release licensing, third-party notices, privacy/security/support policy,
  compatibility matrix, issue forms, Dependabot, and contributor documentation.
- Third-party GitHub downloads now require and verify the release asset SHA-256 digest
  before replacing a cached client, server executable, or devreorder binary.
- Config writes are atomic and keep a last-known-good backup that startup can restore.
- BeamMP AuthKeys are masked by default, and Console diagnostics are redacted.
- Added one-click redacted support bundles and release-safety self-tests.
- Added Windows CI and tagged-release automation with hashes and build provenance.

## 1.6.2

- Moved the complete launch pipeline onto a worker thread so blocking instance repair,
  mod/input deployment, process startup, or window discovery cannot freeze the launch
  cinematic's UI-thread render loop.
- Launch progress now reports directly from the worker into thread-safe telemetry rather
  than posting every milestone back through WPF's dispatcher.
- Demoted integrated-console log rendering below animation and input priority, preventing
  bursts of launcher output from starving cinematic frames.

## 1.6.1

- Replaced per-player personal mod copies with one Windows directory junction per
  profile, so every player reads the same library instantly with no duplicate storage.
- Simplified the Mods page: the library checkbox is the single player-profile switch,
  while per-package checkboxes now apply only to BeamMP's server-distributed mod pack.
- Default detection mounts `mods/repo` rather than the parent mods folder, preventing
  the pinned or downloaded multiplayer packages from appearing through the shared link.
- Updated the self-test to verify junction creation/removal and source preservation.

## 1.6.0

- Added a dedicated Mods page that discovers ZIP packages from the normal BeamNG user
  folder and lets each package target local player profiles, the local BeamMP server,
  or both.
- Personal mod sources remain read-only: BeamSplit copies selections into an isolated
  managed subfolder in each profile and never touches the pinned multiplayer client.
- Server selections sync into `Resources/Client`; BeamSplit tracks its own files so
  hand-installed server mods are never removed or overwritten during later syncs.
- Mod selections refresh automatically before launch, missing source folders preserve
  existing copies, and the self-test now covers install/remove/source-preservation.

- Let BeamMP's built-in auto-login settle before falling back to guest authentication,
  preventing two authentication replies from racing during automatic local-server join.

## 1.5.1

- BeamMP sessions now guest-login and Direct Connect each instance to BeamSplit's local
  server automatically, removing the Multiplayer → guest → Direct Connect menu routine.
- Auto-join uses BeamMP's own Lua API, waits for launcher authentication, retries safely,
  reads the configured server port, and can be disabled under Settings → Session behaviour.

## 1.5.0

- Rebuilt the launch film as kinetic light that visualizes the product itself: one beam
  splits into a live pane per player, then resolves into the configured screen geometry.
- The film now uses an intro, a seamless indefinite progress loop, and a success/fault
  resolve driven by the real parallel launch task instead of a fixed-duration cartoon.
- Each pane reacts to its own player pipeline, while the shared light ribbon and log feed
  expose real launch progress without spawning separate terminal windows.
- Launch progress now eases continuously between milestones and retargets from its current
  on-screen position when parallel player updates arrive in quick succession.
- The resolved panes mirror configured monitor and split regions, including multi-monitor
  layouts and the intentionally empty quadrant in a three-player four-grid layout.
- Cached Skia resources, aspect-correct capped rendering and adaptive detail keep the film
  responsive while BeamNG instances contend for the machine.
- Launch playback is genuinely borderless fullscreen and stays above game windows until
  handoff; Skip still returns immediately while launch continues safely behind it.
- Hidden BeamNG auxiliary console windows are now folded into BeamSplit's integrated
  logging experience alongside the already-hidden BeamMP launchers.

## 1.4.0

- Merged the first-time setup guide into Play with an in-page Quick play / Setup guide
  switcher; removed the duplicate Guide navigation destination.
- Simplified Session telemetry to two meaningful gauges: whole-system CPU load and RAM.
- Replaced instance and synchronization dials with readable status cards.
- Reworked dashboard captions and hardware details into separate wrapping rows to stop
  value, label and system-spec text collisions at common window sizes and DPI levels.

## 1.3.1

- Fixed retile accepting the correct rectangle while BeamNG had restored a
  fullscreen/maximized or captioned window style.
- Retile now clears maximized state, normalizes borderless/bordered styles in both
  directions, and verifies the style alongside the window bounds.
- Extended live-retile stabilization to survive BeamNG's delayed window recreation.

## 1.3.0

- Rebuilt the launch cinematic as a single Skia-rendered scene with one camera and
  cohesive art direction instead of independently animated WPF controls.
- Added a neon road environment, horizon lighting, motion echoes, deterministic wall
  destruction, sparks, shockwave, scanline/vignette grading and a cinematic title card.
- Capped the internal render surface while preserving full-window output so the launch
  film stays responsive while all player pipelines start in parallel behind it.

## 1.2.0

- Added a reusable six-stop cockpit tour over the real app screens, including the live
  process monitor and integrated console workflow.
- Rebuilt Session as a car-style telemetry dashboard with vector CPU, memory, instance
  and synchronization gauges plus per-player load and working-set meters.
- Added a skippable full-window launch cinematic with a vector car, impact flash,
  individually scattered bricks and speed lines.
- The real parallel instance launch now begins behind the cinematic so the animation
  hides early setup time instead of delaying it.
- Added a safe cinematic preview and an enable/disable setting.

## 1.1.0

- Added a persistent five-step first-run Guide covering players, setup repair, screen
  layouts, BeamMP/server/audio choices, and launch readiness.
- Rebuilt Quick Start as a practical launch dashboard with mode/player controls,
  animated readiness progress, missing-item guidance, and direct screen/guide actions.
- Added background and manual portable updates from GitHub Releases. Downloads require
  GitHub's SHA-256 asset digest, retain the previous executable, and restart in place.
- Added shared setup-repair orchestration so Guide and Play use the same checks/fixes.

- Preserve customized screen assignments when launching; player-count validation no
  longer resets vertical/side-by-side layouts to one full-screen monitor per player.
- Keep keyboard/no-pad instances attached to Proto Input so a controller can be
  assigned live without restarting the game.
- Preserve a running instance's identity when another player drops out, preventing
  controller handles, ports, profiles, and windows from shifting to the wrong player.
- Match retiling by instance process instead of process-list order.
- Restore maximized windows and verify their bounds repeatedly through BeamNG's late
  startup display-mode resets.
- Apply and verify tiling after screen/controller drag changes, not only split buttons.
- Added BeamNG audio controls for master/effects/music/UI levels, background audio,
  stereo-headphones mode, and the Windows output endpoint used by every instance.
- Added a shared-speaker mix that keeps the full world mix on P0 and mutes later
  instances, eliminating doubled/phasing BeamMP vehicle audio on one speaker system.
- Added a recommended per-player BeamMP mix which uses BeamMP's local/remote vehicle
  flag to suppress duplicate remote vehicle emitters while retaining both listeners.

## 1.0.0 — Release 1

First packaged BeamSplit release.

- Parallel multi-instance BeamNG.drive and BeamMP launch.
- Independent per-instance game folders, profiles, ports, and controller routing.
- Proto Input focus-independent XInput isolation with devreorder/proxy fallbacks.
- Natural `DISPLAY1`, `DISPLAY2`, `DISPLAY3` assignment order.
- Live full, stacked, side-by-side, and four-grid window retiling.
- BeamMP client matching for older BeamNG versions.
- Clean launch replaces stale BeamSplit processes, preventing port collisions and
  per-instance BeamMP client overwrites.
- Hidden BeamMP launcher consoles with their output integrated into BeamSplit.
- Launch dashboard with server state, mod health, ports, controllers, process CPU/RAM,
  system specifications, and per-instance launcher/game log previews.
- Self-contained Windows x64 executable; no separate .NET installation required.
