# Security policy

Security fixes are supported on the latest BeamSplit release. Please do not disclose a
suspected vulnerability in a public issue. Use GitHub's **Security → Report a
vulnerability** form for this repository. If private reporting is unavailable, contact
the maintainer through the repository before publishing details.

Include the BeamSplit version, Windows version, affected feature, reproduction steps,
and impact. Never include a BeamMP AuthKey, Steam credentials, or unredacted logs.

BeamSplit downloads release assets only from the official GitHub repositories named in
`THIRD-PARTY.md`. Downloads must carry GitHub's SHA-256 asset digest or an explicitly
audited pinned hash and pass verification before BeamSplit installs them. Application updates also retain the previous executable
for rollback.
