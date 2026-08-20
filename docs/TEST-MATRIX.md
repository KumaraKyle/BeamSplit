# Release test matrix

Record Pass, Fail, or Not Tested for every release candidate.

| Test | Windows 10 clean | Windows 11 clean | Development PC |
|---|---:|---:|---:|
| Starts without a separately installed .NET runtime |  |  |  |
| First-run guide and automatic detection |  |  |  |
| Solo, two players, one monitor horizontal/vertical |  |  |  |
| BeamMP, two players, direct auto-join |  |  |  |
| Two monitors and retile while running |  |  |  |
| Two distinct controllers remain isolated after focus changes |  |  |  |
| Controller reassignment after one instance exits |  |  |  |
| Three and four player launch |  |  |  |
| Audio background/local-vehicle modes |  |  |  |
| Shared personal mods and server mod pack |  |  |  |
| App update, SHA-256 verification, rollback |  |  |  |
| Support bundle redacts AuthKey/profile path |  |  |  |
| Defender/SmartScreen scan recorded |  |  |  |

Also record BeamNG, BeamMP client/server, controller firmware, GPU driver, monitor scale,
and any antivirus exclusions. Do not mark an untested cell as passing.
