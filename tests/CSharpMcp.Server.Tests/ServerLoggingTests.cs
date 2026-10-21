using CSharpMcp.Infrastructure;
using Serilog;
using Xunit;

namespace CSharpMcp.Tests;

/// <summary>
/// Verifies the durable local logging policy independently from the MCP transport.
/// </summary>
public sealed class ServerLoggingTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "CSharpMCP.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveLogDirectorySupportsAnExplicitAbsoluteOverride()
    {
        var configuredDirectory = Path.Combine(testRoot, "configured-logs");

        var resolvedDirectory = ServerLogging.ResolveLogDirectory(configuredDirectory);

        Assert.Equal(Path.GetFullPath(configuredDirectory), resolvedDirectory);
    }

    [Fact]
    public void ResolveLogDirectoryRejectsARelativeOverride()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => ServerLogging.ResolveLogDirectory("relative-logs"));

        Assert.Contains(ServerLogging.LogDirectoryEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableLogDirectoryDoesNotPreventLoggingToStderr()
    {
        Directory.CreateDirectory(testRoot);
        var blockedDirectory = Path.Combine(testRoot, "blocked");
        File.WriteAllText(blockedDirectory, "A file prevents directory creation.");

        var configuration = new LoggerConfiguration();
        ServerLogging.Configure(configuration, blockedDirectory);
        using var logger = configuration.CreateLogger();
        logger.Warning("Still available after a file logging failure");
    }

    [Fact]
    public void FileSinkWritesDailyLogsAndDeletesFilesOlderThanThirtyDays()
    {
        Directory.CreateDirectory(testRoot);
        var staleTimestamp = DateTime.Now.AddDays(-(ServerLogging.RetentionDays + 1));
        var retainedTimestamp = DateTime.Now.AddDays(-(ServerLogging.RetentionDays - 1));
        var staleLog = CreateHistoricalLog(staleTimestamp);
        var retainedLog = CreateHistoricalLog(retainedTimestamp);

        var configuration = new LoggerConfiguration();
        ServerLogging.Configure(configuration, testRoot);
        using (var logger = configuration.CreateLogger())
        {
            logger.Information("Logging retention test {Marker}", "written");
        }

        Assert.False(File.Exists(staleLog));
        Assert.True(File.Exists(retainedLog));
        var currentLog = Assert.Single(
            Directory.GetFiles(testRoot, "csharpmcp-*.log"),
            path => path != retainedLog);
        Assert.Contains("Logging retention test written", File.ReadAllText(currentLog), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (!Directory.Exists(testRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (IOException)
        {
            // File-system filters can release new log files shortly after the sink is disposed.
        }
        catch (UnauthorizedAccessException)
        {
            // Antivirus scanning can transiently hold generated log files on Windows.
        }
    }

    private string CreateHistoricalLog(DateTime timestamp)
    {
        var path = Path.Combine(testRoot, $"csharpmcp-{timestamp:yyyyMMdd}.log");
        File.WriteAllText(path, "historical log");
        File.SetLastWriteTime(path, timestamp);
        return path;
    }
}
