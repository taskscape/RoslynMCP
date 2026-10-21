using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CSharpMcp.Workspace;

/// <summary>
/// Maintains explicit session and persistent trust for directories containing Roslyn workspaces.
/// </summary>
internal sealed class SolutionTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly HashSet<string> sessionRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> persistentRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly string storePath;
    private readonly ILogger<SolutionTrustStore> logger;

    /// <summary>
    /// Initializes the trust store at the per-user application-data location.
    /// </summary>
    public SolutionTrustStore(ILogger<SolutionTrustStore> logger)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CSharpMCP",
                "trusted-paths.json"),
            logger)
    {
    }

    /// <summary>
    /// Initializes a trust store at an explicit path, primarily for isolated tests.
    /// </summary>
    internal SolutionTrustStore(string storePath, ILogger<SolutionTrustStore> logger)
    {
        this.storePath = Path.GetFullPath(storePath);
        this.logger = logger;
        LoadPersistentRoots();
    }

    /// <summary>
    /// Adds a workspace root to session trust and optionally persists the decision.
    /// </summary>
    public TrustEntry Trust(string workspaceOrDirectoryPath, bool persist)
    {
        var root = NormalizeRoot(workspaceOrDirectoryPath, requireExisting: true);
        lock (syncRoot)
        {
            sessionRoots.Add(root);
            if (persist && persistentRoots.Add(root))
            {
                try
                {
                    SavePersistentRoots();
                }
                catch (Exception exception)
                {
                    // A failed write must not be reported as persisted on the next attempt.
                    persistentRoots.Remove(root);
                    logger.LogError(exception, "Could not persist trust at {TrustStorePath}; session trust remains available", storePath);
                    throw;
                }
            }

            return new TrustEntry(root, sessionRoots.Contains(root), persistentRoots.Contains(root));
        }
    }

    /// <summary>
    /// Removes matching session and persistent trust entries.
    /// </summary>
    public TrustRevocation Revoke(string workspaceOrDirectoryPath)
    {
        var root = NormalizeRoot(workspaceOrDirectoryPath, requireExisting: false);
        lock (syncRoot)
        {
            var sessionRemoved = sessionRoots.Remove(root);
            var persistentRemoved = persistentRoots.Remove(root);
            if (persistentRemoved)
            {
                try
                {
                    SavePersistentRoots();
                }
                catch (Exception exception)
                {
                    persistentRoots.Add(root);
                    if (sessionRemoved)
                    {
                        sessionRoots.Add(root);
                    }

                    logger.LogError(exception, "Could not persist trust revocation at {TrustStorePath}; revocation can be retried", storePath);
                    throw;
                }
            }

            return new TrustRevocation(root, sessionRemoved, persistentRemoved);
        }
    }

    /// <summary>
    /// Lists all trust roots without exposing other process or environment state.
    /// </summary>
    public IReadOnlyList<TrustEntry> List()
    {
        lock (syncRoot)
        {
            return sessionRoots.Concat(persistentRoots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(root => new TrustEntry(root, sessionRoots.Contains(root), persistentRoots.Contains(root)))
                .ToArray();
        }
    }

    /// <summary>
    /// Throws when a workspace is outside every explicitly trusted root.
    /// </summary>
    public void EnsureTrusted(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        lock (syncRoot)
        {
            if (sessionRoots.Concat(persistentRoots).Any(root => IsWithinRoot(fullPath, root)))
            {
                return;
            }
        }

        throw new UntrustedWorkspaceException(fullPath);
    }

    /// <summary>
    /// Reports whether a workspace is covered by a session or persistent trust root.
    /// </summary>
    public bool IsTrusted(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        lock (syncRoot)
        {
            return sessionRoots.Concat(persistentRoots).Any(root => IsWithinRoot(fullPath, root));
        }
    }

    private void LoadPersistentRoots()
    {
        if (!File.Exists(storePath))
        {
            return;
        }

        try
        {
            var model = JsonSerializer.Deserialize<TrustStoreModel>(File.ReadAllText(storePath), JsonOptions);
            foreach (var root in model?.Roots ?? [])
            {
                persistentRoots.Add(NormalizeRoot(root, requireExisting: false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            persistentRoots.Clear();
            logger.LogWarning(exception, "Could not load the CSharpMCP trust store at {TrustStorePath}; no persistent trust was granted. Session trust is available", storePath);
        }
    }

    private void SavePersistentRoots()
    {
        var directory = Path.GetDirectoryName(storePath)
            ?? throw new InvalidOperationException("The trust-store path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(storePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new TrustStoreModel(persistentRoots.Order(StringComparer.OrdinalIgnoreCase).ToArray()), JsonOptions));
            File.Move(temporaryPath, storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeRoot(string workspaceOrDirectoryPath, bool requireExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceOrDirectoryPath);
        var fullPath = Path.GetFullPath(workspaceOrDirectoryPath);
        if (Directory.Exists(fullPath))
        {
            return Path.TrimEndingDirectorySeparator(fullPath);
        }

        if (File.Exists(fullPath) || !requireExisting && Path.HasExtension(fullPath))
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The workspace path has no parent directory.", nameof(workspaceOrDirectoryPath)));
        }

        if (requireExisting)
        {
            throw new DirectoryNotFoundException($"The trust target '{fullPath}' does not exist.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Equals(".", StringComparison.Ordinal) ||
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private sealed record TrustStoreModel(IReadOnlyList<string> Roots);
}

/// <summary>
/// Describes one normalized trusted root and its lifetime.
/// </summary>
internal sealed record TrustEntry(string RootPath, bool SessionTrusted, bool Persisted);

/// <summary>
/// Describes which trust scopes were removed.
/// </summary>
internal sealed record TrustRevocation(string RootPath, bool SessionTrustRemoved, bool PersistentTrustRemoved);

/// <summary>
/// Indicates that a Roslyn workspace must be explicitly trusted before it can be evaluated.
/// </summary>
internal sealed class UntrustedWorkspaceException : InvalidOperationException
{
    /// <summary>
    /// Initializes an actionable error without exposing unrelated environment state.
    /// </summary>
    public UntrustedWorkspaceException(string workspacePath)
        : base($"Workspace '{workspacePath}' is not trusted. Call trust_solution for this solution or its repository directory before loading it.")
    {
    }
}
