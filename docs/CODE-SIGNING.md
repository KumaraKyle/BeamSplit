# Code signing

Public Windows releases should use a trusted, timestamped Authenticode signature. A
self-signed certificate does not establish publisher reputation. Suitable routes include
a normal OV/EV certificate, Azure Trusted Signing, or SignPath Foundation for qualifying
open-source projects.

Signing credentials must live in the release platform's protected secret store, never in
the repository. Sign `BeamSplit.exe` before calculating hashes, creating the portable ZIP,
and generating provenance. Verify with `Get-AuthenticodeSignature` and `signtool verify
/pa /all /v BeamSplit.exe`. Unsigned builds must be labelled clearly as such.
