# Third-party components

## Ours (C source in the repo under `native/`)

- **`Resources/xinput1_4.dll`** — the per-instance XInput proxy. Forwards to the real
  system DLL but exposes only one physical pad, as user index 0. Source:
  `native/xinput_filter.c`.
- **`Resources/dilist.exe`** — lists DirectInput controllers and their instance GUIDs.
  Needed because identical pads share a name, so GUIDs are the only way to tell
  them apart. Source: `native/dilist.c`.

Rebuild either with MSYS2/MinGW-w64 (gcc):

```
gcc -O2 -shared -o Resources/xinput1_4.dll native/xinput_filter.c -Wl,--kill-at -lkernel32 -luser32
gcc -O2 -o Resources/dilist.exe native/dilist.c -ldinput8 -ldxguid -lole32 -luuid
```

Note: gcc fails silently with exit 1 if `ucrt64\bin` isn't on PATH.

## Not ours

- **Proto Input** — per-process input and simulated-focus hooks by Ilyaki and
  contributors. <https://github.com/SplitScreen-Me/splitscreenme-protoInput>
  BeamSplit embeds its 64-bit loader/hooks and their EasyHook runtime dependencies.
  Proto Input is MIT licensed; its full notice is embedded in `BeamSplit.exe` and
  extracted to `%LOCALAPPDATA%\BeamSplit\bin\protoinput\ProtoInput-LICENSE.txt`.

- **EasyHook** — Windows API hooking runtime used by Proto Input.
  <https://github.com/EasyHook/EasyHook> (MIT licence). Its binaries are used only
  as part of Proto Input injection.

- **devreorder** (`bin\dinput8.dll`) — DirectInput device hider/reorderer by
  Brian Kendall. <https://github.com/briankendall/devreorder>
  Used per instance to hide the other players' pads from DirectInput
  enumeration. If it's missing, BeamSplit still separates pads through the
  XInput proxy; the game just also lists the other controllers. Setup can copy
  it from a Nucleus Co-op install, or you can download it yourself.
  Distributed under its own licence — check the repo before redistributing.

- **BeamMP** — client mod and server, by the BeamMP team (AGPL-3.0-or-later).
  <https://github.com/BeamMP/BeamMP> · <https://github.com/BeamMP/BeamMP-Server>
  BeamSplit does **not** bundle these. Setup downloads them from the official
  GitHub releases at your request. The client mod is installed unmodified — the
  version-matching picks the release built for your game, rather than patching
  a newer one.

- **BeamNG.drive** — the game itself, by BeamNG GmbH. Not included, not
  modified. BeamSplit only creates junctions/hardlinks to it and writes to its
  own instance folders.

## What BeamSplit changes on your system

Nothing outside this folder, with two exceptions worth knowing:

1. Per-instance profiles live in `instances\`, not in your normal BeamNG
   profile, so your single-player settings and mods are untouched.
2. If you download the BeamMP server through Setup, it lands in `server\` here.

Uninstalling is deleting BeamSplit.exe plus %LOCALAPPDATA%\BeamSplit. Instance folders contain junctions to your
game install — delete them with the app (Advanced tab) or with
`cmd /c rmdir` on the junctions, **not** with a tool that follows links, or you
risk deleting into the real install.
