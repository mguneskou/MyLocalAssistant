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

## v2.1.4.0 highlights (current release)

New **Excel skill plug-in** — a signed plug-in (`plugins/excel/`) that lets agents read, query, write, format, and recalculate `.xlsx` workbooks entirely in-process via [ClosedXML](https://github.com/ClosedXML/ClosedXML). No Microsoft Office install required.

- **13 tools**: `list_sheets`, `read_range`, `read_table`, `find`, `describe`, `pivot`, `write_cells`, `append_row`, `create_workbook`, `set_format`, `set_formula`, `recalculate`, `evaluate`.
- **Live formula engine** — `set_formula` returns the computed result immediately; `recalculate` re-runs every formula in the workbook before saving; `evaluate` runs a one-off formula against a workbook context.
- **Sandboxed file access** — admin-controlled `allowedRoots` plus the per-conversation work directory; everything else returns `path_not_allowed`.
- **Writes are opt-in** — every mutating tool returns `writes_disabled` until the owner sets `"allowWrites": true` in the plug-in's `ConfigJson` from the Skills tab.
- **Bounded responses** — `maxRowsPerCall` and `maxCellBytes` cap payloads to keep the model's context lean.
- See [`plugins/excel/README.md`](plugins/excel/README.md) for the full tool list, config schema and build instructions (`pwsh ./plugins/excel/build.ps1`).

## v2.1.3.0 highlights

Follow-up to 2.1.2: the first-launch migration now also scans Velopack's prior-version `app-X.Y.Z\` sibling folders for leftover state, so users upgrading from 2.1.0 / 2.1.1 (which wrote everything inside the doomed `current\`) will have their downloaded models, database, vector store, plug-ins and settings auto-recovered if Velopack hasn't yet purged the old install.

## v2.1.2.0 highlights

Fix release. Persistent state (downloaded models, database, vector store, plug-ins, logs, settings) now survives Velopack updates by living in a sibling `state\` folder instead of being wiped when the `current\` folder is swapped.

- **State directory** — the server now writes models / data / vectors / config / plugins / logs / output to `%LocalAppData%\MyLocalAssistant\state\` on installed machines (and next to the executable for dev runs, so tests are unaffected).
- **One-shot legacy migration** — first launch after upgrading auto-moves any folders still found inside `current\` to the new state root.
- **Heads-up for testers upgrading from 2.1.0 / 2.1.1**: those builds stored everything in the per-version `current\` folder, which Velopack replaces atomically on each update; the data installed under those versions cannot be recovered. Re-download your model after upgrading to 2.1.2 — every update from this point on will preserve it.

## v2.1.1.0 highlights

Client chat polish release. Adds a modern bubble-style transcript and several quality-of-life tweaks; no server, contract, or installer changes.

### Client chat (new)

- **Bubble transcript** — user messages right-aligned in accent blue; assistant messages on a left-aligned light card; tool calls / system notes shown as centered amber chips; errors as centered red chips.
- **Speaker + timestamp** rendered under each bubble (e.g. `Assistant · 14:32`).
- **Pulsing typing indicator** — three dots in the empty assistant bubble until the first token arrives.
- **Sticky-bottom auto-scroll** — stream only follows the bottom if you were already there; scroll up to read history without being yanked back.
- **Selectable text in every bubble** + right-click *Copy message* / *Select all in this message* / *Copy whole transcript*.
- **Re-flow on resize** — bubbles cap at 78% of the pane width and re-wrap when the splitter moves.

## v2.1.0.0 highlights

Second v2 release. Hardens the platform, adds a plug-in skill runtime, and ships a
proper installer + auto-updater so testers can be onboarded with a single download.

### Distribution & lifecycle (new)

- **`MyLocalAssistant.ServerHost.exe`** — system-tray launcher that spawns the server
  as a child process, polls `/healthz` every 2 s, and exposes Open Admin / Open Client /
  Restart server / Open logs / About / Quit menu items. Single-instance via a global mutex.
- **Velopack-based installer**: `MyLocalAssistant-win-Setup.exe` installs per-user under
  `%LocalAppData%\MyLocalAssistant\current\` — no admin rights, no UAC prompt.
- **Automatic updates** from GitHub Releases: hourly silent check + manual
  *Check for updates…* menu item. Delta packages keep upgrades small.
- **GitHub Actions release pipeline** (`.github/workflows/release.yml`): pushing a
  `v*` tag builds, tests, packs (`scripts/release.ps1` → `vpk pack` → `vpk upload github`),
  and publishes the release.
- **Tester guide** ([tester_guide_v2.pdf](tester_guide_v2.pdf)) — install / use / update /
  uninstall / suggested test scenarios, including default credentials.

### Skills & plug-in runtime (new)

- **Skill catalog** with three built-in skills: `math.eval`, `time.now`,
  `rag.search_collection`. Per-agent capability binding from the Admin console.
- **Tag-mode tool calling**: agents emit `<tool>{...}</tool>` blocks, the server runs the
  skill, and feeds the result back into the same turn — model-agnostic, no JSON-grammar
  required.
- **Plug-in runtime**: out-of-process JSON-RPC over stdio, manifest + ed25519 signing,
  Windows **Job Object** sandbox (memory + CPU caps, no child processes, network ACL).
- **Reference plug-in** (`plugins/echo/`) and **`SkillTools` CLI** for signing /
  verifying / packaging plug-ins.

### Stability & polish

- **`DateTimeOffset` ↔ `long` (UtcTicks) value converter** — fixes the
  *"SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses"*
  crash on chat history.
- **Client crash hardening** — global `Application.ThreadException`,
  `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` handlers all
  log to `%LocalAppData%\MyLocalAssistant\client.log` + show a MessageBox; deferred
  `SplitContainer` sizing to `HandleCreated` to stop ctor-time `InvalidOperationException`.
- **Telemetry & hot-reload** of agent config; **sandbox ACL + UI limits** in the Admin
  console; minimum password length raised to 8 to match the server.
- **Modern WinForms theme**: rounded buttons, app icon, themed tool-strips and dialogs,
  branded login header.
- **31 → expanded** xUnit test suite (now also covers plug-in manifest parsing,
  signing, sandbox enforcement).

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
  MyLocalAssistant.Server/      ASP.NET Core service (LLM, RAG, auth, audit, REST/SSE)
  MyLocalAssistant.ServerHost/  Tray-icon launcher + Velopack auto-updater
  MyLocalAssistant.Admin/       WinForms admin console
  MyLocalAssistant.Client/      WinForms end-user chat client
  MyLocalAssistant.Shared/      DTOs / contracts shared by server + clients
  MyLocalAssistant.Core/        Catalog, downloader, hashing (shared with v1)
  MyLocalAssistant.App/         v1 single-user desktop app (legacy, still buildable)
  tests/                        xUnit test project
plugins/echo/                   Reference plug-in skill (out-of-process, signed)
tools/MyLocalAssistant.SkillTools/  Sign / verify / pack plug-ins
scripts/                        publish-all.ps1, release.ps1, install/uninstall service
.github/workflows/release.yml   Tag-driven Velopack release pipeline
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

## Packaging a release

Local end-to-end build of an installer:

```powershell
.\scripts\publish-all.ps1                      # publishes 4 exes into dist\stage\
.\scripts\release.ps1 -Version 2.1.3           # publish + vpk pack -> dist\releases\
.\scripts\release.ps1 -Version 2.1.3 -Upload `
    -GitHubToken $env:GH_PAT                   # also upload to GitHub Releases
```

In CI, just push a tag — `.github/workflows/release.yml` does the rest:

```powershell
git tag v2.1.3.0
git push origin v2.1.3.0
```

The workflow restores, runs tests, builds with `vpk`, and attaches
`MyLocalAssistant-win-Setup.exe` + delta `nupkg` + `RELEASES` manifest to the
created GitHub Release. Installed `ServerHost.exe` instances pick up the new
version within an hour (or immediately via *Check for updates…*).

See [tester_guide_v2.pdf](tester_guide_v2.pdf) for a full install / use / update /
uninstall walkthrough aimed at non-developer testers.

---

## Versioning

| Tag         | What it is                                                         |
|-------------|--------------------------------------------------------------------|
| `v1.0.0.0`  | First v1 desktop release (skeleton + first-run model wizard).      |
| `v1.0.1`    | v1 patch release.                                                  |
| `v2.0.0.0`  | First v2 release — server + admin + client + RAG + ACL + owner.    |
| `v2.1.3.0`  | Migration also scans Velopack `app-*\` sibling folders to rescue state from 2.1.0/2.1.1 installs. |
| `v2.1.2.0`  | Persistent state moved to sibling `state\` folder so it survives Velopack updates. |
| `v2.1.1.0`  | Client chat bubbles, typing indicator, sticky-bottom scroll, per-bubble copy. |
| `v2.1.0.0`  | ServerHost tray launcher, Velopack auto-update, plug-in skill runtime, stability fixes. |

## License

TBD.
