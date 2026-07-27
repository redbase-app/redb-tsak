@{
    # ─────────────────────────────────────────────────────────────────────────
    # Единый источник правды для состава shared-слоя (Libs/shared).
    # Потребители: build-shared.ps1 (сборка), позже — fail-fast preload и compat-gate
    # (через экспорт в Libs/shared/shared-manifest.json на этапе B).
    #
    # НЕ добавлять дубли между секциями. redb.Route.Sql — в Connectors (собирается
    # как коннектор уже сегодня), поэтому в Framework его НЕТ.
    # ─────────────────────────────────────────────────────────────────────────

    # Framework + провайдеры. Сегодня живут в bin (compile-ref из redb.Tsak.Core).
    # На этапе B переезжают в shared (build-shared -IncludeFramework) и убираются из bin.
    # НЕ включаются в сборку на этапе A → состав shared не меняется.
    Framework = @(
        'redb.Core'
        'redb.Core.Pro'
        'redb.Route.Core'
        'redb.Route.Http'
        'redb.Route.Quartz'
        'redb.Postgres'
        'redb.Postgres.Pro'
        'redb.MSSql'
        'redb.MSSql.Pro'
        'redb.SQLite'
        'redb.SQLite.Pro'
    )

    # Коннекторы — то, что build-shared кладёт в Libs/shared уже сегодня.
    # Порядок и состав идентичны прежним двум скриптам (включая redb.Route.Sql).
    Connectors = @(
        'redb.Route.RabbitMQ'
        'redb.Route.Amqp'
        'redb.Route.AzureServiceBus'
        'redb.Route.Controllers'
        'redb.Route.Elasticsearch'
        'redb.Route.Firebase'
        'redb.Route.Grpc'
        'redb.Route.Kafka'
        'redb.Route.Ldap'
        'redb.Route.Sql'
        'redb.Route.File'
        'redb.Route.Ftp'
        'redb.Route.GenericFile'
        'redb.Route.Redis'
        'redb.Route.S3'
        'redb.Route.SignalR'
        'redb.Route.Tcp'
        'redb.Route.Validation.Adapters'
        'redb.Route.WebSocket'
        'redb.Route.MqttNet'
        'redb.Route.Mail'
        'redb.Route.Sftp'
        'redb.Route.IbmMq'
        'redb.Route.Llm.Abstractions'
        'redb.Route.Llm'
        'redb.Route.Llm.Tools'
        'redb.Route.Llm.Mcp'
        'redb.Route.Exec'
        'redb.Route.Sqs'
        'redb.Route.Telegram'
    )
}
