# Third-party components

The portable release includes `THIRD-PARTY-NOTICES.txt` with the licence notices for
redistributed components.

## Included in BeamSplit.exe

- **Proto Input** by Ilyaki and contributors — MIT.
  <https://github.com/SplitScreen-Me/splitscreenme-protoInput>
- **EasyHook** — MIT. Redistributed as the runtime used by Proto Input.
  <https://github.com/EasyHook/EasyHook>
- **SkiaSharp** — MIT. Used to draw the launch film.
  <https://github.com/mono/SkiaSharp>
- **.NET runtime / System.Management** — MIT. The portable build is self-contained.
  <https://github.com/dotnet/runtime>

`xinput1_4.dll` and `dilist.exe` are BeamSplit components built from the C source under
`native/` and covered by BeamSplit's AGPL-3.0-or-later licence.

## Downloaded only when requested

- **devreorder** by Brian Kendall hides other DirectInput devices per instance.
  <https://github.com/briankendall/devreorder> BeamSplit can copy an existing Nucleus
  Co-op copy or download the official release. It extracts only `x64/dinput8.dll` into
  BeamSplit's app-data folder. It is not bundled in the BeamSplit release.
- **BeamMP client and server** — AGPL-3.0-or-later.
  <https://github.com/BeamMP/BeamMP> and <https://github.com/BeamMP/BeamMP-Server>
  BeamSplit downloads official release assets and selects a client compatible with the
  installed BeamNG version. It then adds BeamSplit-authored auto-join and audio-isolation
  Lua files to the local client ZIP. BeamMP is not bundled in the BeamSplit release.

GitHub's SHA-256 asset digest is required and checked before a requested third-party
download is installed. The older devreorder v1.0.4 asset predates that GitHub field, so
BeamSplit pins its audited official ZIP hash and rejects a different tag or payload.

## Not included

BeamNG.drive is commercial software by BeamNG GmbH. BeamSplit neither includes nor
licenses it and does not provide DRM or ownership bypasses. BeamSplit is an independent
community project and is not affiliated with, endorsed by, or sponsored by BeamNG GmbH,
BeamMP, Valve, Proto Input, EasyHook, devreorder, or Nucleus Co-op. Product names and
trademarks belong to their respective owners.

## Local changes and removal

Configuration/logs/default server data live under `%LOCALAPPDATA%\BeamSplit`. Instance
folders live at the location selected in Settings, often on the game volume. Personal
mods may be exposed through directory junctions; server packages are copied separately.
Official BeamNG repository downloads are user-requested community content fetched from
BeamNG's public website and stored separately under BeamSplit's application-data folder;
their authors retain their own rights and licenses.
Use BeamSplit's maintenance action to remove instances safely. Do not use a deletion tool
that follows directory junctions into the original game or mod folder.
