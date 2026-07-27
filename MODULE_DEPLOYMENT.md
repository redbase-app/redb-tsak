# redb.Tsak — Module Deployment & Signing

Deploying a module means shipping **code that Tsak loads and runs in-process**. This document
explains the trust model, because the pieces are easy to confuse: there are two verification
boundaries, two separate "require signature" switches, and a signing step that happens somewhere
else entirely (your build pipeline, not the node).

Read this once before turning on remote upload.

> See also: [CONFIG_GUIDE.md](CONFIG_GUIDE.md) for the config layers, [API_GUIDE.md](API_GUIDE.md)
> for the endpoints, [SECURITY.md](SECURITY.md) for the overall posture.

---

## 1. The one-paragraph version

A module is a `.tpkg` (a ZIP with a manifest + DLLs). It reaches a node one of two ways: an
**operator drops the file** into the module directory, or someone **uploads it via the API**. Both
paths end at the same place — the hot-reload scanner loads the package. The *trust anchor* is a
**signing key pair**: your CI signs the `.tpkg` with the **private** key; the node verifies it with
the **public** key before running any of its code. Remote upload is **off by default**; signature
enforcement is **off by default**. Turn both on together for a node that accepts remote deploys.

---

## 2. Two paths in, one gate

```
                     ┌─────────────────────────────┐
  operator drops     │                             │
  file into dir  ───►│   module directory          │
                     │   (Tsak:Modules:AssemblyPaths)
  POST /api/modules  │                             │
  /upload        ───►│   {name}.tpkg  {name}.tpkg.sig
                     └──────────────┬──────────────┘
                                    │
                                    ▼
                     ┌─────────────────────────────┐
                     │  HotReloadService — LOAD GATE│   ← the authoritative check
                     │  Tsak:Modules:Signature      │
                     │  .Required ? verify(.sig)    │
                     └──────────────┬──────────────┘
                          pass ─────┼───── fail → refused, logged, NOT loaded
                                    ▼
                            loaded & running
```

The **load gate** is the check that matters: it runs before any code from the package executes,
and it applies to **every** `.tpkg` regardless of how it arrived. So the public key — not "who has
write access to the directory" — decides what may run.

The API upload *also* verifies the signature early (fail-fast, before the file is written), but
that's a convenience so a bad upload is rejected with a clear message. The load gate is what
actually protects you.

---

## 3. The two "require signature" switches — do not confuse them

There are two, at different boundaries, and they answer different questions.

| Switch | Boundary | Question it answers | Default |
|---|---|---|---|
| `Tsak:Modules:Signature:Required` | **load** (HotReloadService) | "May this `.tpkg` run at all?" — applies to operator drops AND uploads | `false` |
| `Tsak:Modules:Upload:RequireSignatureForUpload` | **upload** (API only) | "May this upload proceed when no key is configured?" | `true` |

- `Signature:Required = true` is the strong one. With a public key configured, **nothing** loads
  without a valid signature — an operator who drops an unsigned file gets it refused too. This is
  the setting that makes a leaked admin API key harmless (the attacker still lacks the private key).
- `Upload:RequireSignatureForUpload` only governs the *upload endpoint when no public key is set*.
  With it `true` (default) and no key, uploads are refused outright — you cannot accidentally stand
  up an unauthenticated code-push endpoint. Set it `false` only to deliberately allow unsigned
  uploads (dev boxes), which logs a loud warning on every upload.

**Rule of thumb:** for production, configure a public key and set `Signature:Required = true`.
Then both switches are satisfied and both boundaries are protected.

---

## 4. Who signs, and how

Signing happens in your **build / release pipeline**, by whoever holds the **private** key — never
on the node. The node only ever sees the public key.

### Generate a key pair (once)

```bash
tsak module keygen --out tsak-module
#   tsak-module.key  → PRIVATE key. Keep it in your CI secret store. Never deploy it.
#   tsak-module.pub  → PUBLIC key. Configure it on every node.
```

