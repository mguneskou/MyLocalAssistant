# MyLocalAssistant

A small-organization, fully-offline AI platform: a Windows-Service-hosted **server**
runs LLMs, agents, and a RAG pipeline; **WinForms clients** on the LAN authenticate
against it and chat with the agents they're allowed to see.

Built on .NET 8 and [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (CPU / CUDA 12
/ Vulkan auto-selected). No outbound network calls except admin-triggered model
downloads.

> **v1** (single-user desktop app) is preserved at tag [`v1.0.1`](../../releases/tag/v1.0.1).
> v2 is a server + clients rewrite that lives on the `v2-server` branch.

---

## v2.0.0.0 highlights

### Server (`MyLocalAssistant.Server`)

- ASP.NET Core 8 minimal API, hosted as a **Windows Service** (or `dotnet run` for dev).
- **Auth**: PBKDF2-hashed local accounts + JWT (HS256, short-lived access + refresh).
  Optional **LDAP / Active Directory** behind `IIdentityProvider`.
- **TLS**: dev cert + Windows Service install/uninstall PowerShell scripts.
- **Three-tier admin model**:
  - **Users** — chat with the agents their department / roles allow.
  - **System admin** (`admin` / `admin` first-login bootstrap) — manages users,
    departments, roles, models, RAG collections, server settings, audit.
  - **Global admin / owner** — hidden `owner` account, credentials hardcoded in
    source and rotated by recompile. Exclusive control over which agents are
    enabled, each agent's system prompt, and the server-wide global system prompt.
- **LLM serving**: single active model with a FIFO queue per LLamaSharp context;
  agents may request a specific default model.
- **RAG**: LanceDB-backed vector store, LLamaSharp embeddings, admin-driven
  ingestion (PDF / DOCX / XLSX / HTML / text / markdown), cosine + MMR retrieval.
- **Per-turn file attachments**: clients can attach a file to a single chat turn;
  the server parses and inlines it (ephemeral — not ingested into RAG).
- **Audit**: who / what / when, configurable retention (90 days default).

### Admin GUI (`MyLocalAssistant.Admin`)

WinForms admin console: Users, Departments, Roles, Models, RAG Collections,
Audit, Server Settings — and (owner only) an Agents tab with a per-agent
system-prompt editor and the global system prompt editor.

### Client (`MyLocalAssistant.Client`)

WinForms LAN client: login → ACL-filtered agent list → streaming chat panel
with file attachments, copy / export, conversation history per agent,
JWT auto-refresh, offline indicator.

### Repo layout

```
src/
  MyLocalAssistant.Server/   ASP.NET Core service (LLM, RAG, auth, audit, REST/SSE)
  MyLocalAssistant.Admin/    WinForms admin console
  MyLocalAssistant.Client/   WinForms end-user chat client
  MyLocalAssistant.Shared/   DTOs / contracts shared by server + clients
  MyLocalAssistant.Core/     Catalog, downloader, hashing (shared with v1)
  MyLocalAssistant.App/      v1 single-user desktop app (legacy, still buildable)
  tests/                     xUnit test project (31 tests)
```

---

## Build & run

Requires the **.NET 8 SDK** on Windows.

### Server (dev)

```powershell
dotnet build src\MyLocalAssistant.Server\MyLocalAssistant.Server.csproj -c Release
dotnet run  --project src\MyLocalAssistant.Server -c Release
```

On first launch the server creates `./data`, `./models`, `./vectors`, `./ingestion`,
`./logs`, `./config` next to the executable, and seeds:

- system admin `admin` / `admin` (forced password change on first login),
- global admin / owner `owner` / `owner` (hidden — rotate via the constants in
  `src/MyLocalAssistant.Server/Auth/UserService.cs` and recompile).

### Server as a Windows Service

```powershell
.\scripts\install-service.ps1
.\scripts\uninstall-service.ps1
```

### Admin GUI

```powershell
dotnet run --project src\MyLocalAssistant.Admin -c Release
```

### Client

```powershell
dotnet run --project src\MyLocalAssistant.Client -c Release
```

### Tests

```powershell
dotnet test src\tests\MyLocalAssistant.Core.Tests
```

---

## Versioning

| Tag         | What it is                                                         |
|-------------|--------------------------------------------------------------------|
| `v1.0.0.0`  | First v1 desktop release (skeleton + first-run model wizard).      |
| `v1.0.1`    | v1 patch release.                                                  |
| `v2.0.0.0`  | First v2 release — server + admin + client + RAG + ACL + owner.    |

## License

TBD.
