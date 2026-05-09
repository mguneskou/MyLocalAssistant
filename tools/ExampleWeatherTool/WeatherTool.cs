using System.Text.Json;
using MyLocalAssistant.Server.Tools;
using MyLocalAssistant.Shared.Contracts;

namespace ExampleWeatherTool;

/// <summary>
/// Example plugin tool — returns mock weather for a given city.
/// Replace <see cref="FetchWeatherAsync"/> with a real API call.
/// </summary>
public sealed class WeatherTool : ITool
{
    public string Id          => "weather.current";
    public string Name        => "Current weather";
    public string Description => "Gets the current weather conditions for a city.";
    public string Category    => "Productivity";
    public string Source      => ToolSources.Plugin;
    public string? Version    => "1.0.0";
    public string? Publisher  => "Example Corp";
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
                    ["city"] = new ToolPropertyDto(
                        Type: "string",
                        Description: "The city name, e.g. 'London' or 'Istanbul'"),
                },
                Required: ["city"]))
    ];

    public ToolRequirementsDto Requirements => new(
        MinContextTokens: 2048,
        RequiresToolCalling: true,
        RequiresVision: false);

    public async Task<ToolResult> InvokeAsync(ToolInvocation inv, ToolContext ctx)
    {
        string city;
        try
        {
            using var doc = JsonDocument.Parse(inv.ArgumentsJson);
            city = doc.RootElement.GetProperty("city").GetString()
                   ?? throw new JsonException("city is null");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Could not parse arguments: {ex.Message}");
        }

        try
        {
            var weather = await FetchWeatherAsync(city, ctx.CancellationToken);
            var structured = JsonSerializer.Serialize(new { city, weather });
            return ToolResult.Ok(weather, structured);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Request cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to get weather for '{city}': {ex.Message}");
        }
    }

    /// <summary>
    /// Replace this with a real HTTP call, e.g. to OpenWeatherMap or wttr.in.
    /// </summary>
    private static async Task<string> FetchWeatherAsync(string city, CancellationToken ct)
    {
        // TODO: call a real weather API here.
        // Example using wttr.in (no API key required):
        //   using var http = new HttpClient();
        //   return await http.GetStringAsync($"https://wttr.in/{Uri.EscapeDataString(city)}?format=3", ct);

        await Task.Delay(10, ct); // simulate async I/O
        return $"Weather in {city}: 21 °C, partly cloudy, humidity 65 %.";
    }
}
