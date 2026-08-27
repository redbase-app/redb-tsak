# redb.Tsak Configuration Guide

This guide is about **how** configuration is assembled: the five layers, how they merge, and which
one wins. For **what** each setting is called and what it defaults to, see
PARAMETERS.md.

## 5-Layer Configuration Model

redb.Tsak uses a 5-layer configuration model where each layer merges over the previous one.
Later layers always win on conflicts. Nested objects are deep-merged (not replaced wholesale).

```
Layer 1: Tsak:Contexts:default          ← base settings for ALL contexts
Layer 2: Tsak:Contexts:{name}           ← per-context overrides
Layer 3: context.json (from modules/)      ← module infrastructure defaults
Layer 4: {Module}.config.json (modules/)   ← module builder settings
Layer 5: Tsak:Contexts:{name}:Override  ← DevOps last word (always wins)
```

### Layer Priority (lowest → highest)

| Layer | Source | Who writes it | Purpose |
|-------|--------|---------------|---------|
| 1 | `appsettings.json` → `Tsak:Contexts:default` | Operator | Base defaults for all contexts |
| 2 | `appsettings.json` → `Tsak:Contexts:{name}` | Operator | Per-context settings |
| 3 | `modules/{Module}/context.json` | Module developer | Module infrastructure defaults |
| 4 | `modules/{Module}/{Module}.config.json` | Module developer | Module builder/business settings |
| 5 | `appsettings.json` → `Tsak:Contexts:{name}:Override` | DevOps | Final override (always wins) |

## Configuration Examples

### appsettings.json

```json
{
  "Tsak": {
    "Contexts": {
      "default": {
        "AutoStart": true,
        "RabbitMQ": {
          "Host": "rabbitmq.local",
          "Port": 5672,
          "VirtualHost": "/"
        }
      },
      "api": {
        "Modules": ["Api.Orders", "Api.Catalog"],
        "RabbitMQ": {
          "VirtualHost": "/api"
        },
        "Override": {
          "RabbitMQ": {
            "Host": "prod-rabbit.internal"
          }
        }
      }
    }
  }
}
```

In this example, the `api` context will get:
- `RabbitMQ.Host` = `"prod-rabbit.internal"` (from Override, layer 5)
- `RabbitMQ.Port` = `5672` (inherited from default, layer 1)
- `RabbitMQ.VirtualHost` = `"/api"` (from named context, layer 2)
- `AutoStart` = `true` (inherited from default, layer 1)

### Module Config Files

Module developers can ship config files alongside their DLLs in the `modules/` directory:

```
modules/
  Api.Orders/
    Api.Orders.dll
    context.json              ← Layer 3: infrastructure defaults
    Api.Orders.config.json    ← Layer 4: builder/business settings
```

**context.json** — infrastructure defaults the module needs:

```json
{
  "RabbitMQ": {
    "Exchange": "orders",
    "QueuePrefix": "orders."
  },
  "Redis": {
    "Database": 2
  }
}
```

**{Module}.config.json** — business/builder settings:

```json
{
  "OrderProcessing": {
    "MaxRetries": 3,
    "TimeoutSeconds": 30
  },
  "FeatureFlags": {
    "EnableBulkImport": true
  }
}
```

### Deep Merge Behavior

Nested objects are merged recursively, not replaced. Example:

```
default:           { "RabbitMQ": { "Host": "a", "Port": 5672 } }
named context:     { "RabbitMQ": { "Host": "b" } }
─────────────────────────────────────────────────────
result:            { "RabbitMQ": { "Host": "b", "Port": 5672 } }
```

Port is preserved from default while Host is overridden by the named context.

## Reserved Keys

The following keys are reserved and excluded from context configuration:

- `Modules` — array of module names assigned to a named context
- `Override` — the DevOps override section (layer 5)

## Hot Reload

Config files (`context.json`, `{Module}.config.json`) are monitored by the hot-reload service.
When a config file changes on disk, the affected module's context is automatically recreated
with the updated configuration. No restart required.

### How it works

1. Each scan cycle checks timestamps of known config files
2. If a config file has changed since the last scan, the module's context is recreated
3. The full 5-layer merge is re-executed with the new file contents
4. The context is restarted automatically (if AutoStart is enabled)

## Accessing Config in Routes

Configuration values are set as context properties. Access them in your route modules:

```csharp
public class OrdersRoute : RouteBuilder
{
    public override void Configure(IRouteContext context)
    {
        // Simple value (Layer 1-2)
        var autoStart = context.GetProperty<string>("AutoStart");
        
        // Nested object (Layer 1-5 merged)
        var rabbitConfig = context.GetProperty<IDictionary<string, object?>>("RabbitMQ");
        var host = rabbitConfig?["Host"]?.ToString();
        var port = rabbitConfig?["Port"]?.ToString();
    }
}
```

## Named vs Anonymous Contexts

- **Named context**: Defined in `Tsak:Contexts:{name}` with a `Modules` array. Multiple modules share one context.
- **Anonymous context**: Created automatically for modules not assigned to any named context. One module = one context.

```json
{
  "Tsak": {
    "Contexts": {
      "api": {
        "Modules": ["Api.Orders", "Api.Catalog"],
        "AutoStart": true
      }
    }
  }
}
```

Modules `Api.Orders` and `Api.Catalog` share the `api` context. Any other module gets its own anonymous context.

## Graceful Shutdown on Module Removal

When a module's DLL is removed from the `modules/` directory:

1. The hot-reload service detects the missing file (with debounce to avoid false positives during file replacement)
2. The module's context is gracefully stopped (connections closed, consumers drained)
3. The module is unregistered from the registry
4. The AssemblyLoadContext is cleaned up

Configure the debounce threshold in `appsettings.json`:

```json
{
  "Tsak": {
    "HotReload": {
      "RemovalDebounceScans": 2
    }
  }
}
```
