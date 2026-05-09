# Plugin Tool Development Guide

This guide explains how to build a **plugin tool** — a custom capability that any agent in MyLocalAssistant can call during a chat turn.

## Concepts

| Term | Meaning |
|------|---------|
| **Tool** | A capability the LLM can call by emitting `<tool_call>{ "name": "...", ... }</tool_call>`. |
| **ITool** | The C# interface every tool must implement (server-side). |
| **ToolContext** | Per-turn context passed to your tool: user identity, conversation ID, working directory, cancellation. |
| **ToolResult** | What your tool returns: a string for the LLM to read, optionally with a structured JSON payload for the UI. |
| **ToolRegistry** | Process-wide catalog. Built-in tools are registered at startup via DI. Plugin tools can be registered with `ToolRegistry.Register(ITool)`. |

---

## Quick-start: your first tool in 4 steps

### 1. Reference the Server project (or extract the contracts)

Your plugin DLL must reference `MyLocalAssistant.Server` **or** a separate contracts library you extract containing:

- `ITool`, `ToolContext`, `ToolResult`, `ToolInvocation` (in `MyLocalAssistant.Server.Tools`)
- `ToolFunctionDto`, `ToolRequirementsDto` (in `MyLocalAssistant.Shared.Contracts`)

```xml
<!-- tools/MyWeatherTool/MyWeatherTool.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\MyLocalAssistant.Server\MyLocalAssistant.Server.csproj" />
  </ItemGroup>
</Project>
```

### 2. Implement `ITool`

```csharp
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Shared.Contracts;

namespace MyWeatherTool;

public sealed class WeatherTool : ITool
{
    public string Id          => "weather.current";
    public string Name        => "Current weather";
    public string Description => "Gets the current weather for a city.";
    public string Category    => "Productivity";
    public string Source      => ToolSources.Plugin;   // "plugin"
    public string? Version    => "1.0.0";
    public string? Publisher  => "My Company";
    public string? KeyId      => null;

    public IReadOnlyList<ToolFunctionDto> Tools =>
    [
        new ToolFunctionDto(
            Name: "get_weather",
            Description: "Returns the current weather for the given city.",
            Parameters: new ToolParametersDto(
                Type: "object",
                Properties: new Dictionary<string, ToolPropertyDto>
                {
                    ["city"] = new ToolPropertyDto(Type: "string", Description: "City name, e.g. 'Istanbul'"),
                },
                Required: ["city"]))
    ];

    public ToolRequirementsDto Requirements => new(
        MinContextTokens: 2048,
        RequiresToolCalling: true,
        RequiresVision: false);

    public async Task<ToolResult> InvokeAsync(ToolInvocation inv, ToolContext ctx)
    {
        // Parse arguments.
        using var doc = System.Text.Json.JsonDocument.Parse(inv.ArgumentsJson);
        var city = doc.RootElement.GetProperty("city").GetString() ?? "";

        // Call your actual logic here.
        var summary = await FetchWeatherAsync(city, ctx.CancellationToken);
        return ToolResult.Ok(summary);
    }

    private static async Task<string> FetchWeatherAsync(string city, CancellationToken ct)
    {
        // TODO: call a real weather API.
        await Task.Delay(50, ct);
        return $"Weather in {city}: 22 °C, partly cloudy.";
    }
}
```

### 3. Register your tool at startup

In `Server/Program.cs` (or a startup extension) call:

```csharp
// After var app = builder.Build(); but before app.Run()
var registry = app.Services.GetRequiredService<ToolRegistry>();
registry.Register(new MyWeatherTool.WeatherTool());
```

Alternatively, you can register via DI and let the built-in enumeration pick it up:

```csharp
// In your service registration block
builder.Services.AddSingleton<ITool, MyWeatherTool.WeatherTool>();
```

### 4. Enable the tool on an agent

1. Open **Admin → Agents**.
2. Click the **Tools** cell for your agent.
3. Check your new tool in the list.
4. Save.

---

## ToolContext reference

| Property | Type | Description |
|----------|------|-------------|
| `UserId` | `Guid` | The end-user's ID. |
| `Username` | `string` | Display name. |
| `IsAdmin` | `bool` | Whether the caller is an admin. |
| `IsGlobalAdmin` | `bool` | Whether the caller is the global admin. |
| `AgentId` | `string` | Which agent is making the call. |
| `ConversationId` | `Guid` | Current conversation. |
| `WorkDirectory` | `string` | Per-conversation scratch folder (safe to write to). |
| `CancellationToken` | `CancellationToken` | Honour this to respect chat turn timeouts. |

---

## ToolResult reference

```csharp
// Success — LLM sees the content string.
return ToolResult.Ok("Weather in Istanbul: 22 °C");

// Success with structured JSON shown in the Admin UI / audit trail.
return ToolResult.Ok("Weather fetched.", JsonSerializer.Serialize(apiResponse));

// Error — LLM is told the tool failed; the Admin UI shows the error message.
return ToolResult.Error("City not found: " + city);
```

---

## Declaring parameters

Tools use a subset of JSON Schema. Example with optional parameters:

```csharp
new ToolParametersDto(
    Type: "object",
    Properties: new Dictionary<string, ToolPropertyDto>
    {
        ["query"]   = new("string",  "The search query"),
        ["maxRows"] = new("integer", "Maximum rows to return (default 10)", Enum: null),
    },
    Required: ["query"])   // maxRows is optional
```

---

## Best practices

- **Keep tools focused.** One tool per distinct capability is easier to enable selectively.
- **Honour cancellation.** Pass `ctx.CancellationToken` to every async call.
- **Return structured JSON when useful.** The `StructuredJson` payload is shown in the Admin UI and written to audit — great for debugging.
- **Never store secrets in the tool.** Retrieve them from configuration or environment variables at startup.
- **Validate inputs.** The LLM occasionally passes wrong types. Wrap `JsonDocument.Parse` in a try-catch and return `ToolResult.Error(...)`.
- **Use `WorkDirectory` for file output.** It is already namespaced per conversation and per user.

---

## Example project layout

```
tools/
  MyWeatherTool/
    MyWeatherTool.csproj
    WeatherTool.cs
    WeatherApiClient.cs   ← your HTTP client, etc.
```

See the built-in tools under `src/MyLocalAssistant.Server/Tools/Builtins/` for full examples.
