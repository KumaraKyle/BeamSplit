# Privacy

BeamSplit has no telemetry, advertising, analytics, accounts, or background data
collection. Configuration and logs stay under `%LOCALAPPDATA%\BeamSplit` and the
instance directory you choose.

Network access happens only for features you request or enable: checking GitHub for
BeamSplit updates, downloading official BeamMP/devreorder releases, opening the BeamMP
Keymaster site, browsing/downloading public mods from `www.beamng.com`, and running
BeamMP itself. The repository browser uses no BeamNG account, credentials, cookies,
telemetry, or background polling. BeamSplit does not send your BeamMP AuthKey.
The key is stored locally in BeamMP's `ServerConfig.toml` and is hidden in BeamSplit's UI.

**Create support bundle** is a local, user-initiated action. The bundle redacts AuthKey
values and the Windows user-profile path, but you should still inspect it before sharing.
Your shared personal-mod folder is mounted read-only in intent: BeamSplit enumerates and
links it but does not modify its packages. Server-pack selections are copied separately.
Mods explicitly downloaded in BeamSplit are stored separately under
`%LOCALAPPDATA%\BeamSplit\mods\repository` and linked into player profiles.
