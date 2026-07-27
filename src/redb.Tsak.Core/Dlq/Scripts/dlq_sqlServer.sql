-- Tsak dead-letter store — SQL Server / Azure SQL
-- Auto-applied by DlqSchemaInitializer on startup. Idempotent (OBJECT_ID / sys.indexes guards).

IF OBJECT_ID(N'[dbo].[tsak_dlq]', N'U') IS NULL
CREATE TABLE [dbo].[tsak_dlq] (
    [id]                bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [entry_id]          uniqueidentifier NOT NULL,
    [occurred_at]       datetimeoffset NOT NULL,
    [context_name]      nvarchar(200) NOT NULL,
    [route_id]          nvarchar(200) NOT NULL,
    [marker_name]       nvarchar(200) NOT NULL,
    [status]            nvarchar(20) NOT NULL,
    [exception_type]    nvarchar(200) NULL,
    [exception_message] nvarchar(2000) NULL,
    [correlation_id]    nvarchar(200) NULL,
    [body_kind]         nvarchar(20) NOT NULL,
    [body_type]         nvarchar(400) NULL,
    [body_data]         nvarchar(max) NULL,
    [headers_json]      nvarchar(max) NULL,
    [properties_json]   nvarchar(max) NULL,
    [replayable]        bit NOT NULL,
    [replayed_at]       datetimeoffset NULL,
    CONSTRAINT [uq_tsak_dlq_entry_id] UNIQUE ([entry_id])
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsak_dlq_occurred' AND object_id = OBJECT_ID(N'[dbo].[tsak_dlq]'))
CREATE INDEX [ix_tsak_dlq_occurred] ON [dbo].[tsak_dlq] ([occurred_at] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsak_dlq_status' AND object_id = OBJECT_ID(N'[dbo].[tsak_dlq]'))
CREATE INDEX [ix_tsak_dlq_status] ON [dbo].[tsak_dlq] ([status]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tsak_dlq_route' AND object_id = OBJECT_ID(N'[dbo].[tsak_dlq]'))
CREATE INDEX [ix_tsak_dlq_route] ON [dbo].[tsak_dlq] ([route_id]);
