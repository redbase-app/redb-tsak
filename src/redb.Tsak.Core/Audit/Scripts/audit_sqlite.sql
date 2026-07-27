-- Tsak admin audit log — SQLite
-- Auto-applied by AuditSchemaInitializer on startup when a redb provider is configured.
-- Idempotent: every statement is guarded by IF NOT EXISTS.
--
-- Timestamps are TEXT in ISO-8601 with offset ("2026-07-24T10:15:00.1234567+00:00").
-- This table is Tsak's own — not part of the redb schema — so it does NOT follow redb's
-- REAL Julian-day convention: lexicographic ordering equals chronological ordering, the
-- retention sweep compares strings, and the value is readable in any SQLite browser.

CREATE TABLE IF NOT EXISTS tsak_audit_log (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id          TEXT NOT NULL,
    ts                TEXT NOT NULL,
    action            TEXT NOT NULL,
    controller_type   TEXT,
    actor_principal   TEXT,
    actor_key_id      TEXT,
    remote_ip         TEXT,
    user_agent        TEXT,
    http_method       TEXT,
    request_path      TEXT,
    target_resource   TEXT,
    status_code       INTEGER NOT NULL,
    duration_ms       REAL NOT NULL,
    exception_type    TEXT,
    exception_message TEXT,
    payload           TEXT,
    CONSTRAINT uq_tsak_audit_event_id UNIQUE (event_id)
);

CREATE INDEX IF NOT EXISTS ix_tsak_audit_ts     ON tsak_audit_log (ts DESC);
CREATE INDEX IF NOT EXISTS ix_tsak_audit_actor  ON tsak_audit_log (actor_key_id);
CREATE INDEX IF NOT EXISTS ix_tsak_audit_action ON tsak_audit_log (action);
