# 0004: Blaze as the FHIR server for integration tests and the demo

- Status: accepted
- Date: 2026-11-12

## Context

Integration tests and `docker compose up` need a real FHIR R4 server that supports transaction bundles, `PUT` with
client ids, `DELETE`, history and `_summary=count`. The development machine is shared and memory-constrained (a
7.6 GB Docker VM running Oracle Database Free and other projects), and the CI runner has 16 GB.

## Options considered

1. *HAPI FHIR JPA server.* The reference open-source server, but a large Spring Boot image whose default setup wants
   well over a gigabyte of heap plus a database.
2. *Firely Server.* Commercial licence for anything beyond evaluation.
3. *An in-repo fake.* Fast, but it would test the bridge against my own reading of the spec. Used only for unit tests
   (`FakeFhirServer`), where its behaviour is pinned to what Blaze actually did.
4. *Blaze 1.12* (chosen). Open source (Apache-2.0), single container with embedded RocksDB, starts in about 12 s,
   runs with `-Xmx512m` in tests and `-Xmx768m` in the demo. It enforces referential integrity in transactions, which
   is exactly the behaviour the bridge's ordering and parking logic must cope with.

## Behaviour observed on Blaze 1.12 (and pinned in `FakeFhirServer`)

- Entry order inside a transaction does not matter (an `Encounter` before its `Patient` succeeds).
- A reference to a missing resource is `409 Conflict` with an OperationOutcome; deleting a referenced `Patient` alone
  is `409`, deleting it together with its referencing `Encounter` in one transaction is `200`.
- An identical `PUT` creates no new version; `DELETE` of a missing resource succeeds.
- Code bindings are not validated (`gender: "bogus"` accepted), see ADR 0001.
- `POST [base]/` (trailing slash) is answered with `308` to `[base]`. .NET's `HttpClient` followed the redirect for the
  first request and then failed every later one on the same handler with "the response ended prematurely". The sink
  now posts to the base URL without the slash; a unit test pins the request URI.

## Decision

Blaze `samply/blaze:1.12.0` (pinned) in Testcontainers and in docker compose. The bridge itself only speaks the FHIR
REST API, so any R4 server with transaction support can be the production target.

## Consequences

- One more JVM in CI (about 1 GB image); the integration job takes a couple of minutes including Oracle start-up.
- Tests share one Blaze and one Oracle per run and leave both empty by deleting every source row and letting the
  bridge propagate the deletes, which exercises the delete path on every test.

## Sources

- Blaze environment variables: https://samply.github.io/blaze/deployment/environment-variables.html
- Blaze data sync guide (referential integrity and ordering): https://samply.github.io/blaze/data-sync.html
