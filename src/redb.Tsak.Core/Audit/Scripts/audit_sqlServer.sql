-- Tsak admin audit log — SQL Server / Azure SQL
-- Auto-applied by AuditSchemaInitializer on startup when a redb provider is configured.
-- Idempotent: each CREATE is guarded by an OBJECT_ID / sys.indexes check.

IF OBJECT_ID(N'[dbo].[tsak_audit_log]', N'U') IS NULL
CREATE TABLE [dbo].[tsak_audit_log] (
    [id]                bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [event_id]          uniqueidentifier NOT NULL,
    [ts]                datetimeoffset NOT NULL,
    [action]            nvarchar(200) NOT NULL,
    [controller_type]   nvarchar(200) NULL,
    [actor_principal]   nvarchar(200) NULL,
    [actor_key_id]      nvarchar(100) NULL,
    [remote_ip]         nvarchar(64) NULL,
    [user_agent]        nvarchar(500) NULL,
    [http_method]       nvarchar(16) NULL,
    [request_path]      nvarchar(500) NULL,
    [target_resource]   nvarchar(300) NULL,
    [status_code]       int NOT NULL,
    [duration_ms]       float NOT NULL,
    [exception_type]    nvarchar(200) NULL,
    [exception_message] nvarchar(2000) NULL,
    -- No native JSON type before SQL Server 2025; nvarchar(max) is the documented carrier
    -- and JSON_VALUE / OPENJSON read it directly.
    [payload]           nvarchar(max) NULL,
    CONSTRAINT [uq_tsak_audit_event_id] UNIQUE ([event_id])
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsak_audit_ts' AND object_id = OBJECT_ID(N'[dbo].[tsak_audit_log]'))
CREATE INDEX [ix_tsak_audit_ts] ON [dbo].[tsak_audit_log] ([ts] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsak_audit_actor' AND object_id = OBJECT_ID(N'[dbo].[tsak_audit_log]'))
CREATE INDEX [ix_tsak_audit_actor] ON [dbo].[tsak_audit_log] ([actor_key_id]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsak_audit_action' AND object_id = OBJECT_ID(N'[dbo].[tsak_audit_log]'))
CREATE INDEX [ix_tsak_audit_action] ON [dbo].[tsak_audit_log] ([action]);
