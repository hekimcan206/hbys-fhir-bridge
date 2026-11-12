# 0002: Change capture with a trigger journal, read AS OF one SCN and consumed like a queue

- Status: accepted
- Date: 2026-11-12

## Context

The bridge has to move every committed change in three Oracle tables to the FHIR server, incrementally, including
deletes, without ever skipping one. It runs as a separate login with no right to change clinical data. Most HBYS
integrations do this with a "last updated" column (`WHERE guncelleme_zamani > :last_run`). That approach has two
structural defects:

1. **Late commits are skipped for good.** The stamp is taken when the statement runs; the row becomes visible when the
   transaction commits. Transaction A stamps a row at t1 and keeps working; B stamps another row at t2 > t1 and
   commits; the bridge reads B's row and moves its watermark to t2; A commits. A's row has t1 < watermark and is
   never read.
2. **Deletes are invisible.** A deleted row leaves nothing for the query to return.

Both are reproduced against real Oracle in `A_long_transaction_is_lost_by_the_timestamp_strategy_but_not_by_the_journal`
and measured under concurrent writers in docs/benchmarks.md.

## Options considered

1. *Timestamp watermark* (the baseline above). Kept in the repo only as `TimestampWatermarkSource`, to measure it.
2. *Watermark on a journal identity / sequence.* Same defect as 1: identity values are assigned at insert, not at
   commit, so a high-water cursor skips late-committing transactions.
3. *Watermark on `ORA_ROWSCN` of a `ROWDEPENDENCIES` journal.* Commit-ordered, non-destructive, supports several
   consumers. But Oracle documents `ORA_ROWSCN` as a *conservative upper bound* of the commit SCN; if a value reported
   above the batch SCN is later reported exactly (below it), a strict watermark skips the row. Rare, but "rare" is the
   wrong word for a clinical feed, and `ROWDEPENDENCIES` can only be chosen at `CREATE TABLE` time.
4. *Log-based CDC* (LogMiner via Debezium, GoldenGate, XStream). No triggers on the write path and full fidelity,
   but it needs supplemental logging and LogMiner privileges on a hospital database, a Kafka/Debezium estate or a
   GoldenGate licence. Out of scope for a bridge that should install with one schema script; the sibling
   `cdc-pipeline` project covers LogMiner.
5. *Trigger journal consumed like a queue* (chosen). Row-level triggers write `(table, id, I/U/D)` into
   `DEGISIKLIK_GUNLUGU` in the same transaction as the change. Each batch:
   1. locks its row in `BRIDGE_CHECKPOINT` (`FOR UPDATE NOWAIT`, the single-writer lease);
   2. reads `DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER` = S;
   3. reads up to N journal entries **`AS OF SCN S`** and the current state of the changed rows **`AS OF SCN S`**;
   4. delivers, then deletes exactly the journal ids it read, records parking and moves the checkpoint, in one commit.

## Why reading both AS OF the same SCN matters

If the journal were read "now" and the rows `AS OF S` (or the other way round), an entry committed between the two
reads would be consumed while the row state predates it: a lost update. Reading both at S means every consumed entry
describes a change that is already visible in the state that was sent. An entry that commits after S is invisible to
this batch and still in the table for the next one. There is no watermark to get wrong.

## Decision

Trigger journal, flashback reads at one SCN, delete-on-acknowledge. The bridge login gets `SELECT, FLASHBACK` on the
three tables, `SELECT, FLASHBACK, DELETE` on the journal, nothing else (tested: `UPDATE`/`DELETE` on clinical tables
and `INSERT` into the journal fail with ORA-41900/ORA-01031).

## Consequences

- Correctness does not depend on clocks, sequences or commit order. Under 4 writers holding transactions open for up
  to 50/200 ms the journal had 0 stale and 0 missing rows in every run; the timestamp baseline did not (benchmarks).
- Every write in the HBYS pays one extra insert. Measured worst case (one set-based 100,000-row insert): 1.78 s
  without the trigger, 5.94 s with it, about 41 µs extra per row (docs/benchmarks.md, section 3). A production rollout
  must measure it on the real workload.
- Deletes arrive as journal entries whose row no longer exists and become FHIR `DELETE`s.
- The initial load is just the first drain: the seed (or any bulk insert) fills the journal through the triggers. For
  an existing database the journal can be primed with one `INSERT ... SELECT` per table by the schema owner.
- `AS OF SCN` needs undo for the batch duration only (seconds), not for the bridge's downtime: a bridge that was down
  for a day reads its backlog at the current SCN.
- One consumer per pipeline by construction (the lease). A second instance gets `PipelineBusyException` and backs off.

## Sources

- Oracle, `ORA_ROWSCN` pseudocolumn (conservative upper bound): https://docs.oracle.com/cd/B28359_01/server.111/b28286/pseudocolumns007.htm
- Oracle, flashback query `AS OF SCN`: https://www.dbs.ifi.lmu.de/oradoc10g/DOC/appdev.101/b10795/adfns_fl.htm
- Query-based vs log-based CDC trade-offs (deletes invisible to polling): https://www.conduktor.io/glossary/log-based-vs-query-based-cdc-comparison
