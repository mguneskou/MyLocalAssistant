# MyLocalAssistant

A C# .NET 8 WinForms app for running commercially-permissive open-source LLMs locally,
fully in-process via [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (llama.cpp bindings).

## v1.0.0.0 — Skeleton + first-run model wizard

This release contains the foundation:

- Solution + project structure (`MyLocalAssistant.App`, `MyLocalAssistant.Core`, tests)
- LLamaSharp integration with **CPU + CUDA 12 + Vulkan** backends, auto-selected at startup
- Curated catalog of 18 commercially-permissive models across four hardware tiers
  (Lightweight / Mid / Heavy / Workstation) — embedded as a JSON resource
- First-run wizard with grouped checkbox list and live "X models, Y.Y GB" summary
- Resumable HTTP downloader with per-file progress, SHA256 verification, and 2-way concurrency
- Models management screen (Installed / Available tabs)
- Hidden smoke test (`Ctrl+Shift+T`) that loads the smallest installed model and generates 10 tokens
- Portable folder layout: `./models`, `./config`, `./logs` next to the `.exe`
- Serilog rolling file logging
- Unit tests covering SHA verification, catalog parsing, and downloader resume / mismatch handling

**Not included in this release:** chat UI, prompt templates, conversation history, RAG, voice.
SHA256 fields in the catalog are intentionally empty for v1.0.0.0 (verifier treats empty as
"skip"); a build-time refresh script is planned for a follow-up.

## Build & run

```powershell
dotnet build -c Release
dotnet run --project src\MyLocalAssistant.App -c Release
```

Requires the .NET 8 SDK on Windows. First launch shows the model selection wizard.

## License

TBD.
