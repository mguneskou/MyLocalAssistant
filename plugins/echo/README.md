# Echo reference plug-in

A minimal `ISkill` plug-in that echoes its input. It exists to verify the end-to-end
plug-in pipeline (signature verification, sandboxed launch, JSON-RPC over stdio).

## Build

```powershell
dotnet publish .\echo.csproj -c Release -r win-x64 --self-contained false -o .\publish\
```

## Package & sign (Phase 3b: SkillTools CLI does this)

1. Generate an ed25519 keypair: `SkillTools keygen --out config\trusted-keys\dev.pub --priv dev.key`
2. Edit `manifest.template.json`: set `keyId` to `dev` and fill the SHA-256 of `echo.exe`.
3. Save as `manifest.json` next to `echo.exe`.
4. Sign: `SkillTools sign manifest.json --key dev.key` → writes `manifest.json.sig`.
5. Drop the folder under `<install>/plugins/echo/`. Restart the server.

The plug-in then appears in the Admin → Skills tab, disabled by default; enable it
and bind it to an agent in Admin → Agents to expose `echo.say` to that agent.
