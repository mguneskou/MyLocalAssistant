using LLama.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyLocalAssistant.Core.Inference;

/// <summary>
/// Configures LLamaSharp's native library selection. Must be called exactly once,
/// before any LLamaSharp API is used.
/// </summary>
public static class BackendSelector
{
    private static readonly object s_lock = new();
    private static bool s_configured;
    private static string s_selected = "Auto (pending first load)";

    public static string SelectedBackend => s_selected;

    /// <summary>
    /// Configures auto-fallback and a preferred AVX/CUDA priority.
    /// Safe to call repeatedly; only the first call has an effect.
    /// </summary>
    public static void Configure(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        lock (s_lock)
        {
            if (s_configured) return;
            try
            {
                NativeLibraryConfig.All
                    .WithAutoFallback(true)
                    .WithLogCallback((level, message) =>
                    {
                        if (level >= LLamaLogLevel.Info)
                        {
                            logger.LogDebug("[llama.cpp {Level}] {Message}", level, message?.TrimEnd());
                        }
                    });
                s_configured = true;
                logger.LogInformation("LLamaSharp native library configured with auto-fallback (CUDA -> Vulkan -> CPU).");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure LLamaSharp native library.");
                throw;
            }
        }
    }

    /// <summary>
    /// Records which backend ended up loaded. Call after the first successful model load.
    /// </summary>
    public static void RecordSelectedFrom(string nativeLibraryPath)
    {
        var name = Path.GetFileName(nativeLibraryPath);
        var folder = Path.GetFileName(Path.GetDirectoryName(nativeLibraryPath) ?? "");
        s_selected = string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}";
    }
}
