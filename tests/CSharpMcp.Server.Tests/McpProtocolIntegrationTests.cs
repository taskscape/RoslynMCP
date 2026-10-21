using System.Collections.Concurrent;
using System.Text.Json;
using CSharpMcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CSharpMcp.Tests;

/// <summary>
/// Verifies the MCP 2.0 stdio contract exposed by the executable server.
/// </summary>
public sealed class McpProtocolIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoggingFailureDoesNotPreventDiscoveryOrSubsequentRequests(bool relativeDirectory)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "CSharpMCP.LoggingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var blockedDirectory = Path.Combine(testRoot, "blocked");
        File.WriteAllText(blockedDirectory, "This file prevents log directory creation.");
        var standardError = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "logging-fallback-test",
            Command = "dotnet",
            Arguments = [typeof(McpToolResponse).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CSHARPMCP_LOG_DIRECTORY"] = relativeDirectory ? "relative-logs" : blockedDirectory,
                ["CSHARPMCP_TOOL_GROUPS"] = ""
            },
            StandardErrorLines = line => standardError.Enqueue(line),
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
        {
            Assert.Equal(32, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
            var error = await client.CallToolAsync("solution_overview", new Dictionary<string, object?>
            {
                ["workspacePath"] = Path.Combine(testRoot, "missing.slnx")
            }, cancellationToken: timeout.Token);
            Assert.True(error.IsError);
            Assert.Equal(32, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
        }

        Assert.Contains(standardError, line => line.Contains(relativeDirectory
            ? "Invalid CSharpMCP log directory" : "file logging unavailable", StringComparison.Ordinal));
        Assert.Contains(standardError, line => line.Contains("solution_overview", StringComparison.Ordinal) &&
            (line.Contains("failed", StringComparison.Ordinal) || line.Contains("error", StringComparison.Ordinal)));
        Directory.Delete(testRoot, recursive: true);
    }

    /// <summary>
    /// Proves that the optional profile advertises and executes both feature-gated tools.
    /// </summary>
    [Fact]
    public async Task AllFeatureProfileAdvertisesAndExecutesOptionalTools()
    {
        var serverAssemblyPath = typeof(McpToolResponse).Assembly.Location;
        var solutionPath = FindSolutionPath(serverAssemblyPath);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "csharp-roslyn-all-features-integration-test",
            Command = "dotnet",
            Arguments = [serverAssemblyPath],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CSHARPMCP_TOOL_GROUPS"] = "all",
                ["CSHARPMCP_LOG_DIRECTORY"] = GetTestLogDirectory()
            },
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        Assert.Equal(34, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "api_compatibility");
        Assert.Contains(tools, tool => tool.Name == "architecture_rule_check");

        var trustResult = await client.CallToolAsync(
            "trust_solution",
            new Dictionary<string, object?>
            {
                ["workspacePath"] = solutionPath,
                ["persist"] = false
            },
            cancellationToken: timeout.Token);
        Assert.False(trustResult.IsError ?? false);

        var apiResult = await client.CallToolAsync(
            "api_compatibility",
            new Dictionary<string, object?>
            {
                ["workspacePath"] = solutionPath,
                ["projectName"] = "CSharpMcp.Server",
                ["includeCurrentSurface"] = true,
                ["maxResults"] = 1
            },
            cancellationToken: timeout.Token);
        Assert.False(apiResult.IsError ?? false);
        Assert.True(Assert.IsType<JsonElement>(apiResult.StructuredContent)
            .GetProperty("data").TryGetProperty("currentApiCount", out _));

        var architectureResult = await client.CallToolAsync(
            "architecture_rule_check",
            new Dictionary<string, object?>
            {
                ["workspacePath"] = solutionPath,
                ["projectName"] = "CSharpMcp.Server",
                ["rules"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "tools-do-not-depend-on-analysis",
                        ["fromNamespace"] = "CSharpMcp.Tools",
                        ["forbid"] = new[] { "CSharpMcp.Analysis" }
                    }
                },
                ["maxResults"] = 5
            },
            cancellationToken: timeout.Token);
        Assert.False(architectureResult.IsError ?? false);
        Assert.True(Assert.IsType<JsonElement>(architectureResult.StructuredContent)
            .GetProperty("data").TryGetProperty("violations", out _));
    }

    /// <summary>
    /// Proves discovery-first negotiation, useful schemas, direct structured output, and progress on the wire.
    /// </summary>
    [Fact]
    public async Task StdioClientNegotiatesMcp2AndReceivesStructuredTools()
    {
        var serverAssemblyPath = typeof(McpToolResponse).Assembly.Location;
        var solutionPath = FindSolutionPath(serverAssemblyPath);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "csharp-roslyn-integration-test",
            Command = "dotnet",
            Arguments = [serverAssemblyPath],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CSHARPMCP_LOG_DIRECTORY"] = GetTestLogDirectory()
            },
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
        Assert.Equal("csharp-roslyn", client.ServerInfo.Name);
        Assert.Equal("C# Roslyn Code Intelligence", client.ServerInfo.Title);
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInfo.Description));

        var catalogResult = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);
        Assert.Equal(CacheScope.Public, catalogResult.CacheScope);
        Assert.Equal(TimeSpan.FromMinutes(10), catalogResult.TimeToLive);
        Assert.Equal(32, catalogResult.Tools.Count);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(32, tools.Count);
        var catalogCharacters = JsonSerializer.Serialize(tools.Select(tool => tool.ProtocolTool)).Length;
        Assert.InRange(catalogCharacters, 1, 65_000);
        Assert.DoesNotContain(tools, tool => tool.Name == "api_compatibility");
        Assert.DoesNotContain(tools, tool => tool.Name == "architecture_rule_check");
        Assert.All(tools, tool =>
        {
            var schema = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
            var dataProperties = schema.GetProperty("properties").GetProperty("data").GetProperty("properties");
            Assert.True(dataProperties.EnumerateObject().Any(), $"{tool.Name} must advertise tool-specific data fields.");

            var inputSchema = Assert.IsType<JsonElement>(tool.ProtocolTool.InputSchema);
            if (!inputSchema.TryGetProperty("properties", out var inputProperties))
            {
                return;
            }

            foreach (var property in inputProperties.EnumerateObject()
                         .Where(property => IsReturnedItemLimit(property.Name) &&
                                            property.Value.TryGetProperty("default", out _)))
            {
                Assert.InRange(property.Value.GetProperty("default").GetInt32(), 1, 50);
            }
        });
        Assert.True(GetDataSchema(tools, "find_references").TryGetProperty("references", out _));
        Assert.True(GetDataSchema(tools, "project_dependencies").TryGetProperty("namespaceEdges", out _));
        Assert.True(GetDataSchema(tools, "rename_preview").TryGetProperty("nextCursor", out _));

        var trustedPathsTool = Assert.Single(tools, tool => tool.Name == "list_trusted_paths");
        Assert.Equal("List trusted paths", trustedPathsTool.Title);
        Assert.False(trustedPathsTool.ProtocolTool.Annotations?.OpenWorldHint);

        var outputSchema = Assert.IsType<JsonElement>(trustedPathsTool.ProtocolTool.OutputSchema);
        var schemaProperties = outputSchema.GetProperty("properties");
        Assert.True(schemaProperties.TryGetProperty("data", out var dataSchema));
        var dataTypes = dataSchema.GetProperty("type").ValueKind == JsonValueKind.Array
            ? dataSchema.GetProperty("type").EnumerateArray().Select(value => value.GetString()).ToArray()
            : [dataSchema.GetProperty("type").GetString()];
        Assert.Contains("object", dataTypes);
        Assert.Equal("array", dataSchema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.True(schemaProperties.TryGetProperty("metadata", out _));
        Assert.False(schemaProperties.TryGetProperty("result", out _));
        var metadataSchema = schemaProperties.GetProperty("metadata").GetProperty("properties");
        Assert.True(metadataSchema.TryGetProperty("kind", out _));
        Assert.True(metadataSchema.TryGetProperty("returned", out _));
        Assert.True(metadataSchema.TryGetProperty("truncated", out _));
        Assert.True(metadataSchema.TryGetProperty("nextCursor", out _));

        var hiddenToolResult = await client.CallToolAsync(
            "api_compatibility",
            new Dictionary<string, object?>
            {
                ["workspacePath"] = solutionPath
            },
            cancellationToken: timeout.Token);
        Assert.True(hiddenToolResult.IsError);

        var trustResult = await client.CallToolAsync(
            "trust_solution",
            new Dictionary<string, object?>
            {
                ["workspacePath"] = solutionPath,
                ["persist"] = false
            },
            cancellationToken: timeout.Token);
        Assert.False(trustResult.IsError ?? false);

        var progress = new RecordingProgress();
        var overviewResult = await client.CallToolAsync(
            "solution_overview",
            new Dictionary<string, object?>
            {
                ["workspacePath"] = solutionPath,
                ["maxProjects"] = 10
            },
            progress,
            cancellationToken: timeout.Token);

        Assert.False(overviewResult.IsError ?? false);
        var structuredContent = Assert.IsType<JsonElement>(overviewResult.StructuredContent);
        Assert.Equal(JsonValueKind.Object, structuredContent.ValueKind);
        Assert.True(structuredContent.TryGetProperty("data", out _));
        var metadata = structuredContent.GetProperty("metadata");
        Assert.True(metadata.TryGetProperty("returned", out _));
        Assert.True(metadata.TryGetProperty("truncated", out _));
        Assert.False(structuredContent.TryGetProperty("result", out _));
        Assert.Contains(overviewResult.Content, block => block is TextContentBlock);
        Assert.Collection(
            progress.Values,
            first => Assert.Equal(0, first.Progress),
            last => Assert.Equal(1, last.Progress));
    }

    private static string GetTestLogDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "CSharpMCP.Tests", "ServerLogs");
    }

    /// <summary>
    /// Returns the tool-specific data properties from one advertised output schema.
    /// </summary>
    private static JsonElement GetDataSchema(IList<McpClientTool> tools, string toolName)
    {
        var tool = Assert.Single(tools, candidate => candidate.Name == toolName);
        var schema = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
        return schema.GetProperty("properties").GetProperty("data").GetProperty("properties");
    }

    /// <summary>
    /// Identifies parameters that directly bound returned collection sizes rather than internal scan work.
    /// </summary>
    private static bool IsReturnedItemLimit(string parameterName)
    {
        return parameterName is "maxResults" or "maxProjects" or "maxFrames" or
            "maxCandidates" or "maxResultsPerSection" or "maxReferencesPerSymbol";
    }

    /// <summary>
    /// Locates the repository solution without relying on the test runner's working directory.
    /// </summary>
    private static string FindSolutionPath(string serverAssemblyPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(serverAssemblyPath)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "CSharpMCP.slnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate CSharpMCP.slnx from the server assembly path.");
    }

    /// <summary>
    /// Records MCP progress synchronously so the integration assertion is deterministic.
    /// </summary>
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        private readonly ConcurrentQueue<ProgressNotificationValue> values = new();

        /// <summary>
        /// Gets progress values in receive order.
        /// </summary>
        public IReadOnlyList<ProgressNotificationValue> Values => [.. values];

        /// <summary>
        /// Records one request-scoped progress notification.
        /// </summary>
        public void Report(ProgressNotificationValue value)
        {
            values.Enqueue(value);
        }
    }
}
