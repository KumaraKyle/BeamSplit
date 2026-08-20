# Privacy

BeamSplit has no telemetry, advertising, analytics, accounts, or background data
collection. Configuration and logs stay under `%LOCALAPPDATA%\BeamSplit` and the
instance directory you choose.

Network access happens only for features you request or enable: checking GitHub for
BeamSplit updates, downloading official BeamMP/devreorder releases, opening the BeamMP
Keymaster site, and running BeamMP itself. BeamSplit does not send your BeamMP AuthKey.
The key is stored locally in BeamMP's `ServerConfig.toml` and is hidden in BeamSplit's UI.

**Create support bundle** is a local, user-initiated action. The bundle redacts AuthKey
values and the Windows user-profile path, but you should still inspect it before sharing.
Your shared personal-mod folder is mounted read-only in intent: BeamSplit enumerates and
links it but does not modify its packages. Server-pack selections are copied separately.
