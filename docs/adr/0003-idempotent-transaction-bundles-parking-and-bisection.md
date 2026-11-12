# 0003: Idempotent transaction bundles, deterministic ids, parking with dependencies, bisection

- Status: accepted
- Date: 2026-11-12

## Context

Delivery happens over HTTP to a FHIR server that may time out after committing, restart, or reject content. The
source side (ADR 0002) is at-least-once: a crash between "server accepted" and "journal consumed" re-delivers the
batch. The target has referential integrity: Blaze answers `409 Conflict` to an `Encounter` whose `Patient` does not
exist, and to deleting a `Patient` an `Encounter` still references. And some rows will never be deliverable until a
human fixes them.

## Options considered

**Ids and verbs**

1. *`POST` with server-assigned ids, plus a lookup table source-id to server-id.* Every retry risks a duplicate; the
   lookup table must be updated atomically with the server, which it cannot be.
2. *Conditional create/update on a business identifier* (`PUT Patient?identifier=...`). Idempotent, but each entry
   costs a search on the server, and conditional semantics vary between servers.
3. *`PUT [type]/[id]` and `DELETE [type]/[id]` with deterministic ids* (chosen). The id is a name-based UUID
   (RFC 9562 version 5) of `TABLE:primary key` in a fixed project namespace. Internal sequence numbers never appear in
   URLs, and the same row always has the same URL, so every entry is idempotent.

**Unit of delivery**

1. *One request per resource.* Simple, but thousands of round trips for an initial load.
2. *`batch` bundles.* Entries succeed or fail independently, but a child can be stored before its parent is, and
   references between entries are not resolved by all servers.
3. *`transaction` bundles* (chosen), up to 250 entries. Atomic, so a batch is either fully on the server or not.

**When something is rejected**

A transaction fails as a whole and the answer does not reliably say which entry was at fault.

1. *Fail the batch and stop.* One bad row blocks the hospital's whole feed.
2. *Fall back to one request per entry.* Finds the culprit with n round trips.
3. *Bisect* (chosen): split the run in half, send each half, recurse into halves that fail. One bad entry among n
   costs at most 2·⌈log2 n⌉ extra requests. The ordering makes halves safe to send one after another: deletes first,
   children before parents; then upserts, parents before children.

**Rows that cannot be delivered**

Parked rows go to `BRIDGE_PARKED` with a reason (`invalid`, `unmapped-code`, `unknown-unit`, `invalid-source`,
`rejected`, `dependency`) and are consumed from the journal, so they never block the stream. A later change to the
row retries it. A child whose parent is parked is parked as `dependency` with `BLOCKED_TYPE/BLOCKED_ID`; when that
parent is delivered, the engine immediately opens an "unblocked" batch for its waiting children (and their children).

Children of a parked parent are **held back without sending** instead of being discovered through 409s. Without that,
at a 2% dirty rate every broken patient sends its encounters and results into bisection. Measured on the initial load
of the demo dataset: see docs/benchmarks.md ("hold children" on vs off).

## Decision

Transaction bundles of idempotent PUT/DELETE entries with UUIDv5 ids; local validation before sending; bisection for
server rejections; parking with explicit dependencies, children held back while their parent is parked, and released
in the same run when the parent is delivered.

## Consequences

- Replays are free on the server: Blaze does not create a version for an identical PUT, so re-delivering a whole
  database wrote **0** new versions (docs/benchmarks.md; also an integration test). A crash anywhere is safe.
- The HTTP client may retry the POST of a transaction (Microsoft.Extensions.Http.Resilience standard handler) because
  every entry is idempotent.
- A held child is delayed until its parent is fixed even when an older valid version of the parent is already on the
  server. That is conservative on purpose (a result never overtakes a broken patient record) and visible in
  `/status`; README Limitations discusses it.
- Changing the UUID namespace or the name format re-keys every resource; a unit test pins both.

## Sources

- FHIR R4 RESTful API, transaction processing rules: https://hl7.org/fhir/R4/http.html#transaction
- RFC 9562 (UUID version 5): https://www.rfc-editor.org/rfc/rfc9562
- Blaze referential integrity (`ENFORCE_REFERENTIAL_INTEGRITY`, deletes blocked while referenced): https://samply.github.io/blaze/deployment/environment-variables.html
