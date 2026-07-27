-- Tsak admin audit log — PostgreSQL
-- Auto-applied by AuditSchemaInitializer on startup when a redb provider is configured.
-- Idempotent: every statement is guarded by IF NOT EXISTS.

CREATE TABLE IF NOT EXISTS tsak_audit_log (
    id                BIGSERIAL PRIMARY KEY,
    event_id          UUID NOT NULL,
    ts                TIMESTAMPTZ NOT NULL,
    action            VARCHAR(200) NOT NULL,
    controller_type   VARCHAR(200),
    actor_principal   VARCHAR(200),
    actor_key_id      VARCHAR(100),
    remote_ip         VARCHAR(64),
    user_agent        VARCHAR(500),
    http_method       VARCHAR(16),
    request_path      VARCHAR(500),
    target_resource   VARCHAR(300),
    status_code       INTEGER NOT NULL,
    duration_ms       DOUBLE PRECISION NOT NULL,
    exception_type    VARCHAR(200),
    exception_message VARCHAR(2000),
    payload           JSONB,
    CONSTRAINT uq_tsak_audit_event_id UNIQUE (event_id)
);

-- ts DESC: every query and the retention sweep walk this index.
CREATE INDEX IF NOT EXISTS ix_tsak_audit_ts     ON tsak_audit_log (ts DESC);
CREATE INDEX IF NOT EXISTS ix_tsak_audit_actor  ON tsak_audit_log (actor_key_id);
CREATE INDEX IF NOT EXISTS ix_tsak_audit_action ON tsak_audit_log (action);
