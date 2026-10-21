using Serilog;
using Serilog.Events;

namespace CSharpMcp.Infrastructure;

/// <summary>
/// Configures stderr and durable per-user logging for the stdio server.
/// </summary>
internal static class ServerLogging
{
    internal const int RetentionDays = 30;
    internal const string LogDirectoryEnvironmentVariable = "CSHARPMCP_LOG_DIRECTORY";
    internal const string LogFileName = "csharpmcp-.log";

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}";

    private static readonly TimeSpan RetainedFileTimeLimit = TimeSpan.FromDays(RetentionDays);

    /// <summary>
    /// Resolves the log directory from an optional environment override or the user's local application data.
    /// </summary>
    internal static string ResolveLogDirectory(string? configuredDirectory = null)
    {
        configuredDirectory ??= Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CSharpMCP",
                "logs"));
        }

        var expandedDirectory = Environment.ExpandEnvironmentVariables(configuredDirectory);
        if (!Path.IsPathFullyQualified(expandedDirectory))
        {
            throw new ArgumentException(
                $"{LogDirectoryEnvironmentVariable} must contain an absolute path.",
                nameof(configuredDirectory));
        }

        return Path.GetFullPath(expandedDirectory);
    }

    /// <summary>
    /// Adds a stderr sink for MCP hosts and a shared daily file sink retained for thirty days.
    /// </summary>
    internal static void Configure(LoggerConfiguration configuration, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        configuration
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: OutputTemplate);

        try
        {
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, LogFileName);
            configuration.WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: RetainedFileTimeLimit,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: OutputTemplate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or unavailable log directory must not disable MCP discovery.
            Console.Error.WriteLine($"CSharpMCP file logging unavailable at '{logDirectory}'; continuing with stderr: {exception}");
        }
    }
}
