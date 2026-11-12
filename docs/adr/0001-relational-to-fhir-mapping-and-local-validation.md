# 0001: Pure row-to-resource mapping, validated locally with the Firely SDK against project profiles

- Status: accepted
- Date: 2026-11-12

## Context

The source is a relational HBYS (Hastane Bilgi Yönetim Sistemi, hospital information system) schema with Turkish
names and local codes: `HASTA` (patient), `BASVURU` (visit/admission), `LAB_SONUC` (laboratory result, local
`TETKIK_KODU` test codes, free-text `BIRIM` units). The target is HL7 FHIR R4: `Patient`, `Encounter`, `Observation`.
Two things make the mapping hard rather than tedious:

1. **Local codes and units.** A lab result is only useful downstream when it is LOINC-coded with a UCUM unit. The
   source writes `mg/dl`, `mg/dL`, `mEq/L`, `x10^3/uL` for the same concepts, and sometimes something nobody can map.
2. **Data quality.** Real HBYS data has finished visits without an end time, deaths recorded before births, results
   with unknown units. A bridge that forwards them produces resources that are syntactically valid JSON but wrong.

The FHIR server is not a reliable gate. On Blaze 1.12 a transaction with `Patient.gender = "bogus"` was accepted with
`201 Created` (measured while building this project; Blaze validates structure and references, not code bindings).

## Options considered

**Mapping**

1. *FHIR Mapping Language / StructureMap or a template engine (Liquid).* Declarative and standard, but the hard parts
   (unit recognition, status vocabularies, interpretation flags, deterministic ids) end up as custom functions anyway,
   and it adds a runtime and a second language to test.
2. *A pure C# mapper over typed rows* (chosen). `FhirMapper` takes a row record and returns a resource or a
   `MappingResult.Fail(reason)`. No I/O, no clock: the same row always produces byte-identical JSON, which is what the
   golden tests and idempotent re-sends rely on.

**Codes and units**

1. *Convert units* (mmol/L glucose to mg/dL and so on). Conversion is clinical logic with its own failure modes; a
   silent factor-18 error is worse than a parked row.
2. *Recognise, never convert* (chosen). `LabCatalog` is a ConceptMap in code: local code to LOINC, UCUM unit, and the
   list of local spellings that mean exactly that unit. An unknown spelling or code fails with `unknown-unit` /
   `unmapped-code` and the row is parked for a human.

**Validation**

1. *Trust the server.* Rejected: see the Blaze observation above.
2. *A remote validator* (HL7 Java validator, a terminology server). Accurate, but a network dependency on the hot path,
   and the Java validator image would be a second large JVM on this machine.
3. *Firely `Firely.Fhir.Validation` in-process against the core R4 definitions plus three project profiles*
   (chosen). Profiles are differentials (`Profiles/*.json`) with FHIRPath invariants for the rules that matter here:
   `hbys-pat-2` death not before birth, `hbys-enc-1/2` finished visits have a sane period, `hbys-obs-1..4` LOINC code,
   laboratory category, value or `dataAbsentReason` on released results, UCUM quantities. `LocalTerminologyService`
   checks required bindings to core value sets offline.

## Decision

Map with a pure, deterministic C# mapper; recognise but never convert units; validate every resource locally with the
Firely validator and park failures with the validator's message. LOINC itself is not shipped with the core package,
so LOINC correctness comes from the curated catalogue plus invariant `hbys-obs-1`, not from a terminology server.

## Consequences

- Golden files under `tests/HbysFhirBridge.UnitTests/Golden/` make every mapping change a reviewable JSON diff.
- The first validation per resource type compiles the profile snapshot (seconds); after that a validation costs about
  a millisecond (docs/benchmarks.md). The worker warms the validator before it reports ready.
- The Firely snapshot generator is not thread-safe on first use: parallel unit tests failed with
  `Invalid snapshot generator state (OnStartRecursion)`. `ResourceValidator` serialises calls; the sync loop is
  single-threaded, so this costs nothing in production.
- Turkey has used a fixed UTC+3 since 2016, so `TIMESTAMP` columns without a zone are mapped with a fixed offset
  (configurable), avoiding a tzdata dependency in the chiseled image.

## Sources

- Firely .NET SDK, profile validation: https://docs.fire.ly/projects/Firely-NET-SDK/en/latest/validation/profile-validation.html
- Firely .NET SDK, terminology services: https://docs.fire.ly/projects/Firely-NET-SDK/en/latest/validation/terminology-service.html
- FHIR R4 `Observation` and `observation-category`: https://hl7.org/fhir/R4/observation.html
