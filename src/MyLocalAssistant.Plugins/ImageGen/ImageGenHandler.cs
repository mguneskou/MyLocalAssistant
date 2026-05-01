using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyLocalAssistant.Plugin.Shared;

namespace MyLocalAssistant.Plugins.ImageGen;

/// <summary>
/// Generates images via a local Stable Diffusion backend (Automatic1111 or ComfyUI).
/// The generated PNG is saved to the conversation WorkDirectory.
/// Structured result: {"type":"image","filename":"...","path":"...","width":512,"height":512}
/// Config JSON: {"provider":"auto1111","baseUrl":"http://localhost:7860","defaultWidth":512,
///               "defaultHeight":512,"defaultSteps":20,"model":""}
/// </summary>
internal sealed class ImageGenHandler : IPluginTool
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private string _provider     = "auto1111";
    private string _baseUrl      = "http://localhost:7860";
    private int    _defaultWidth  = 512;
    private int    _defaultHeight = 512;
    private int    _defaultSteps  = 20;
    private string _model         = "";

    public void Configure(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return;
        var cfg = JsonSerializer.Deserialize<Config>(configJson, s_json);
        if (cfg is null) return;
        if (!string.IsNullOrWhiteSpace(cfg.Provider)) _provider    = cfg.Provider.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(cfg.BaseUrl))  _baseUrl     = cfg.BaseUrl.TrimEnd('/');
        if (cfg.DefaultWidth  is > 0)                 _defaultWidth  = cfg.DefaultWidth.Value;
        if (cfg.DefaultHeight is > 0)                 _defaultHeight = cfg.DefaultHeight.Value;
        if (cfg.DefaultSteps  is > 0)                 _defaultSteps  = cfg.DefaultSteps.Value;
        if (cfg.Model is not null)                    _model         = cfg.Model;
    }

    public async Task<PluginToolResult> InvokeAsync(
        string toolName, JsonElement arguments, PluginContext context, CancellationToken ct)
    {
        var prompt   = arguments.TryGetProperty("prompt",          out var p)  ? p.GetString()  ?? "" : "";
        var negPrompt= arguments.TryGetProperty("negative_prompt", out var np) ? np.GetString() ?? "" : "";
        var width    = arguments.TryGetProperty("width",           out var w)  && w.TryGetInt32(out var wn)  ? wn : _defaultWidth;
        var height   = arguments.TryGetProperty("height",          out var h)  && h.TryGetInt32(out var hn)  ? hn : _defaultHeight;
        var steps    = arguments.TryGetProperty("steps",           out var st) && st.TryGetInt32(out var sn) ? sn : _defaultSteps;
        var filename = arguments.TryGetProperty("filename",        out var fn) ? fn.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(prompt))
            return PluginToolResult.Error("prompt is required");

        // Clamp to reasonable SD limits.
        width  = Math.Clamp(width,  64, 2048);
        height = Math.Clamp(height, 64, 2048);
        steps  = Math.Clamp(steps,  1,  150);

        if (string.IsNullOrWhiteSpace(filename))
            filename = $"image_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
        if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            filename += ".png";

        var workDir = context.WorkDirectory;
        if (string.IsNullOrWhiteSpace(workDir))
            return PluginToolResult.Error("WorkDirectory not set — cannot save image");

        Directory.CreateDirectory(workDir);
        var outputPath = Path.Combine(workDir, filename);

        try
        {
            byte[] imageBytes = _provider switch
            {
                "comfyui" => await GenerateComfyUiAsync(prompt, negPrompt, width, height, steps, ct),
                _         => await GenerateAuto1111Async(prompt, negPrompt, width, height, steps, ct),
            };

            await File.WriteAllBytesAsync(outputPath, imageBytes, ct);

            var structured = JsonSerializer.Serialize(new
            {
                type     = "image",
                filename,
                path     = outputPath,
                width,
                height,
            }, s_json);

            return PluginToolResult.Ok(
                $"Image generated and saved: {filename} ({width}×{height}, {steps} steps)",
                structured);
        }
        catch (HttpRequestException ex)
        {
            return PluginToolResult.Error(
                $"Could not reach {_provider} at {_baseUrl}: {ex.Message}. " +
                "Ensure the local Stable Diffusion server is running.");
        }
        catch (Exception ex)
        {
            return PluginToolResult.Error($"Image generation failed: {ex.Message}");
        }
    }

    // ── Automatic1111 (stable-diffusion-webui) ────────────────────────────────

    private async Task<byte[]> GenerateAuto1111Async(
        string prompt, string negPrompt, int w, int h, int steps, CancellationToken ct)
    {
        var payload = new
        {
            prompt,
            negative_prompt = negPrompt,
            width  = w,
            height = h,
            steps,
            sd_model_checkpoint = _model.Length > 0 ? _model : (string?)null,
        };

        using var resp = await s_http.PostAsJsonAsync($"{_baseUrl}/sdapi/v1/txt2img", payload, s_json, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("images", out var images) ||
            images.GetArrayLength() == 0)
            throw new InvalidDataException("Auto1111 returned no images.");

        var base64 = images[0].GetString()
            ?? throw new InvalidDataException("Auto1111 image element was null.");

        // Strip the data URI prefix if present.
        var comma = base64.IndexOf(',');
        if (comma >= 0) base64 = base64[(comma + 1)..];

        return Convert.FromBase64String(base64);
    }

    // ── ComfyUI ───────────────────────────────────────────────────────────────

    private async Task<byte[]> GenerateComfyUiAsync(
        string prompt, string negPrompt, int w, int h, int steps, CancellationToken ct)
    {
        // ComfyUI: queue a basic txt2img workflow, poll for completion, download result.
        var clientId = Guid.NewGuid().ToString("N");

        // Minimal KSampler workflow JSON.
        var workflow = BuildComfyWorkflow(prompt, negPrompt, w, h, steps, clientId);

        // Queue the prompt.
        var queueResp = await s_http.PostAsJsonAsync(
            $"{_baseUrl}/prompt", new { prompt = workflow, client_id = clientId }, s_json, ct);
        queueResp.EnsureSuccessStatusCode();
        var queueJson   = await queueResp.Content.ReadAsStringAsync(ct);
        var queueDoc    = JsonDocument.Parse(queueJson);
        var promptId    = queueDoc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new InvalidDataException("ComfyUI did not return prompt_id.");

        // Poll history endpoint until the job is done.
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            await Task.Delay(2000, pollCts.Token).ConfigureAwait(false);
            var histResp = await s_http.GetAsync($"{_baseUrl}/history/{promptId}", pollCts.Token);
            histResp.EnsureSuccessStatusCode();
            var histJson = await histResp.Content.ReadAsStringAsync(pollCts.Token);
            var histDoc  = JsonDocument.Parse(histJson);

            if (!histDoc.RootElement.TryGetProperty(promptId, out var entry)) continue;
            if (!entry.TryGetProperty("outputs", out var outputs)) continue;

            // Find first image output.
            foreach (var node in outputs.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("images", out var images)) continue;
                foreach (var img in images.EnumerateArray())
                {
                    var imgFilename  = img.TryGetProperty("filename",  out var fv) ? fv.GetString() : null;
                    var subfolder    = img.TryGetProperty("subfolder", out var sv) ? sv.GetString() : "";
                    var imgType      = img.TryGetProperty("type",      out var tv) ? tv.GetString() : "output";
                    if (imgFilename is null) continue;
                    var viewUrl = $"{_baseUrl}/view?filename={Uri.EscapeDataString(imgFilename)}" +
                                  $"&subfolder={Uri.EscapeDataString(subfolder ?? "")}" +
                                  $"&type={Uri.EscapeDataString(imgType ?? "output")}";
                    return await s_http.GetByteArrayAsync(viewUrl, pollCts.Token);
                }
            }
        }
    }

    private static object BuildComfyWorkflow(
        string prompt, string negPrompt, int w, int h, int steps, string clientId)
    {
        // Minimal 6-node workflow: CheckpointLoader → CLIPTextEncode × 2 → EmptyLatentImage → KSampler → VAEDecode → SaveImage
        return new
        {
            _1 = new { class_type = "CheckpointLoaderSimple", inputs = new { ckpt_name = "v1-5-pruned-emaonly.ckpt" } },
            _2 = new { class_type = "CLIPTextEncode",         inputs = new { text = prompt,    clip = new object[] { "1", 1 } } },
            _3 = new { class_type = "CLIPTextEncode",         inputs = new { text = negPrompt, clip = new object[] { "1", 1 } } },
            _4 = new { class_type = "EmptyLatentImage",       inputs = new { width = w, height = h, batch_size = 1 } },
            _5 = new { class_type = "KSampler",               inputs = new { model = new object[] { "1", 0 }, positive = new object[] { "2", 0 }, negative = new object[] { "3", 0 }, latent_image = new object[] { "4", 0 }, seed = Random.Shared.NextInt64(), steps, cfg = 7.0, sampler_name = "euler", scheduler = "normal", denoise = 1.0 } },
            _6 = new { class_type = "VAEDecode",              inputs = new { samples = new object[] { "5", 0 }, vae = new object[] { "1", 2 } } },
            _7 = new { class_type = "SaveImage",              inputs = new { images = new object[] { "6", 0 }, filename_prefix = $"mla_{clientId[..8]}" } },
        };
    }

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Config
    {
        [JsonPropertyName("provider")]      public string? Provider      { get; set; }
        [JsonPropertyName("baseUrl")]       public string? BaseUrl       { get; set; }
        [JsonPropertyName("defaultWidth")]  public int?    DefaultWidth  { get; set; }
        [JsonPropertyName("defaultHeight")] public int?    DefaultHeight { get; set; }
        [JsonPropertyName("defaultSteps")]  public int?    DefaultSteps  { get; set; }
        [JsonPropertyName("model")]         public string? Model         { get; set; }
    }
}
