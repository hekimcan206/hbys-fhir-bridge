# Benchmarks

Every number here was measured on the machine below on 2026-11-12. Nothing is estimated. Where runs were repeated,
the median is reported and the individual runs are listed. The raw console output is reproduced as printed (the e2e
runs predate the invariant-culture fix in the harness, so the original printed decimal commas; they are shown with
points here).

## Environment

| | |
|---|---|
| Host | Intel Core i7-13620H (10 cores / 16 threads), Windows 11 25H2 |
| Docker | Docker Desktop 29.9, Linux VM with 7.6 GiB RAM and 16 vCPUs, **shared** with 8 other running containers and another build agent during all runs |
| .NET | SDK 10.0.401, runtime 10.0.12, Release build |
| Oracle | `gvenzl/oracle-free:23-slim-faststart`, which reports *Oracle AI Database 26ai Free Release 23.26.3* (Free edition limits: 2 CPU threads, 2 GB SGA+PGA) |
| FHIR server | `samply/blaze:1.12.0`, `-Xmx768m`, fresh container (empty database) for every e2e run |
| Data | `SyntheticHospital` seed 20261112: 1,000 patients, 2,507 visits, 6,725 lab results = 10,232 rows; 228 deliberately dirty rows (dirty rate 2%), which with their dependants make 594 parked rows |

Host load matters: the machine was busy (other agent builds and containers), so absolute throughput varies run to run
(the initial load ranged 14.7–31.2 s with identical input). Ratios between configurations measured back to back are
more trustworthy than the absolute values.

## 1. Capture correctness under concurrent writers (the core claim)

Both change sources run side by side against the same Oracle and the same workload, each feeding an in-memory sink.
Four writers run `SourceWorkload` for 30 s (new and corrected lab results, finished visits, patient edits, deleted
results, new visits); each writer holds its transaction open for a random 0..*hold* ms after its change and before
`COMMIT`. Both sources poll every 200 ms. After the writers stop, both drain once more, and each sink is compared row
by row with the mapped current source state:

- **stale**: the sink holds an older version of a row than the source;
- **missing**: a row that exists in the source never reached the sink;
- **not deleted**: the sink still holds a row that was deleted in the source.

```
dotnet run -c Release --project benchmarks/HbysFhirBridge.Benchmarks -- capture --patients 300 --writers 4 --seconds 30 --max-hold-ms <hold> --poll-ms 200 --runs 3
```

| hold | run | rows changed | journal: stale / missing / not deleted | timestamp: stale / missing / not deleted |
|---|---|---|---|---|
| 0 ms | 1 | 12,142 | 0 / 0 / 0 | 0 / 4 / 1,140 |
| 0 ms | 2 | 20,050 | 0 / 0 / 0 | 0 / 0 / 1,593 |
| 0 ms | 3 | 16,370 | 0 / 0 / 0 | 0 / 0 / 1,378 |
| 0–50 ms | 1 | 2,803 | 0 / 0 / 0 | 20 / 35 / 341 |
| 0–50 ms | 2 | 2,718 | 0 / 0 / 0 | 23 / 43 / 320 |
| 0–50 ms | 3 | 2,726 | 0 / 0 / 0 | 26 / 48 / 297 |
| 0–200 ms | 1 | 904 | 0 / 0 / 0 | 21 / 34 / 117 |
| 0–200 ms | 2 | 843 | 0 / 0 / 0 | 23 / 31 / 107 |
| 0–200 ms | 3 | 896 | 0 / 0 / 0 | 19 / 34 / 103 |

Medians:

| hold | journal (stale / missing / not deleted) | timestamp (stale / missing / not deleted) |
|---|---|---|
| 0 ms | **0 / 0 / 0** | 0 / 0 / 1,378 |
| 0–50 ms | **0 / 0 / 0** | 23 / 43 / 320 |
| 0–200 ms | **0 / 0 / 0** | 21 / 34 / 107 |

Reading it:

- The journal source was exact in all 9 runs, 59,452 committed row changes in total.
- The timestamp source never sees deletes (every deleted row stays on the target), and as soon as transactions stay
  open for tens of milliseconds it also loses updates (stale) and inserts (missing) for good: the late-committing
  transaction's stamp is older than the watermark the poller already moved past. Even with no deliberate hold it lost
  4 inserts in one run.
- Writers are slower with longer holds (fewer rows changed), so the absolute loss counts are not comparable across
  rows; the zero column is.
- The deterministic version of this race is an integration test
  (`A_long_transaction_is_lost_by_the_timestamp_strategy_but_not_by_the_journal`).

## 2. Initial load, replay and live sync into Blaze

```
HFB_OWNER=... HFB_BRIDGE=... HFB_FHIR=http://127.0.0.1:18480/fhir/ \
  dotnet run -c Release --project benchmarks/HbysFhirBridge.Benchmarks -- e2e --patients 1000 --dirty-permille 20 --hold <true|false> --live-seconds <60|0>
```

Each run: empty Oracle schema, fresh Blaze, seed 10,232 rows (array binding, capture triggers on), drain the journal
into Blaze (batch 500, bundles of up to 250 entries), then fire a no-op `UPDATE` on every row (the triggers journal
all 10,232 rows again) and drain again.

