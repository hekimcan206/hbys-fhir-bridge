# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Database access | The bridge login has `SELECT, FLASHBACK` on clinical tables and `DELETE` on the change journal only; it cannot change clinical data or forge journal entries (integration-tested) |
| Data minimisation | The national identity number (TC Kimlik No) is not exported unless `Bridge:IncludeNationalId=true` |
| Integrity | Changes are captured in the same transaction as the write and consumed at one SCN; delivery is idempotent |
| Logs | Keys and reasons only (`LAB_SONUC:123 unknown-unit`), no clinical values |
| SQL | Bind variables for every value; schema/user names pass an identifier allow-list |
| Container | Chiseled .NET runtime, non-root user, no shell |
| Secrets | None in the repository. Connection strings come from environment variables; `.env.example` holds demo-only values |
| Data | Everything in the repository is synthetic |
