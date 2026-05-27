# Copilot Project Handoff

Date: 2026-05-27
Repository: https://github.com/mguneskou/MyLocalAssistant
Branch: main

## Purpose

This handoff is for continuing development on another machine with minimal re-discovery.
It captures architecture, recent completed work, validation status, and safe next steps.

## Project Architecture Snapshot

- Server: `src/MyLocalAssistant.Server` (.NET 8 ASP.NET Core minimal API)
- Desktop host: `src/MyLocalAssistant.ServerHost` (WinForms tray app)
- Web client: `src/MyLocalAssistant.Web` (React + Vite, served from server wwwroot)
- Core library: `src/MyLocalAssistant.Core`
- Shared contracts: `src/MyLocalAssistant.Shared`
- Tests: `src/tests/MyLocalAssistant.Core.Tests` (xUnit)

Auth/security baseline:
- JWT bearer (HS256), default 30 min access, 14 day refresh
- PBKDF2-SHA256 password hashes in format `pbkdf2$sha256$<iters>$<salt>$<hash>`

Model/provider baseline:
- Providers routed by `ModelSource` via `ChatProviderRouter`
- Sources currently include: Local, OpenAi, Anthropic, Groq, Gemini, Mistral, Cerebras

## Work Completed In This Push

### 1) Behavior-neutral metadata/cosmetic updates

- Provider XML docs de-drifted and made model-agnostic:
  - `src/MyLocalAssistant.Server/Llm/GeminiChatProvider.cs`
  - `src/MyLocalAssistant.Server/Llm/GroqChatProvider.cs`
  - `src/MyLocalAssistant.Server/Llm/CerebrasChatProvider.cs`
- Whitespace/formatting cleanup:
  - `src/MyLocalAssistant.Server/Api/SettingsEndpoints.cs`

### 2) Latent bug fixes (only observable as avoiding failures)

- Fixed race in LanceDB table init path:
  - `src/MyLocalAssistant.Server/Rag/LanceDbVectorStore.cs`
  - `EnsureCollectionAsync` now re-checks and mutates under `_initLock`
- Scheduler owner-null safety:
  - `src/MyLocalAssistant.Server/Hosting/SchedulerHostedService.cs`
  - If owner user no longer exists, logs warning and skips run (avoids FK issues)
- Atomic schedule persistence:
  - `src/MyLocalAssistant.Server/Hosting/SchedulerHostedService.cs`
  - `SaveSchedules` now writes temp file then replaces/moves target
- Password hash cost bump for new hashes:
  - `src/MyLocalAssistant.Server/Auth/Pbkdf2Hasher.cs`
  - Default iterations changed from 210000 to 600000
  - Legacy hashes remain valid because verify reads iteration count from stored hash

### 3) Test coverage additions

Added:
- `src/tests/MyLocalAssistant.Core.Tests/Pbkdf2HasherTests.cs`
- `src/tests/MyLocalAssistant.Core.Tests/BuildCeoModeAComplianceSupplementIfNeededTests.cs`
- `src/tests/MyLocalAssistant.Core.Tests/UserServiceRefreshTokenTests.cs`
- `src/tests/MyLocalAssistant.Core.Tests/ChatProviderRouterTests.cs`

Coverage intent:
- PBKDF2 compatibility and defaults
- CEO Mode A supplement generation and idempotence
- Refresh token rotation and one-time-use semantics
- Router mapping for all known model sources and unknown-source throw path

### 4) Git hygiene

Updated ignore rules in `.gitignore`:
- `tester_*.pdf`
- `docs/todo.md`
- `docs/agent-prompt-preview-*.md`

## Validation Status

Latest test command executed successfully:

`dotnet test src\tests\MyLocalAssistant.Core.Tests\MyLocalAssistant.Core.Tests.csproj -c Debug --nologo --verbosity minimal`

Result: Passed 92, Failed 0, Skipped 0

## Explicitly Deferred (not changed in this push)

These were intentionally not changed because they alter runtime behavior/policy:

- HTTPS/TLS enforcement and related auth transport policy changes
- Rate limiting policy changes
- Owner/global-admin credential model changes
- EF migration model overhaul
- Model catalog pruning of speculative cloud entries

## Continue Work On Another Laptop

1. Clone and open the repo.
2. Pull latest `main`.
3. Run:
   - `dotnet build MyLocalAssistant.slnx`
   - `dotnet test src\tests\MyLocalAssistant.Core.Tests\MyLocalAssistant.Core.Tests.csproj -c Debug`
4. Start from this handoff file and recent commits.

## Suggested Next Safe Iteration

- Keep building tests around auth and scheduler edge cases.
- If changing security posture (HTTPS, issuer/audience strictness, rate limiting), do it in a separate explicitly behavior-changing milestone.
- If provider-doc drift keeps recurring, consider generating provider model notes from catalog at build time.
