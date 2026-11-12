-- Run as HBYS_BRIDGE. The bridge keeps its state next to the source so that a checkpoint and the data it describes
-- are backed up together.

-- One row per pipeline. Locked FOR UPDATE NOWAIT for the length of a batch, which makes it the single-writer lease:
-- a second bridge instance cannot consume the journal concurrently. POSITION is the SCN of the last batch (for
-- operators) or, for the naive comparison pipeline, its timestamp watermark.
CREATE TABLE bridge_checkpoint (
    pipeline    VARCHAR2(40)  CONSTRAINT pk_bridge_checkpoint PRIMARY KEY,
    position    VARCHAR2(128),
    batches     NUMBER(12)    DEFAULT 0 NOT NULL,
    updated_at  TIMESTAMP(6)  DEFAULT SYSTIMESTAMP NOT NULL
)
/
-- Source rows that could not be delivered. Parked rows do not block the stream. A later change to the row retries
-- it; a row parked because its parent is parked (BLOCKED_*) is retried as soon as the parent is delivered; a
-- successful delivery removes the entry.
CREATE TABLE bridge_parked (
    resource_type  VARCHAR2(20)   NOT NULL,
    source_id      NUMBER(12)     NOT NULL,
    reason         VARCHAR2(20)   NOT NULL,
    detail         VARCHAR2(4000) NOT NULL,
    blocked_type   VARCHAR2(20),
    blocked_id     NUMBER(12),
    first_parked   TIMESTAMP(6)   DEFAULT SYSTIMESTAMP NOT NULL,
    last_parked    TIMESTAMP(6)   DEFAULT SYSTIMESTAMP NOT NULL,
    attempts       NUMBER(6)      DEFAULT 1 NOT NULL,
    CONSTRAINT pk_bridge_parked PRIMARY KEY (resource_type, source_id)
)
/
CREATE INDEX ix_bridge_parked_blocked ON bridge_parked (blocked_type, blocked_id)
/
