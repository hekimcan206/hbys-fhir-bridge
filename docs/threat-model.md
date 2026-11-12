# Threat model

hbys-fhir-bridge reads clinical data from a hospital database and writes it to a FHIR server. Everything in this
repository is synthetic, but the design assumes it will one day carry real special-category health data under KVKK
(Kişisel Verilerin Korunması Kanunu, Turkey's personal data protection law, Law No. 6698), where health data is a
"special category" with stricter processing rules.

## Assets

| Asset | Where | Why it matters |
|---|---|---|
| Patient identity (name, DOSYA_NO, TC Kimlik No, phone, address) | HBYS tables, FHIR `Patient` | Direct identifiers; the national id is the highest-value identifier in Turkey |
| Clinical facts (visits, lab results) | HBYS tables, FHIR `Encounter`/`Observation` | Special-category health data |
| Integrity of the feed | journal, checkpoint, FHIR server | A lost or stale result is a patient-safety problem, not only a data problem |
| Bridge credentials | environment of the bridge process | Read access to all clinical tables |
| Parking table details | `BRIDGE_PARKED.DETAIL` | Validator and server messages can quote field values |

## Actors

- **Bridge operator**: runs and monitors the process; needs `/status`, not patient data.
- **Data steward**: fixes parked rows in the HBYS.
- **HBYS application**: the only writer of clinical data.
- **FHIR consumers**: read from the FHIR server (out of scope here: their access control belongs to that server).
- **Attacker with network access** to the bridge host, the database or the FHIR endpoint.
- **Compromised bridge host**: an attacker who obtains the bridge's credentials.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation in this repo | Residual risk / next step |
|---|---|---|---|
| S1 | Spoofed FHIR server receives the feed | Base URL from configuration only; TLS is supported by `HttpClient` | Demo uses plain HTTP on a private Docker network; production must use HTTPS and, ideally, mutual TLS or OAuth2 client credentials (SMART Backend Services) |
| S2 | Stolen bridge credentials used to read clinical data | Bridge login is read-only on clinical tables; credentials come from environment variables, never files; `.env` is git-ignored | Use a vault-injected secret and Oracle proxy authentication or a wallet in production |
| T1 | Bridge (or someone using its login) modifies clinical data | Least privilege: `SELECT, FLASHBACK` on clinical tables, `DELETE` only on the journal. Integration test proves `UPDATE`/`DELETE` on clinical tables and `INSERT` into the journal fail (ORA-41900/ORA-01031) | A DBA can still do anything; database auditing (Unified Audit) is the control there |
| T2 | Forged or replayed journal entries cause wrong data on the server | The bridge cannot insert journal entries; an entry only names a row, the content always comes from the row itself `AS OF` the batch SCN, so a replayed entry re-sends the current truth | - |
| T3 | Lost or skipped changes (silent integrity failure) | Journal written in the same transaction as the change; read and consumed at one SCN; no watermark (ADR 0002). Measured under concurrent writers; property test of the engine under failures | Triggers can be disabled by a DBA; monitor `PendingChanges` and trigger status |
| T4 | Wrong clinical meaning (unit conversion, unmapped codes) | Units are recognised, never converted; unknown codes/units are parked (ADR 0001); profiles enforce LOINC and UCUM | Catalogue maintenance is a clinical governance task |
| R1 | No trace of what was sent when | Structured JSON logs per batch (SCN position, counts), OpenTelemetry spans per batch and per HTTP request; the FHIR server keeps version history | Logs deliberately contain keys (`LAB_SONUC:123`), not clinical values |
| I1 | National id exported unnecessarily | `IncludeNationalId` is **off by default** (KVKK data minimisation); enable only when the receiving system needs cross-institution matching | The identifier system URI is a placeholder until a deployment fixes it |
| I2 | Patient data in logs | The engine logs keys and reasons; validator messages are FHIRPath locations and invariant ids | `BRIDGE_PARKED.DETAIL` can contain server diagnostics that quote values; restrict access to that table to the data-steward role |
| I3 | Enumeration of resources by guessable ids | Logical ids are UUIDv5 of an internal key, not the HBYS sequence | UUIDv5 is deterministic: anyone who knows the namespace and a primary key can compute the id. Ids are not secrets; the FHIR server's authorization is the control |
| D1 | A malformed row blocks the feed | Parking + bisection; the stream never stops for one row | A flood of bad rows costs extra round trips (bounded, ADR 0003) |
| D2 | FHIR server outage | Transient errors roll the batch back; exponential back-off up to 30 s; nothing acknowledged until delivered | The journal grows during an outage; alert on `PendingChanges` and `OldestPendingAt` |
| D3 | Two bridge instances race | Pipeline lease: `SELECT ... FOR UPDATE NOWAIT` on the checkpoint row; the second instance backs off (integration test) | - |
| E1 | Container escape / privilege on the host | Chiseled runtime image, non-root user (UID 1654), no shell; CI asserts non-root | - |
| E2 | SQL injection through configuration | Identifiers (schema, user) pass an allow-list before being spliced into SQL; all values are bind variables | - |

## Data protection notes

- All data in this repository is generated (`SyntheticHospital`, Bogus `tr` locale). National ids are random digits
  with a valid checksum, not derived from any person.
- The bridge stores no clinical data of its own: the journal holds only table names, ids and timestamps; parking
  holds keys and messages.
- Retention: consumed journal entries are deleted in the same transaction that acknowledges them.
