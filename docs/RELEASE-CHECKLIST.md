# Release checklist

- [ ] Version and changelog updated
- [ ] CI build, self-test, dependency audit, and provenance attestation pass
- [ ] `docs/TEST-MATRIX.md` completed for available machines
- [ ] Portable ZIP contains EXE, LICENSE, README, privacy, and third-party notices
- [ ] EXE and ZIP SHA-256 hashes published
- [ ] AuthKey absent from repository, artifacts, logs, and support-bundle sample
- [ ] Microsoft Defender scan clean; VirusTotal result reviewed if policy permits upload
- [ ] Code signature valid, timestamped, and from the expected publisher
- [ ] Update from the previous public release succeeds; rollback copy exists
- [ ] Known limitations and compatibility table remain honest
- [ ] Tag and GitHub release are public; downloadable assets match updater names
