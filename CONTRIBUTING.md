# Contributing

Thanks for taking the time to improve hbys-fhir-bridge.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start Oracle Database 23ai Free and Blaze through Testcontainers; `docker compose up`
  runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                                              # must be warning-free: warnings are errors
   dotnet test --project tests/HbysFhirBridge.UnitTests      # unit, property and golden tests (seconds)
   dotnet test --project tests/HbysFhirBridge.IntegrationTests  # Oracle + Blaze (Docker running, ~2 min)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable and make
the hand-written CHANGELOG easy to assemble at release time:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing the mapping

The FHIR output is a contract with every consumer of the server.

1. Add or update a case in `tests/HbysFhirBridge.UnitTests/Mapping/MappingGoldenTests.cs`.
2. Regenerate the golden files with `UPDATE_GOLDEN=1 dotnet test --project tests/HbysFhirBridge.UnitTests`, then
   review the JSON diff under `Golden/` like code.
3. A new local test code or unit spelling goes into `LabCatalog` only with its LOINC code and UCUM unit. Units are
   recognised, never converted (ADR 0001).
4. Never change the UUID namespace or the `TABLE:id` name format in `ResourceIds`: it would re-key every resource.

## Changing a profile

Profiles live in `src/HbysFhirBridge.Core/Profiles/` as differentials. Every new invariant needs a failing-case test in
`ResourceValidatorTests` and must keep the clean synthetic dataset valid.

## Schema scripts

`src/HbysFhirBridge.Oracle/Sql/` holds the synthetic HBYS schema and the bridge tables. Statements end with a line
holding a single `/`. The bridge login's grants are part of the security model: a change there needs a matching
integration test (`The_bridge_login_cannot_change_clinical_data_or_forge_journal_entries`).

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one (MADR: context, options
considered, decision, consequences). Do not edit the old ADR in place.
