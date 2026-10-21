using CSharpMcp.Analysis;
using CSharpMcp.Infrastructure;
using CSharpMcp.Tools;
using CSharpMcp.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Serilog;

// Configure diagnostics before SDK discovery or host construction can fail.
Serilog.Debugging.SelfLog.Enable(TextWriter.Synchronized(Console.Error));
try
{
    string logDirectory;
    try
    {
        logDirectory = ServerLogging.ResolveLogDirectory();
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"Invalid CSharpMCP log directory; using the default: {exception}");
        logDirectory = ServerLogging.ResolveLogDirectory(string.Empty);
    }

    var loggingConfiguration = new LoggerConfiguration();
    ServerLogging.Configure(loggingConfiguration, logDirectory);
    Log.Logger = loggingConfiguration.CreateLogger();
    Log.Information("CSharpMCP logging initialized at {LogDirectory}; retention {RetentionDays} days", logDirectory, ServerLogging.RetentionDays);

    MsBuildBootstrap.Register();
    var builder = Host.CreateApplicationBuilder(args);
    var toolCatalogProfile = ToolCatalogProfile.FromEnvironment();
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(Log.Logger, dispose: false);

    builder.Services.AddSingleton<SolutionWorkspaceCache>();
    builder.Services.AddSingleton<SolutionTrustStore>();
    builder.Services.AddSingleton<RoslynAnalysisService>();
    builder.Services.AddSingleton(toolCatalogProfile);
    builder.Services
        .AddMcpServer(options =>
        {
            // Keep the planning and verification contract close to the tool catalog exposed to Codex.
            options.ServerInfo = new()
            {
                Name = "csharp-roslyn",
                Title = "C# Roslyn Code Intelligence",
                Version = "2.0.0",
                Description = "Read-only, compiler-accurate navigation, impact analysis, diagnostics, and workspace intelligence for C# solutions."
            };
            options.ServerInstructions = "Compiler-accurate Roslyn intelligence with no repository-writing tools. Call trust_solution before loading a repository because MSBuild evaluation and configured analyzers/source generators execute repository-controlled code. Use symbol_at_position or symbol_info before unfamiliar edits; context_bundle for bounded orientation; invocation_binding for overload questions; find_references, inheritance_graph, member_surface, affected_symbols, and rename_preview before contract changes; implementation_map before interface or handler changes; test_impact for focused test planning; diagnostics_delta and diagnostics after meaningful C# edits; workspace_health when completeness is uncertain. Tool results use the MCP 2.0 structured envelope directly: inspect data for tool-specific facts and metadata for returned, truncated, workspaceLoadedAt, and workspaceDiagnostics completeness fields. Results describe compile-time semantics only; still run repository builds and tests.";
        })
        .WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var toolName = context.Params?.Name ?? "unknown";
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var result = await next(context, cancellationToken).ConfigureAwait(false);
                    if (result.IsError == true)
                    {
                        Log.Warning("MCP tool {ToolName} returned an error after {ElapsedMilliseconds} ms", toolName,
                            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Log.Information("MCP tool {ToolName} was cancelled", toolName);
                    throw;
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "MCP tool {ToolName} failed after {ElapsedMilliseconds} ms", toolName,
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    throw;
                }
            });

            // The catalog is static and user-independent, so MCP 2.0 clients may safely reuse it briefly.
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                result.Tools = result.Tools.Where(tool => toolCatalogProfile.IsEnabled(tool.Name)).ToList();
                result.CacheScope = CacheScope.Public;
                result.TimeToLive = TimeSpan.FromMinutes(10);
                return result;
            });
        })
        .WithStdioServerTransport()
        .WithTools<RoslynTools>();

    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception exception)
{
    Log.Fatal(exception, "CSharpMCP server startup or execution failed");
    // Also covers failures before the logger could be constructed.
    Console.Error.WriteLine($"CSharpMCP server startup or execution failed: {exception}");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
