# ExampleWeatherTool

A minimal plugin tool for **MyLocalAssistant** that returns the current weather for a given city.

## Purpose

This project demonstrates the full structure of a plugin tool. Use it as a template when building your own.

## How to use

1. **Build** this project alongside the server (or add it to the solution).
2. **Register** the tool in `Server/Program.cs`:

   ```csharp
   var registry = app.Services.GetRequiredService<MyLocalAssistant.Server.Tools.ToolRegistry>();
   registry.Register(new ExampleWeatherTool.WeatherTool());
   ```

3. **Enable** it on an agent via Admin → Agents → Tools.
4. **Ask** the assistant "What is the weather in Istanbul?" — the LLM will call `get_weather`.

## Wiring a real API

Open `WeatherTool.cs` and replace `FetchWeatherAsync` with a real HTTP call. The method is already async and cancellation-aware.

## File structure

| File | Role |
|------|------|
| `WeatherTool.cs` | Main `ITool` implementation |
| `ExampleWeatherTool.csproj` | Project file; references the server project |
| `README.md` | This file |

See [plugin-development.md](../../docs/plugin-development.md) for a full guide.
