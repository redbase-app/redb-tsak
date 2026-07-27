# Security Policy — redb.Tsak

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.0.0-preview.1 | Yes |

Older versions do not receive security fixes. Upgrade to the latest release.

---

## Reporting a Vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Report privately to: **security@redbase.app**

Include in your report:
- Package and version (`tsak --version`)
- Deployment mode: `InMemory`, `Redb:Postgres`, `Redb:MSSql`, Cluster
- Detailed description of the vulnerability
- Steps to reproduce
- Potential impact assessment
- Your suggested fix (optional but appreciated)

You will receive an acknowledgment within 2 business days. We aim to publish a fix and disclose the issue within 30 days.

---

## Scope

This policy covers the following packages and components:

| Project | Area |
|---------|------|
| `redb.Tsak.Core` | Authentication, API key store, cluster coordination, module isolation |
| `redb.Tsak.Worker` | REST API, config handling, hosted service startup |
| `redb.Tsak.Contracts` | Wire DTOs, input validation |
| `redb.Tsak.Client` | HTTP client, credential handling |
| `redb.Tsak.CLI` | CLI credential storage, `--key` flag handling |
| `redb.Tsak.Web` | Web dashboard authentication, session management |

---

## Security Architecture

### API Authentication

Tsak uses API Key authentication with HMAC-SHA256 hashing.

- Raw keys are **never stored** — only `SHA-256(key)` is persisted
- Key comparison uses `CryptographicOperations.FixedTimeEquals` to prevent timing attacks
- Keys are transmitted via `Authorization: Bearer <key>` or `X-Api-Key: <key>` header
- The `_system` context exposes the REST API and cannot be stopped or removed by any key, including admin keys

### Key Store

- **InMemory mode**: keys are loaded from `Tsak:Auth:Keys` in `appsettings.json` at startup and are read-only at runtime
- **Redb mode**: `RedbApiKeyStore` stores keys in the redb store — supports create, list, and revoke at runtime
- 5-minute TTL cache reduces DB reads without significantly widening the revocation window

### Cluster Security

- Leader election uses a distributed lock with epoch fencing — stale leaders cannot issue commands after losing election
- Heartbeat-based dead-node detection prevents zombie nodes from holding locks indefinitely
- Inter-node communication uses the same API Key authentication as external clients
- Cluster state (topology, assignments) is stored in the redb store — access requires DB credentials

### Module Isolation

- Each module is loaded in its own `AssemblyLoadContext` — a compromised module cannot directly access another module's types
- `Collectible` ALC mode is **disabled by default** because `Reflection.Emit`-based code (serializers, compiled regex) crashes inside collectible contexts — enabling it without understanding the risk can cause runtime failures
- Modules are loaded from the `Tsak:Modules:AssemblyPaths` directories — **restrict write access** to these directories in production
- Tsak does not sandbox module code — a module with malicious code runs with the same OS privileges as the Tsak process

### Configuration and Secrets

- `Tsak:Auth:Secret` (JWT signing key) and connection strings must never be committed to source control
- Use environment variable overrides or a secrets manager in production
- See DEPLOYMENT_SECRETS.md for the complete secrets management guide

---

## Security Notes for Operators

### Harden the REST API in Production

```json
{
  "Tsak": {
    "Auth": {
      "Enabled": true,
      "Secret": "<strong-random-secret-min-32-chars>",
      "Keys": []
    }
  }
}
```

- Always set `Tsak:Auth:Enabled = true` in production
- Use a randomly generated `Secret` of at least 32 characters
- Pre-seed keys via config only for bootstrap — manage keys at runtime via API
- Assign minimal roles — use `reader` role for monitoring systems, `admin` only for operators

### Protect the Module Directory

```bash
# Linux: restrict Libs/ to the tsak service account
chown -R tsak:tsak /opt/tsak/Libs
chmod -R 750 /opt/tsak/Libs
```

Unauthorized writes to `Libs/` can result in arbitrary code execution inside the Tsak process.

### TLS

Tsak Worker's built-in API (`port 9090`) is HTTP only. In production:
- Place a reverse proxy (nginx, Caddy, Traefik) in front and terminate TLS there
- OR configure Kestrel TLS directly:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:9443",
        "Certificate": { "Path": "/certs/tsak.pfx", "Password": "..." }
      }
    }
  }
}
```

### Cluster Communication

- All inter-node calls go through the REST API — secure it with API keys and TLS
- Do not expose `port 9090` to the public internet — use a private network or VPN for cluster node communication
- Rotate the `NodeId` if a node is decommissioned to prevent stale node resurrection

### Web Dashboard (`redb.Tsak.Web`)

- The dashboard uses in-memory session authentication — sessions do not survive a server restart
- Set strong `AdminLogin` / `AdminPassword` credentials; do not use defaults in production
- In cluster mode, dashboard credentials are validated against the redb-backed user store — manage users via the Auth page
- The dashboard communicates with worker nodes over HTTP — secure that channel with TLS

### Dependency Modules

- Tsak executes module code with the same OS privileges as the host process — audit third-party modules before deployment
- Pin module versions in `manifest.json` — Tsak's hot-reload will apply any updated DLL found in `Libs/`
- Monitor the `WatchdogService` — unexpected module restarts may indicate a module crash-looping on bad input
