-- Tsak dead-letter store — SQLite
-- Auto-applied by DlqSchemaInitializer on startup. Idempotent.
-- Timestamps are TEXT ISO-8601 (Tsak's own table, not the redb REAL-Julian convention) —
-- lexicographic ordering equals chronological ordering.

CREATE TABLE IF NOT EXISTS tsak_dlq (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id          TEXT NOT NULL,
    occurred_at       TEXT NOT NULL,
    context_name      TEXT NOT NULL,
    route_id          TEXT NOT NULL,
    marker_name       TEXT NOT NULL,
    status            TEXT NOT NULL,
    exception_type    TEXT,
    exception_message TEXT,
    correlation_id    TEXT,
    body_kind         TEXT NOT NULL,
    body_type         TEXT,
    body_data         TEXT,
    headers_json      TEXT,
    properties_json   TEXT,
    replayable        INTEGER NOT NULL,
    replayed_at       TEXT,
    CONSTRAINT uq_tsak_dlq_entry_id UNIQUE (entry_id)
);

CREATE INDEX IF NOT EXISTS ix_tsak_dlq_occurred ON tsak_dlq (occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_tsak_dlq_status   ON tsak_dlq (status);
CREATE INDEX IF NOT EXISTS ix_tsak_dlq_route    ON tsak_dlq (route_id);
