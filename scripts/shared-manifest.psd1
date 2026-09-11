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
        # Extracted from redb.Route.Http in 3.5.1 (SharedHttpServerManager). It reaches the shared
        # layer transitively anyway — publish output of Http carries it — but an undeclared assembly
        # gets neither the byte-preload fail-fast nor the minor compat-gate, so a mismatched copy
        # would be swallowed silently instead of aborting the start. Declare it.
        'redb.Route.Http.Hosting'
        # XML route artifacts (Route-XML Ф5): compile-ref of redb.Tsak.Core (XmlRouteModule)
        # and of redb.Route.Http (the <rest> contribution) — reaches the layer transitively
        # either way; declared so the preload fail-fast and the compat-gate see it.
        'redb.Route.Xml'
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
        # XPath 2.0 expressions (owner request 2026-09-03): module code using the XPath2 DSL
        # needs the assembly in the shared layer of a Tsak worker.
        'redb.Route.XPath2'
        # AS2/EDI (3.5.1). Without this line the connector never reaches Libs/shared, so a module
        # asking for an `as2://` endpoint finds no component in a Tsak worker at all.
        'redb.Route.As2'
        # Data formats for <marshal format="..."> / Unmarshal (owner request 2026-09-08). Unlike the
        # XML contributions below these are NOT auto-discovered: module code calls AddCsvDataFormat()
        # and friends, so the assembly must be in the shared layer for that call to resolve at all.
        'redb.Route.DataFormats.Csv'
        'redb.Route.DataFormats.Yaml'
        'redb.Route.DataFormats.Avro'
        'redb.Route.DataFormats.Protobuf'
        # XML DSL element contributions (Route-XML). XmlRouteModule.DiscoverContributions scans
        # loaded 'redb.*' assemblies for IXmlElementContribution, so without these lines an XML
        # route in a worker silently has no <cache>, <transformJson> or <payload> element at all.
        'redb.Route.Cache'
        'redb.Route.JsonTransform'
        'redb.Route.Templates'
        # SOAP connector. Same rule as As2 — without this line a module using a `soap://` endpoint
        # finds no component in a Tsak worker.
        'redb.Route.Soap'
    )
}
