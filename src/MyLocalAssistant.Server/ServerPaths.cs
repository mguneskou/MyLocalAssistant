namespace MyLocalAssistant.Server;

/// <summary>
/// Resolves all server folders.
/// </summary>
/// <remarks>
/// <para><see cref="AppDirectory"/> always points at the executable folder
/// (binaries, embedded assets, sample-config).</para>
/// <para><see cref="StateDirectory"/> is where every piece of <i>persistent</i>
/// state lives (models, database, vectors, plug-ins, logs, settings).
/// For a Velopack install (binaries land in <c>%LocalAppData%\MyLocalAssistant\current\</c>
/// and the whole <c>current\</c> folder is replaced atomically on every update)
/// state is stored at the sibling <c>%LocalAppData%\MyLocalAssistant\state\</c>
/// so it survives upgrades. For local dev runs (binaries live in <c>bin\Debug\…</c>)
/// state stays next to the executable, matching the long-standing behavior tests rely on.</para>
/// </remarks>
public static class ServerPaths
{
    public static string AppDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>True when the binaries live under a Velopack-managed <c>current\</c> folder.</summary>
    public static bool IsVelopackInstall { get; } = DetectVelopackInstall(AppDirectory);

    /// <summary>Root for all persistent state. Survives application updates when running from a Velopack install.</summary>
    public static string StateDirectory { get; } = ResolveStateDirectory(AppDirectory, IsVelopackInstall);

    public static string ModelsDirectory    { get; } = Path.Combine(StateDirectory, "models");
    public static string DataDirectory      { get; } = Path.Combine(StateDirectory, "data");
    public static string VectorsDirectory   { get; } = Path.Combine(StateDirectory, "vectors");
    public static string IngestionDirectory { get; } = Path.Combine(StateDirectory, "ingestion");
    public static string LogsDirectory      { get; } = Path.Combine(StateDirectory, "logs");
    public static string ConfigDirectory    { get; } = Path.Combine(StateDirectory, "config");
    public static string OutputDirectory    { get; } = Path.Combine(StateDirectory, "output");

    public static string DatabasePath     { get; } = Path.Combine(DataDirectory, "app.db");
    public static string SettingsFilePath { get; } = Path.Combine(ConfigDirectory, "server.json");

    private static bool _ensured;
    private static readonly object _ensureLock = new();

    public static void EnsureCreated()
    {
        lock (_ensureLock)
        {
            Directory.CreateDirectory(StateDirectory);
            Directory.CreateDirectory(ModelsDirectory);
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(VectorsDirectory);
            Directory.CreateDirectory(IngestionDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(OutputDirectory);

            if (!_ensured)
            {
                _ensured = true;
                if (IsVelopackInstall) TryMigrateLegacyState();
            }
        }
    }

    private static bool DetectVelopackInstall(string appDir)
    {
        // Velopack lays out: %LocalAppData%\<PackId>\current\<exe>. Recognize that and only that.
        var trimmed = appDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), "current", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Environment variable that, when set, overrides where persistent state lives.
    /// Used by dev / F5 runs so state (config, DB, downloaded models, vectors) lands in a
    /// stable folder OUTSIDE <c>bin\</c> and therefore survives a Visual Studio Clean/Rebuild.
    /// Supports embedded <c>%VAR%</c> tokens (e.g. <c>%LOCALAPPDATA%\MyLocalAssistant\state</c>).
    /// </summary>
    public const string StateDirEnvVar = "MYLOCALASSISTANT_STATE_DIR";

    private static string ResolveStateDirectory(string appDir, bool isVelopack)
    {
        // Explicit override always wins — this is how a from-source run keeps its state
        // in a fixed location instead of next to the exe inside bin\.
        var overrideDir = Environment.GetEnvironmentVariable(StateDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Environment.ExpandEnvironmentVariables(overrideDir.Trim());

        if (!isVelopack) return appDir;
        var trimmed = appDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(trimmed)?.FullName;
        return parent is null ? appDir : Path.Combine(parent, "state");
    }

    /// <summary>
    /// One-shot migration for users upgrading from a build that stored state next to the binaries
    /// (inside <c>current\</c>). Moves any pre-existing folders to the new state root.
    /// Best-effort: never throws; this runs before Serilog is initialized.
    /// </summary>
    private static void TryMigrateLegacyState()
    {
        string[] names = { "models", "data", "vectors", "ingestion", "logs", "config", "output" };

        // Source 1: legacy folders still living inside the freshly-installed `current\` (only happens on
        // the very first upgrade after an unclean swap, but covered for safety).
        TryMigrateFrom(AppDirectory, names);

        // Source 2: Velopack often leaves the previous unpacked install at <root>\app-X.Y.Z\ until the
        // next cleanup pass. Scan the parent for the newest such folder and migrate from it. This is
        // what rescues folks upgrading from 2.1.1 -> 2.1.2 since 2.1.1 wrote everything inside current\.
        try
        {
            var trimmed = AppDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(trimmed)?.FullName;
            if (parent is null) return;
            var candidates = Directory.EnumerateDirectories(parent, "app-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var legacyRoot in candidates)
            {
                if (TryMigrateFrom(legacyRoot, names)) break;
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static bool TryMigrateFrom(string legacyRoot, string[] names)
    {
        var movedAnything = false;
        foreach (var name in names)
        {
            try
            {
                var legacy = Path.Combine(legacyRoot, name);
                var modern = Path.Combine(StateDirectory, name);
                if (!HasAnyContent(legacy)) continue;
                if (HasAnyContent(modern)) continue;
                MoveDirectoryContents(legacy, modern);
                movedAnything = true;
            }
            catch
            {
                // Migration is best-effort; the user can copy folders manually if needed.
            }
        }
        return movedAnything;
    }

    private static bool HasAnyContent(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    private static void MoveDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var dest = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(dest)) File.Move(file, dest);
        }
        foreach (var sub in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var dest = Path.Combine(destination, Path.GetFileName(sub));
            if (Directory.Exists(dest)) MoveDirectoryContents(sub, dest);
            else Directory.Move(sub, dest);
        }
    }

}
