# Changelog

## Unreleased

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