(ECDSA P-256 by default. RSA keys work too — the verifier accepts either.)

### Sign a package (in CI, after building the .tpkg)

```bash
tsak module sign MyModule.tpkg --key tsak-module.key
#   → MyModule.tpkg.sig   (base64 detached signature over the .tpkg bytes)
```

Equivalent with OpenSSL, if your CI doesn't have the `tsak` CLI:

```bash
openssl dgst -sha256 -sign tsak-module.key -out MyModule.tpkg.sig.raw MyModule.tpkg
base64 -w0 MyModule.tpkg.sig.raw > MyModule.tpkg.sig
```

Ship the `.tpkg` **and** the `.tpkg.sig` together.

---

## 5. Configure a node to verify

```jsonc
{
  "Tsak": {
    "Modules": {
      "AssemblyPaths": [ "modules" ],
      "Signature": {
        "Required": true,                       // refuse unsigned/tampered .tpkg at load
        "PublicKeyPath": "keys/tsak-module.pub" // or PublicKeyPem: "-----BEGIN PUBLIC KEY----- …"
      },
      "Upload": {
        "Enabled": true,                        // turn ON the remote upload endpoint
        "MaxSizeMB": 100,
        "KeepVersions": 3                        // previous .tpkg kept on disk for rollback
      }
    }
  }
}
```

- Leave `Upload:Enabled = false` (the default) on any node that doesn't need remote deploys — it
  removes the upload surface entirely.
- With `Signature:Required = true` and a `PublicKeyPath`, even a file dropped straight into
  `modules/` must have a valid `modules/{name}.tpkg.sig` or it won't load.

---

## 6. Deploy and roll back

```bash
# Upload (the CLI sends the .tpkg body + the .tpkg.sig as the X-Tsak-Signature header)
tsak module deploy MyModule.tpkg            # auto-picks MyModule.tpkg.sig if present
tsak module deploy MyModule.tpkg --sig other.sig

# Roll back to the previous on-disk version
tsak module rollback MyModule
```

Both require the **admin** role and are recorded in the [audit trail](API_GUIDE.md#audit).
The node keeps `KeepVersions` previous packages as `{name}.tpkg.v0001`, `…v0002`, …; `rollback`
restores the most recent archived one and triggers a hot-reload.

Raw API (for non-CLI CI):

```
POST /api/modules/upload
  Header: X-Tsak-Signature: <base64 signature>   (omit only if unsigned uploads are allowed)
  Body:   <raw .tpkg bytes>

POST /api/modules/{name}/rollback
```

---

## 7. What each guard defends against

| Guard | Stops |
|---|---|
| `Upload:Enabled = false` (default) | the entire remote code-push surface — no endpoint, no risk |
| `admin` role on upload/rollback | a low-privilege key deploying code |
| Signature at the **load gate** | running any package not signed by your private key — even a file dropped on the box |
| Fail-fast signature at upload | a bad upload landing on disk at all |
| Size ceiling | a memory/disk DoS via a huge body |
| Valid-ZIP + manifest check | garbage or malformed packages |
| Name taken from the **manifest**, sanitized (no `/`, `\`, `..`) | path traversal / zip-slip — the file can only land inside the module directory |
| Atomic install (temp → move) | a half-written package being loaded |
| Audit on every upload/rollback | "who deployed what, when" being unanswerable |

---

## 8. How this compares to WSO2 MI

WSO2 Micro Integrator deploys CApps (`.car`, also a ZIP that can carry class-mediator JARs) by
dropping them into `repository/deployment/server/carbonapps/` — a filesystem hot-deploy, exactly
like Tsak's module directory. WSO2 MI does **not** verify artifact signatures at runtime; its trust
anchor is "who can write to the folder / who can call the authenticated management API." Tsak's
optional load-boundary signature check is therefore **stricter** than the WSO2 MI default: with it
on, filesystem access alone is not enough to run code.
