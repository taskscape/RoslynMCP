using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace CSharpMcp.Workspace;

/// <summary>
/// Caches loaded MSBuild workspaces and invalidates them when relevant repository files change.
/// </summary>
internal sealed class SolutionWorkspaceCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<WorkspaceEntry>> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SolutionWorkspaceCache> logger;
    private readonly SolutionTrustStore trustStore;
    private bool disposed;

    /// <summary>
    /// Initializes a workspace cache.
    /// </summary>
    public SolutionWorkspaceCache(ILogger<SolutionWorkspaceCache> logger, SolutionTrustStore trustStore)
    {
        this.logger = logger;
        this.trustStore = trustStore;
    }

    /// <summary>
    /// Gets a current immutable solution snapshot for a solution, solution filter, or project path.
    /// </summary>
    public async Task<WorkspaceSnapshot> GetSnapshotAsync(string inputPath, CancellationToken cancellationToken)
    {
        return await GetSnapshotAsync(inputPath, WorkspaceLoadOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a current immutable solution snapshot for one explicit MSBuild evaluation.
    /// </summary>
    public async Task<WorkspaceSnapshot> GetSnapshotAsync(
        string inputPath,
        WorkspaceLoadOptions loadOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        trustStore.EnsureTrusted(inputPath);

        var fullPath = ValidatePath(inputPath);
        var normalizedOptions = loadOptions.Normalize();
        var cacheKey = $"{fullPath}|{normalizedOptions.Configuration}|{normalizedOptions.TargetFramework}";
        var lazyEntry = entries.GetOrAdd(cacheKey, _ => new Lazy<WorkspaceEntry>(() => new WorkspaceEntry(fullPath, normalizedOptions, logger)));
        WorkspaceEntry entry;
        try
        {
            entry = lazyEntry.Value;
        }
        catch (Exception exception)
        {
            entries.TryRemove(new KeyValuePair<string, Lazy<WorkspaceEntry>>(cacheKey, lazyEntry));
            logger.LogError(exception, "Could not initialize workspace monitoring for {WorkspacePath}; the next request can retry", fullPath);
            throw;
        }

        return await entry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets operational metadata for a loaded cache entry without exposing environment variables or command lines.
    /// </summary>
    public async Task<WorkspaceCacheHealth> GetHealthAsync(string inputPath, CancellationToken cancellationToken)
    {
        return await GetHealthAsync(inputPath, WorkspaceLoadOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets operational metadata for one explicit MSBuild evaluation.
    /// </summary>
    public async Task<WorkspaceCacheHealth> GetHealthAsync(
        string inputPath,
        WorkspaceLoadOptions loadOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        trustStore.EnsureTrusted(inputPath);

        var fullPath = ValidatePath(inputPath);
        var normalizedOptions = loadOptions.Normalize();
        var cacheKey = $"{fullPath}|{normalizedOptions.Configuration}|{normalizedOptions.TargetFramework}";
        var lazyEntry = entries.GetOrAdd(cacheKey, _ => new Lazy<WorkspaceEntry>(() => new WorkspaceEntry(fullPath, normalizedOptions, logger)));
        WorkspaceEntry entry;
        try
        {
            entry = lazyEntry.Value;
        }
        catch (Exception exception)
        {
            entries.TryRemove(new KeyValuePair<string, Lazy<WorkspaceEntry>>(cacheKey, lazyEntry));
            logger.LogError(exception, "Could not initialize workspace monitoring for {WorkspacePath}; the next request can retry", fullPath);
            throw;
        }
        var reloadedForThisCheck = entry.IsInvalidated;
        var snapshot = await entry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return entry.GetHealth(snapshot, entries.Count, reloadedForThisCheck);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var entry in entries.Values)
        {
            if (entry.IsValueCreated)
            {
                entry.Value.Dispose();
            }
        }

        entries.Clear();
    }

    /// <summary>
    /// Validates and normalizes the caller-supplied workspace path.
    /// </summary>
    private static string ValidatePath(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The requested solution or project does not exist.", fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (extension is not (".sln" or ".slnx" or ".slnf" or ".csproj"))
        {
            throw new ArgumentException("The path must identify a .sln, .slnx, .slnf, or .csproj file.", nameof(inputPath));
        }

        return fullPath;
    }

    /// <summary>
    /// Owns one load gate, workspace, change watcher, and immutable solution snapshot.
    /// </summary>
    private sealed class WorkspaceEntry : IDisposable
    {
        private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".slnf", ".json", ".config", ".editorconfig"
        };

        private readonly string inputPath;
        private readonly WorkspaceLoadOptions loadOptions;
        private readonly ILogger logger;
        private readonly SemaphoreSlim loadGate = new(1, 1);
        private readonly FileSystemWatcher watcher;
        private MSBuildWorkspace? workspace;
        private WorkspaceSnapshot? snapshot;
        private int invalidated = 1;
        private volatile bool monitoringFailed;
        private long reloadCount;
        private TimeSpan lastLoadDuration;
        private DateTimeOffset? lastInvalidatedAt;
        private string? lastInvalidationReason;
        private string? lastInvalidatedPath;
        private bool disposed;

        public WorkspaceEntry(string inputPath, WorkspaceLoadOptions loadOptions, ILogger logger)
        {
            this.inputPath = inputPath;
            this.loadOptions = loadOptions;
            this.logger = logger;

            var root = Path.GetDirectoryName(inputPath)
                ?? throw new InvalidOperationException("The workspace path has no parent directory.");

            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = false
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watcher.Dispose();
                throw;
            }
        }

        public bool IsInvalidated => monitoringFailed || Interlocked.CompareExchange(ref invalidated, 0, 0) != 0;

        public async Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            await loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (snapshot is not null && !IsInvalidated)
                {
                    return snapshot;
                }

                // Clear before loading so changes arriving during evaluation remain pending.
                Interlocked.Exchange(ref invalidated, 0);
                workspace?.Dispose();
                var loadDiagnostics = new List<string>();
                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Configuration"] = loadOptions.Configuration
                };
                if (loadOptions.TargetFramework is not null)
                {
                    properties["TargetFramework"] = loadOptions.TargetFramework;
                }

                workspace = MSBuildWorkspace.Create(properties);
                workspace.RegisterWorkspaceFailedHandler(args =>
                {
                    lock (loadDiagnostics)
                    {
                        loadDiagnostics.Add($"{args.Diagnostic.Kind}: {args.Diagnostic.Message}");
                        logger.LogWarning("Workspace {WorkspacePath}: {DiagnosticKind}: {DiagnosticMessage}",
                            inputPath, args.Diagnostic.Kind, args.Diagnostic.Message);
                    }
                });

                logger.LogInformation("Loading Roslyn workspace from {WorkspacePath}", inputPath);
                var stopwatch = Stopwatch.StartNew();
                var solution = Path.GetExtension(inputPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? (await workspace.OpenProjectAsync(inputPath, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution
                    : await workspace.OpenSolutionAsync(inputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                snapshot = new WorkspaceSnapshot(
                    solution,
                    loadDiagnostics.ToArray(),
                    DateTimeOffset.UtcNow,
                    loadOptions.Configuration,
                    loadOptions.TargetFramework);
                lastLoadDuration = stopwatch.Elapsed;
                Interlocked.Increment(ref reloadCount);
                logger.LogInformation("Loaded workspace {WorkspacePath} in {ElapsedMilliseconds} ms with {DiagnosticCount} diagnostics",
                    inputPath, stopwatch.Elapsed.TotalMilliseconds, loadDiagnostics.Count);

                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref invalidated, 1);
                logger.LogInformation("Workspace load cancelled for {WorkspacePath}; the next request can retry", inputPath);
                throw;
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref invalidated, 1);
                logger.LogError(exception, "Workspace load failed for {WorkspacePath}; the next request can retry", inputPath);
                throw;
            }
            finally
            {
                loadGate.Release();
            }
        }

        /// <summary>
        /// Creates a point-in-time, credential-safe view of this cache entry.
        /// </summary>
        public WorkspaceCacheHealth GetHealth(WorkspaceSnapshot currentSnapshot, int cacheEntryCount, bool reloadedForThisCheck)
        {
            return new WorkspaceCacheHealth(
                inputPath,
                Path.GetDirectoryName(inputPath)!,
                IsInvalidated,
                reloadedForThisCheck,
                lastInvalidationReason,
                lastInvalidatedPath,
                lastInvalidatedAt,
                lastLoadDuration,
                Interlocked.Read(ref reloadCount),
                cacheEntryCount,
                currentSnapshot);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            watcher.Dispose();
            workspace?.Dispose();
            loadGate.Dispose();
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            lastInvalidationReason = "WatcherError";
            lastInvalidatedPath = null;
            lastInvalidatedAt = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref invalidated, 1);
            logger.LogError(args.GetException(), "Workspace watcher failed for {WorkspacePath}; cache will reload", inputPath);
            // After overflow the watcher continues; other errors may stop event delivery.
            // Never reuse a snapshot indefinitely after monitoring has failed.
            monitoringFailed = true;
        }

        private void OnFileRenamed(object sender, RenamedEventArgs args)
        {
            if (IsRelevantPath(args.OldFullPath, includeUnknown: true))
            {
                OnFileChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Renamed,
                    Path.GetDirectoryName(args.OldFullPath)!, Path.GetFileName(args.OldFullPath)));
            }

            OnFileChanged(sender, args);
        }

        private bool IsRelevantPath(string path, bool includeUnknown = false)
        {
            var relativePath = Path.GetRelativePath(Path.GetDirectoryName(inputPath)!, path);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Restore metadata changes references and must be observed even under obj.
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            // Directory moves/deletes must invalidate all contained documents too.
            return includeUnknown || extension.Length == 0 || Directory.Exists(path) || WatchedExtensions.Contains(extension);
        }

        private void OnFileChanged(object sender, FileSystemEventArgs args)
        {
            if (IsRelevantPath(args.FullPath, includeUnknown: args.ChangeType is WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed))
            {
                lastInvalidationReason = args.ChangeType.ToString();
                lastInvalidatedPath = args.FullPath;
                lastInvalidatedAt = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref invalidated, 1);
            }
        }
    }
}