**Initial load (10,232 rows, 9,638 delivered, 594 parked; parked counts identical in every run):**

| children of parked parents | bundles sent | run 1 | run 2 | run 3 | **median** | rows/s (median) |
|---|---|---|---|---|---|---|
| held back (default) | 41 | 18.9 s | 14.7 s | 31.2 s | **18.9 s** | **541** |
| sent, discovered through 409 + bisection | 1,721 | 40.9 s | 38.1 s | 37.0 s | **38.1 s** | **269** |

With 2% dirty rows, holding children back cuts the requests by 42x (41 vs 1,721 bundles) and halves the load time.
Without it, each broken patient's visits and results are only found by the server rejecting them, and every rejection
costs a bisection.

**Replay of every row (no-op updates, same ids, identical content):**

| children of parked parents | run 1 | run 2 | run 3 | **median** | new versions written on Blaze |
|---|---|---|---|---|---|
| held back | 13.5 s | 11.2 s | 19.8 s | **13.5 s** (759 rows/s) | **0** (history 9,638 → 9,638, all 6 runs) |
| sent | 38.3 s | 36.6 s | 41.0 s | **38.3 s** (267 rows/s) | **0** |

The replay is the crash-recovery path at full scale: re-sending everything is safe and leaves the server unchanged.

**Live sync** (held-back mode, 1 writer at 20 operations/s for 60 s, bridge polling every 500 ms; lag = journal
commit of the oldest change in a batch to that batch's acknowledgement, i.e. an upper bound for the batch):

| run | rows delivered | batches | lag p50 | lag p95 | lag max |
|---|---|---|---|---|---|
| 1 | 940 | 244 | 0.09 s | 0.58 s | 0.71 s |
| 2 | 939 | 189 | 0.47 s | 0.56 s | 0.58 s |
| 3 | 939 | 196 | 0.45 s | 0.56 s | 0.61 s |
| **median** | | | **0.45 s** | **0.56 s** | **0.61 s** |

The lag is dominated by the 500 ms poll interval. Journal times come from the database clock and acknowledgement times
from the host clock (Docker VM vs Windows); their skew is not corrected.

**Seed throughput** (10,232 rows in one transaction with array binding, capture triggers on): 0.6–1.7 s across the
six runs, median 0.75 s (≈13,400 rows/s).

## 3. Cost of the capture triggers on the write path

Set-based worst case, measured in SQL*Plus inside the Oracle container: `INSERT INTO lab_sonuc ... SELECT ... CONNECT
BY level <= 100000` (one statement, 100,000 rows), three times with the journal trigger enabled and three times with
it disabled.

| journal trigger | run 1 | run 2 | run 3 | median | per row |
|---|---|---|---|---|---|
| enabled | 5.81 s | 7.64 s | 5.94 s | **5.94 s** | 59 µs |
| disabled | 1.42 s | 1.78 s | 4.53 s | **1.78 s** | 18 µs |

A row-level trigger roughly triples the cost of a bulk insert (about 41 µs extra per row here). For OLTP writes of a
few rows per transaction this is tens of microseconds per row; for nightly bulk loads it is material, and a
production rollout must measure it on the real workload (README, Limitations).

## 4. Micro benchmarks (BenchmarkDotNet 0.15.8)

```
dotnet run -c Release --project benchmarks/HbysFhirBridge.Benchmarks -- micro --filter '*'
```

| Method | Mean | StdDev | Median | Allocated |
|---|---:|---:|---:|---:|
| MapObservation | 2,097 ns | 281 ns | 2,035 ns | 5,888 B |
| MapPatient | 1,611 ns | 292 ns | 1,701 ns | 3,560 B |
| ValidateObservation | 1,049 µs | 68 µs | 1,033 µs | 770,548 B |
| ValidatePatient (incl. mapping) | 855 µs | 191 µs | 855 µs | 545,956 B |
| BuildAndSerializeBundleOf250 | 3.03 ms | 0.46 ms | 3.07 ms | 3,007,166 B |
| ResourceId (UUIDv5) | 323 ns | 58 ns | 303 ns | 336 B |

BenchmarkDotNet flagged multimodal distributions for `MapObservation` and `ValidatePatient` (host noise).

What it says: validation is about 500x more expensive than mapping and allocates ~0.5–0.8 MB per resource. At about
1 ms per resource on one core it caps a single bridge at roughly 1,000 resources/s before any I/O, which is above the
541 rows/s measured end to end, so Blaze and Oracle round trips still dominate on this machine. The first validation
of each resource type compiles its profile and takes seconds (9 s for the first Encounter in an early spike on this
machine), which is why the worker warms the validator before it reports ready.

## How to reproduce

1. Start Oracle with the init script: `docker compose up -d oracle` (or a `gvenzl/oracle-free` container mounting
   `deploy/oracle/init`), then `dotnet run --project src/HbysFhirBridge.Worker -- install` with
   `Bridge__OwnerConnectionString` and `Bridge__ConnectionString` set.
2. For e2e, start a fresh `samply/blaze:1.12.0` per run.
3. Run the commands above with `HFB_OWNER`, `HFB_BRIDGE` (and `HFB_FHIR`) set to the owner and bridge connection
   strings.
