# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-12

### Added

- Synthetic HBYS schema on Oracle Database 23ai Free (`HASTA`, `BASVURU`, `LAB_SONUC`) with row-level capture
  triggers writing a change journal (`DEGISIKLIK_GUNLUGU`) in the same transaction as each change.
- Journal change source: single-writer lease on the checkpoint row, journal and row states read `AS OF` one SCN,
  journal entries deleted in the acknowledging transaction. Deletes become FHIR `DELETE`s.
- Pure, deterministic HBYS-to-FHIR R4 mapping to `Patient`, `Encounter` and `Observation`, with a local test code to
  LOINC/UCUM catalogue that recognises unit spellings and never converts units.
- Local validation with the Firely validator against the core R4 definitions and three project profiles
  (`hbys-patient`, `hbys-encounter`, `hbys-lab-observation`) with FHIRPath invariants.
- Delivery as transaction bundles of idempotent `PUT`/`DELETE` entries with UUIDv5 ids; bisection to isolate
  server-rejected entries; parking table with reasons and parent dependencies; children held back while their
  parent is parked and released in the same run once it is delivered.
- Worker process with `/status`, `/health/live`, `/health/ready`, structured JSON logs and OpenTelemetry traces and
  metrics; `install`, `seed`, `workload` and `verify` commands.
- National identity number export is opt-in (`Bridge:IncludeNationalId`), off by default for KVKK data minimisation.
- `TimestampWatermarkSource`, the common "last updated" integration, kept as a measured baseline.
- Synthetic data generator (Bogus `tr` locale, checksum-valid random national ids, controlled dirty-row rate).
- Unit, FsCheck property and golden tests; Oracle + Blaze integration tests with Testcontainers; BenchmarkDotNet
  micro benchmarks and end-to-end capture and load experiments (docs/benchmarks.md).
- `docker compose up` demo (Oracle, Blaze, seed, simulated workload, bridge) and a CI job that verifies it.
- ADRs 0001–0004, threat model, CI with vulnerability audit, CodeQL.

### Fixed (during development, kept for the record)

- Posting transactions to `[base]/` made Blaze answer `308`; `HttpClient` then failed later requests on the same
  handler. The sink posts to `[base]` without the trailing slash.
- Concurrent first use of the Firely validator failed with `Invalid snapshot generator state`; validation is now
  serialised.

[1.0.0]: https://github.com/hekimcan206/hbys-fhir-bridge/releases/tag/v1.0.0