/// <summary>
/// Represents one immutable loaded solution and its load-time diagnostics.
/// </summary>
internal sealed record WorkspaceSnapshot(
    Solution Solution,
    IReadOnlyList<string> LoadDiagnostics,
    DateTimeOffset LoadedAt,
    string Configuration,
    string? TargetFramework);

/// <summary>
/// Selects one deterministic MSBuild configuration and optional target-framework evaluation.
/// </summary>
internal sealed record WorkspaceLoadOptions(string Configuration, string? TargetFramework)
{
    public static WorkspaceLoadOptions Default { get; } = new("Debug", null);

    /// <summary>
    /// Normalizes properties before they form part of the workspace cache identity.
    /// </summary>
    public WorkspaceLoadOptions Normalize()
    {
        var configuration = string.IsNullOrWhiteSpace(Configuration) ? "Debug" : Configuration.Trim();
        var targetFramework = string.IsNullOrWhiteSpace(TargetFramework) ? null : TargetFramework.Trim();
        return new WorkspaceLoadOptions(configuration, targetFramework);
    }
}

/// <summary>
/// Reports credential-safe operational state for one cached workspace.
/// </summary>
internal sealed record WorkspaceCacheHealth(
    string InputPath,
    string RootPath,
    bool IsInvalidated,
    bool ReloadedForThisCheck,
    string? LastInvalidationReason,
    string? LastInvalidatedPath,
    DateTimeOffset? LastInvalidatedAt,
    TimeSpan LastLoadDuration,
    long ReloadCount,
    int CacheEntryCount,
    WorkspaceSnapshot Snapshot);
