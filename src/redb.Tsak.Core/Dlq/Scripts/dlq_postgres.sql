-- Tsak dead-letter store — PostgreSQL
-- Auto-applied by DlqSchemaInitializer on startup when a redb provider is configured. Idempotent.

CREATE TABLE IF NOT EXISTS tsak_dlq (
    id                BIGSERIAL PRIMARY KEY,
    entry_id          UUID NOT NULL,
    occurred_at       TIMESTAMPTZ NOT NULL,
    context_name      VARCHAR(200) NOT NULL,
    route_id          VARCHAR(200) NOT NULL,
    marker_name       VARCHAR(200) NOT NULL,
    status            VARCHAR(20) NOT NULL,          -- pending | replayed | discarded
    exception_type    VARCHAR(200),
    exception_message VARCHAR(2000),
    correlation_id    VARCHAR(200),
    body_kind         VARCHAR(20) NOT NULL,          -- bytes | string | json | none
    body_type         VARCHAR(400),                  -- CLR type name for body_kind=json
    body_data         TEXT,                          -- base64 (bytes) / literal (string) / json
    headers_json      TEXT,
    properties_json   TEXT,
    replayable        BOOLEAN NOT NULL,
    replayed_at       TIMESTAMPTZ,
    CONSTRAINT uq_tsak_dlq_entry_id UNIQUE (entry_id)
);

CREATE INDEX IF NOT EXISTS ix_tsak_dlq_occurred ON tsak_dlq (occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_tsak_dlq_status   ON tsak_dlq (status);
CREATE INDEX IF NOT EXISTS ix_tsak_dlq_route    ON tsak_dlq (route_id);
