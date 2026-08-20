using System.Text.Json;
using CSharpMcp.Analysis;
using CSharpMcp.Infrastructure;
using CSharpMcp.Tools;
using CSharpMcp.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace CSharpMcp.Tests;

public sealed class RoslynAnalysisServiceTests : IDisposable
{
    private readonly string testRoot;
    private readonly SolutionWorkspaceCache workspaceCache;
    private readonly RoslynAnalysisService analysisService;

    public RoslynAnalysisServiceTests()
    {
        MsBuildBootstrap.Register();
        testRoot = Path.Combine(Path.GetTempPath(), "CSharpMcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        var projectPath = Path.Combine(testRoot, "Fixture.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <EnableNETAnalyzers>true</EnableNETAnalyzers>
                <AnalysisLevel>latest-all</AnalysisLevel>
              </PropertyGroup>
              <ItemGroup>
                <Compile Remove="TestUsages.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(testRoot, ".editorconfig"), """
            root = true

            [*.cs]
            dotnet_diagnostic.CA1822.severity = warning
            """);
        File.WriteAllText(Path.Combine(testRoot, "Services.cs"), """
            using System.Text.RegularExpressions;

            namespace Fixture;

            public interface IGreeter
            {
                string Greet(string name);
            }

            public sealed class Greeter : IGreeter
            {
                public string Greet(string name) => $"Hello {name}";
            }

            public abstract class Processor
            {
                public abstract string Process(string value);
            }

            public sealed class UpperProcessor : Processor
            {
                public override string Process(string value) => value.ToUpperInvariant();
            }

            public static class Overloads
            {
                public static string Echo(string value) => value;

                public static int Echo(int value) => value;

                public static string CallStringOverload() => Echo("bound");
            }

            public static partial class RegexProvider
            {
                [GeneratedRegex("[A-Za-z]+")]
                public static partial Regex Word();
            }

            public sealed class Consumer
            {
                public string Run(IGreeter greeter, CandidateMethods candidates)
                {
                    candidates.UsedProduction();
                    return greeter.Greet("Codex");
                }
            }

            public sealed class CandidateMethods
            {
                public void UsedProduction()
                {
                }

                public void TestOnly()
                {
                }

                private void UnusedPrivate()
                {
                }
            }

            public sealed class AnalyzerCandidate
            {
                public int Sum(int first, int second) => first + second;
            }

            internal sealed class UnusedType
            {
            }

            public sealed class UnusedMembers
            {
                private int unusedField;

                private string UnusedProperty { get; set; } = string.Empty;

                private event System.EventHandler? UnusedEvent;
            }

            public sealed class NumberSequence : System.Collections.Generic.IEnumerable<int>
            {
                public System.Collections.Generic.IEnumerator<int> GetEnumerator() =>
                    System.Linq.Enumerable.Empty<int>().GetEnumerator();

                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            """);
        File.WriteAllText(Path.Combine(testRoot, "Conditional.cs"), """
            namespace Fixture;

            #if FEATURE_ALPHA
            public sealed class AlphaFeature
            {
            }
            #else
            public sealed class FallbackFeature
            {
            }
            #endif
            """);
        File.WriteAllText(Path.Combine(testRoot, "DevelopmentFeatures.cs"), """
            using System;

            namespace Fixture
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MarkerAttribute : Attribute
                {
                    public MarkerAttribute(string name)
                    {
                        Name = name;
                    }

                    public string Name { get; }
                }

                [Marker("service")]
                public sealed class MarkedService
                {
                }

                [Marker("base")]
                public class MarkedBase
                {
                }

                public sealed class MarkedDerived : MarkedBase
                {
                }

                [Obsolete("Use MarkedService", true)]
                public sealed class LegacyMarkedService
                {
                }

                public sealed class EventPublisher
                {
                    public event EventHandler? Changed;

                    public void Raise()
                    {
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                }

                public sealed class EventSubscriber
                {
                    public void Attach(EventPublisher publisher)
                    {
                        publisher.Changed += OnChanged;
                        publisher.Changed -= OnChanged;
                    }

                    private void OnChanged(object? sender, EventArgs args)
                    {
                    }
                }

                public sealed class FactoryProduct
                {
                    public FactoryProduct(string name)
                    {
                        Name = name;
                    }

                    public string Name { get; }
                }

                public static class ProductFactory
                {
                    public static FactoryProduct CreateProduct() => new("factory");
                }

                public static class ProductExtensions
                {
                    public static string Describe(this FactoryProduct product) => product.Name;
                }

                public static class FlowSamples
                {
                    public static int Select(bool condition, int first, int second)
                    {
                        var selected = condition ? first : second;
                        return selected;
                    }
                }
            }

            namespace Microsoft.Extensions.DependencyInjection
            {
                public interface IServiceCollection
                {
                }

                public static class ServiceCollectionExtensions
                {
                    public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services)
                        where TImplementation : TService => services;
                }
            }

            namespace Fixture.Composition
            {
                using Microsoft.Extensions.DependencyInjection;

                public static class ServiceRegistration
                {
                    public static IServiceCollection AddFixtureServices(IServiceCollection services)
                    {
                        services.AddSingleton<Fixture.IGreeter, Fixture.Greeter>();
                        return services;
                    }
                }
            }

            namespace Fixture.Domain
            {
                public sealed class DomainEntity
                {
                }
            }

            namespace Fixture.Web
            {
                public sealed class DomainConsumer
                {
                    public Fixture.Domain.DomainEntity Create() => new();
                }
            }
            """);
        File.WriteAllText(Path.Combine(testRoot, "Fixture.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Fixture.csproj" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="TestUsages.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(testRoot, "TestUsages.cs"), """
            using Fixture;

            namespace FixtureTests;

            public sealed class CandidateMethodTests
            {
                public void ExerciseTestOnlyMethod()
                {
                    new CandidateMethods().TestOnly();
                }
            }
            """);
        File.WriteAllText(Path.Combine(testRoot, "Fixture.slnx"), """
            <Solution>
              <Project Path="Fixture.csproj" />
              <Project Path="Fixture.Tests.csproj" />
            </Solution>
            """);

        var trustStore = new SolutionTrustStore(
            Path.Combine(testRoot, ".trust", "trusted-paths.json"),
            NullLogger<SolutionTrustStore>.Instance);
        trustStore.Trust(testRoot, persist: false);
        workspaceCache = new SolutionWorkspaceCache(NullLogger<SolutionWorkspaceCache>.Instance, trustStore);
        analysisService = new RoslynAnalysisService(workspaceCache);
    }

    [Fact]
    [ToolCoverage("solution_overview", "call_hierarchy", "type_usage", "semantic_search")]
    public async Task OrientationFlowUsageAndSearchToolsReturnCompilerResolvedEvidence()
    {
        var overview = await analysisService.GetSolutionOverviewAsync(
            GetSolutionPath(), configuration: "Debug", targetFramework: "net10.0", maxProjects: 10,
            cancellationToken: CancellationToken.None);
        var hierarchy = await analysisService.GetCallHierarchyAsync(
            GetProjectPath(), "M:Fixture.Consumer.Run(Fixture.IGreeter,Fixture.CandidateMethods)", projectName: "Fixture",
            direction: "callees", maxDepth: 1, maxResults: 20, cancellationToken: CancellationToken.None);
        var usage = await analysisService.GetTypeUsageAsync(
            GetProjectPath(), "T:Fixture.FactoryProduct", projectName: "Fixture", maxResults: 20,
            cancellationToken: CancellationToken.None);
        var search = await analysisService.SemanticSearchAsync(
            GetProjectPath(), "Greeter", projectName: "Fixture", symbolKinds: "NamedType", maxResults: 20,
            cancellationToken: CancellationToken.None);

        var overviewJson = JsonSerializer.Serialize(overview);
        var hierarchyJson = JsonSerializer.Serialize(hierarchy);
        var usageJson = JsonSerializer.Serialize(usage);
        var searchJson = JsonSerializer.Serialize(search);
        Assert.Contains("Fixture.Tests", overviewJson, StringComparison.Ordinal);
        Assert.Contains("evaluatedConfiguration\":\"Debug", overviewJson, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.IGreeter.Greet(System.String)", hierarchyJson, StringComparison.Ordinal);
        Assert.Contains("callee", hierarchyJson, StringComparison.Ordinal);
        Assert.Contains("api-signature", usageJson, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.FactoryProduct", usageJson, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.Greeter", searchJson, StringComparison.Ordinal);
        Assert.Contains("Score", searchJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("symbol_info")]
    public async Task SymbolInfoResolvesDocumentationId()
    {
        var result = await analysisService.GetSymbolInfoAsync(
            GetProjectPath(),
            "T:Fixture.IGreeter",
            projectName: null,
            maxResults: 10,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("T:Fixture.IGreeter", json, StringComparison.Ordinal);
        Assert.Contains("Interface", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("implementation_map")]
    public async Task ImplementationMapFindsConcreteType()
    {
        var result = await analysisService.GetImplementationMapAsync(
            GetProjectPath(),
            "T:Fixture.IGreeter",
            projectName: null,
            maxResults: 10,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("T:Fixture.Greeter", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("find_references")]
    public async Task FindReferencesReturnsSourceLocation()
    {
        var result = await analysisService.FindReferencesAsync(
            GetProjectPath(),
            "M:Fixture.IGreeter.Greet(System.String)",
            projectName: null,
            referenceKinds: null,
            includeDeclarations: false,
            maxResults: 10,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("Services.cs", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invocation", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("unused_symbol_audit")]
    public async Task UnusedSymbolAuditSeparatesUnusedAndTestOnlyMethods()
    {
        var result = await analysisService.AuditUnusedSymbolsAsync(
            GetSolutionPath(),
            projectName: "Fixture",
            includeTestProjectsAsCandidates: false,
            symbolKinds: "Method",
            maxSymbols: 100,
            maxResults: 100,
            maxReferencesPerSymbol: 10,
            cancellationToken: CancellationToken.None);

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("M:Fixture.CandidateMethods.UnusedPrivate", json, StringComparison.Ordinal);
        Assert.Contains("no-source-references", json, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.CandidateMethods.TestOnly", json, StringComparison.Ordinal);
        Assert.Contains("test-only-references", json, StringComparison.Ordinal);
        Assert.Contains("TestUsages.cs", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("M:Fixture.CandidateMethods.UsedProduction", json, StringComparison.Ordinal);
        Assert.DoesNotContain("M:Fixture.Greeter.Greet", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("symbol_at_position", "invocation_binding")]
    public async Task PositionAndInvocationToolsUseCompilerBinding()
    {
        var servicesPath = GetServicesPath();
        var (line, column) = FindPosition(servicesPath, "Echo(\"bound\")");

        var symbolResult = await analysisService.GetSymbolAtPositionAsync(
            GetProjectPath(),
            servicesPath,
            line,
            column,
            includeCandidates: true,
            maxCandidates: 10,
            CancellationToken.None);
        var invocationResult = await analysisService.GetInvocationBindingAsync(
            GetProjectPath(),
            servicesPath,
            line,
            column + 2,
            maxCandidates: 10,
            CancellationToken.None);

        var symbolJson = JsonSerializer.Serialize(symbolResult);
        var invocationJson = JsonSerializer.Serialize(invocationResult);
        Assert.Contains("M:Fixture.Overloads.Echo(System.String)", symbolJson, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.Overloads.Echo(System.String)", invocationJson, StringComparison.Ordinal);
        Assert.Contains("System.String", invocationJson, StringComparison.Ordinal);
        Assert.Contains("value", invocationJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("member_surface", "inheritance_graph")]
    public async Task MemberSurfaceAndInheritanceGraphReportTypeRelationships()
    {
        var surface = await analysisService.GetMemberSurfaceAsync(
            GetProjectPath(),
            "T:Fixture.UpperProcessor",
            projectName: null,
            memberKinds: "Method",
            accessibility: "public-or-protected",
            includeInherited: true,
            includeExplicitInterfaceImplementations: true,
            memberName: null,
            includeApplicableExtensionMethods: false,
            mode: "all",
            maxResults: 100,
            CancellationToken.None);
        var graph = await analysisService.GetInheritanceGraphAsync(
            GetProjectPath(),
            "T:Fixture.Processor",
            projectName: null,
            direction: "both",
            maxDepth: 2,
            includeInterfaces: true,
            maxResults: 100,
            CancellationToken.None);
        var extensions = await analysisService.GetMemberSurfaceAsync(
            GetProjectPath(),
            "T:Fixture.FactoryProduct",
            projectName: null,
            memberKinds: "Method",
            accessibility: "public",
            includeInherited: false,
            includeExplicitInterfaceImplementations: true,
            memberName: "Describe",
            includeApplicableExtensionMethods: true,
            mode: "extensions",
            maxResults: 100,
            CancellationToken.None);
        var metadataExtensions = await analysisService.GetMemberSurfaceAsync(
            GetProjectPath(),
            "T:Fixture.NumberSequence",
            projectName: null,
            memberKinds: "Method",
            accessibility: "public",
            includeInherited: false,
            includeExplicitInterfaceImplementations: true,
            memberName: "Any",
            includeApplicableExtensionMethods: true,
            mode: "extensions",
            maxResults: 20,
            CancellationToken.None);
        var constructors = await analysisService.GetMemberSurfaceAsync(
            GetProjectPath(),
            "T:Fixture.FactoryProduct",
            projectName: null,
            memberKinds: "Method",
            accessibility: "public",
            includeInherited: false,
            includeExplicitInterfaceImplementations: true,
            memberName: null,
            includeApplicableExtensionMethods: false,
            mode: "constructors",
            maxResults: 20,
            CancellationToken.None);

        var surfaceJson = JsonSerializer.Serialize(surface);
        var graphJson = JsonSerializer.Serialize(graph);
        var extensionsJson = JsonSerializer.Serialize(extensions);
        var metadataExtensionsJson = JsonSerializer.Serialize(metadataExtensions);
        var constructorsJson = JsonSerializer.Serialize(constructors);
        Assert.Contains("M:Fixture.UpperProcessor.Process(System.String)", surfaceJson, StringComparison.Ordinal);
        Assert.Contains("override", surfaceJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T:Fixture.UpperProcessor", graphJson, StringComparison.Ordinal);
        Assert.Contains("base-type", graphJson, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.ProductExtensions.Describe", extensionsJson, StringComparison.Ordinal);
        Assert.Contains("applicable-source-extension", extensionsJson, StringComparison.Ordinal);
        Assert.Contains("System.Linq.Enumerable.Any", metadataExtensionsJson, StringComparison.Ordinal);
        Assert.Contains("applicable-metadata-extension", metadataExtensionsJson, StringComparison.Ordinal);
        Assert.Contains("Constructor", constructorsJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("rename_preview")]
    public async Task RenamePreviewChangesImmutableSolutionWithoutWritingFiles()
    {
        var original = File.ReadAllText(GetServicesPath());

        var result = await analysisService.GetRenamePreviewAsync(
            GetProjectPath(),
            "M:Fixture.IGreeter.Greet(System.String)",
            refactorKind: "rename",
            newName: "Welcome",
            newSignature: null,
            projectName: null,
            renameInStrings: false,
            renameInComments: false,
            renameOverloads: false,
            renameFile: false,
            expectedFingerprint: null,
            cursor: null,
            maxResults: 100,
            cancellationToken: CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("Welcome", json, StringComparison.Ordinal);
        Assert.Contains("appliedToDisk\":false", json, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(GetServicesPath()));
    }

    [Fact]
    [ToolCoverage("diagnostics_delta")]
    public async Task DiagnosticsDeltaReportsAnIntroducedCompilerError()
    {
        var baselineResult = await analysisService.GetDiagnosticsDeltaAsync(
            GetProjectPath(),
            baselineToken: null,
            projectName: "Fixture",
            minimumSeverity: "error",
            includeAnalyzers: false,
            maxResults: 100,
            CancellationToken.None);
        using var baselineJson = JsonDocument.Parse(JsonSerializer.Serialize(baselineResult));
        var token = baselineJson.RootElement.GetProperty("Data").GetProperty("baselineToken").GetString();
        Assert.NotNull(token);

        File.AppendAllText(GetServicesPath(), Environment.NewLine + "public sealed class BrokenType { public MissingType Value { get; } = null!; }");
        await Task.Delay(500);

        var delta = await analysisService.GetDiagnosticsDeltaAsync(
            GetProjectPath(),
            token,
            projectName: "Fixture",
            minimumSeverity: "error",
            includeAnalyzers: false,
            maxResults: 100,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(delta);
        Assert.Contains("CS0246", json, StringComparison.Ordinal);
        Assert.Contains("MissingType", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("test_impact")]
    public async Task TestImpactFindsTestProjectCaller()
    {
        var result = await analysisService.GetTestImpactAsync(
            GetSolutionPath(),
            ["M:Fixture.CandidateMethods.TestOnly"],
            documentPaths: null,
            projectName: "Fixture",
            maxDepth: 3,
            maxResults: 100,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("ExerciseTestOnlyMethod", json, StringComparison.Ordinal);
        Assert.Contains("TestUsages.cs", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidencePath", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("source_generator_inventory")]
    public async Task SourceGeneratorInventoryFindsGeneratedRegexOutput()
    {
        var result = await analysisService.GetSourceGeneratorInventoryAsync(
            GetProjectPath(),
            projectName: "Fixture",
            generatorName: null,
            generatedDocumentId: null,
            includeGeneratedSourceExcerpt: true,
            cursor: null,
            maxResults: 100,
            CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var generatedDocumentCount = document.RootElement.GetProperty("Data")
            .GetProperty("projects")[0]
            .GetProperty("generatedDocumentCount")
            .GetInt32();
        Assert.True(generatedDocumentCount > 0, json);
        Assert.Contains("generatedDocumentCount", json, StringComparison.Ordinal);
        Assert.Contains("RegexProvider", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("conditional_compilation_matrix")]
    public async Task ConditionalCompilationMatrixShowsBothBranches()
    {
        var result = await analysisService.GetConditionalCompilationMatrixAsync(
            GetProjectPath(),
            "Fixture",
            ["FEATURE_ALPHA", ""],
            configurations: ["Debug", "Release"],
            targetFrameworks: ["net10.0"],
            documentPath: Path.Combine(testRoot, "Conditional.cs"),
            maxResults: 100,
            cancellationToken: CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("T:Fixture.AlphaFeature", json, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.FallbackFeature", json, StringComparison.Ordinal);
        Assert.Contains("inactiveRegions", json, StringComparison.Ordinal);
        Assert.Contains("requestedConfigurations", json, StringComparison.Ordinal);
        Assert.Contains("Release", json, StringComparison.Ordinal);
        Assert.Contains("net10.0", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("workspace_health")]
    public async Task WorkspaceHealthReportsCacheAndCompilationState()
    {
        var result = await analysisService.GetWorkspaceHealthAsync(
            GetProjectPath(),
            configuration: "Release",
            targetFramework: "net10.0",
            includeProjectChecks: true,
            maxProjects: 100,
            cancellationToken: CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("completeEnoughForSemanticQueries\":true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reloadCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MSBuildPath", json, StringComparison.Ordinal);
        Assert.Contains("evaluatedConfiguration\":\"Release", json, StringComparison.Ordinal);
        Assert.Contains("evaluatedTargetFramework\":\"net10.0", json, StringComparison.Ordinal);
        Assert.Contains("skippedProjectPaths", json, StringComparison.Ordinal);
        Assert.Contains("Fixture", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("symbol_source", "event_flow", "attribute_usage")]
    public async Task SourceEventAndAttributeToolsReturnResolvedEvidence()
    {
        var source = await analysisService.GetSymbolSourceAsync(
            GetProjectPath(),
            ["M:Fixture.Overloads.Echo(System.String)", "T:Fixture.DoesNotExist"],
            projectName: null,
            includeBody: true,
            maxLines: 50,
            maxCharacters: 5000,
            CancellationToken.None);
        var eventFlow = await analysisService.GetEventFlowAsync(
            GetProjectPath(),
            "E:Fixture.EventPublisher.Changed",
            projectName: null,
            actions: null,
            maxResults: 100,
            CancellationToken.None);
        var attributes = await analysisService.GetAttributeUsageAsync(
            GetProjectPath(),
            "T:Fixture.MarkerAttribute",
            projectName: null,
            targetKinds: "NamedType",
            includeInherited: false,
            includeMigrationGroups: false,
            maxResults: 100,
            cancellationToken: CancellationToken.None);

        var sourceJson = JsonSerializer.Serialize(source);
        var eventJson = JsonSerializer.Serialize(eventFlow);
        var attributeJson = JsonSerializer.Serialize(attributes);
        Assert.Contains("status\":\"ok", sourceJson, StringComparison.Ordinal);
        Assert.Contains("status\":\"notFound", sourceJson, StringComparison.Ordinal);
        Assert.Contains("subscribe", eventJson, StringComparison.Ordinal);
        Assert.Contains("unsubscribe", eventJson, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.EventSubscriber.OnChanged", eventJson, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.MarkedService", attributeJson, StringComparison.Ordinal);
        Assert.Contains("service", attributeJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("dependency_injection_map", "construction_options")]
    public async Task DependencyInjectionAndConstructionToolsExplainComposition()
    {
        var registrations = await analysisService.GetDependencyInjectionMapAsync(
            GetProjectPath(),
            projectName: "Fixture",
            serviceSymbol: "T:Fixture.IGreeter",
            lifetimes: "singleton",
            maxResults: 100,
            CancellationToken.None);
        var construction = await analysisService.GetConstructionOptionsAsync(
            GetProjectPath(),
            "T:Fixture.FactoryProduct",
            projectName: "Fixture",
            fromProject: "Fixture",
            maxResults: 100,
            CancellationToken.None);

        var registrationJson = JsonSerializer.Serialize(registrations);
        var constructionJson = JsonSerializer.Serialize(construction);
        Assert.Contains("T:Fixture.IGreeter", registrationJson, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.Greeter", registrationJson, StringComparison.Ordinal);
        Assert.Contains("singleton", registrationJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("M:Fixture.FactoryProduct.#ctor(System.String)", constructionJson, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.ProductFactory.CreateProduct", constructionJson, StringComparison.Ordinal);
        Assert.Contains("accessibleFromProject\":true", constructionJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [ToolCoverage("api_compatibility", "architecture_rule_check", "context_bundle")]
    public async Task ApiArchitectureAndContextToolsStayBoundedAndSemantic()
    {
        var api = await analysisService.GetApiCompatibilityAsync(
            GetProjectPath(),
            projectName: "Fixture",
            baselinePath: null,
            includeCurrentSurface: true,
            cursor: null,
            maxResults: 500,
            cancellationToken: CancellationToken.None);
        var architecture = await analysisService.CheckArchitectureRulesAsync(
            GetProjectPath(),
            [new ArchitectureRuleInput("web-cannot-use-domain", "Fixture.Web", ["Fixture.Domain"], null)],
            projectName: "Fixture",
            maxResults: 100,
            CancellationToken.None);
        var context = await analysisService.GetContextBundleAsync(
            GetProjectPath(),
            "T:Fixture.UpperProcessor",
            projectName: "Fixture",
            profile: "understand",
            maxResultsPerSection: 20,
            CancellationToken.None);

        var apiJson = JsonSerializer.Serialize(api);
        var architectureJson = JsonSerializer.Serialize(architecture);
        var contextJson = JsonSerializer.Serialize(context);
        Assert.Contains("T:Fixture.IGreeter", apiJson, StringComparison.Ordinal);
        Assert.Contains("web-cannot-use-domain", architectureJson, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.Domain.DomainEntity", architectureJson, StringComparison.Ordinal);
        Assert.Contains("members", contextJson, StringComparison.Ordinal);
        Assert.Contains("inheritance", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("api_compatibility")]
    public async Task ApiCompatibilityDelegatesDllBaselinesToOfficialApiCompat()
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(GetProjectPath(), CancellationToken.None);
        var project = Assert.Single(snapshot.Solution.Projects);
        var compilation = await project.GetCompilationAsync(CancellationToken.None);
        Assert.NotNull(compilation);
        var baselinePath = Path.Combine(testRoot, "Fixture.Baseline.dll");
        await using (var stream = File.Create(baselinePath))
        {
            var emit = compilation.Emit(stream);
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        }

        var result = await analysisService.GetApiCompatibilityAsync(
            GetProjectPath(), projectName: "Fixture", baselinePath, includeCurrentSurface: false,
            cursor: null, maxResults: 50, cancellationToken: CancellationToken.None);
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("Microsoft.DotNet.ApiCompat.Tool", json, StringComparison.Ordinal);
        Assert.Contains("\"Compatible\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("region_flow", "resolve_stack_trace")]
    public async Task RegionFlowAndStackTraceResolveCompilerContext()
    {
        var path = Path.Combine(testRoot, "DevelopmentFeatures.cs");
        var start = FindPosition(path, "var selected");
        var end = FindPosition(path, "return selected;");
        var flow = await analysisService.GetRegionFlowAsync(
            GetProjectPath(),
            path,
            start.Line,
            start.Column,
            end.Line,
            end.Column + "return selected;".Length,
            "both",
            CancellationToken.None);
        var stack = await analysisService.ResolveStackTraceAsync(
            GetProjectPath(),
            "   at Fixture.Overloads.CallStringOverload()",
            maxFrames: 10,
            CancellationToken.None);

        var flowJson = JsonSerializer.Serialize(flow);
        var stackJson = JsonSerializer.Serialize(stack);
        Assert.Contains("selected", flowJson, StringComparison.Ordinal);
        Assert.Contains("dataFlow", flowJson, StringComparison.Ordinal);
        Assert.Contains("controlFlow", flowJson, StringComparison.Ordinal);
        Assert.Contains("M:Fixture.Overloads.CallStringOverload", stackJson, StringComparison.Ordinal);
        Assert.Contains("resolved", stackJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("rename_preview")]
    public async Task RenamePreviewUsesRoslynAndSupportsSignaturePaginationAndFreshness()
    {
        var signature = await analysisService.GetRenamePreviewAsync(
            GetProjectPath(),
            "M:Fixture.IGreeter.Greet(System.String)",
            refactorKind: "signature",
            newName: null,
            newSignature: "(string name, int count)",
            projectName: null,
            renameInStrings: false,
            renameInComments: false,
            renameOverloads: false,
            renameFile: false,
            expectedFingerprint: null,
            cursor: null,
            maxResults: 1,
            cancellationToken: CancellationToken.None);

        var json = JsonSerializer.Serialize(signature);
        Assert.Contains("signature", json, StringComparison.Ordinal);
        Assert.Contains("normalizedParameterList", json, StringComparison.Ordinal);
        Assert.Contains("nextCursor", json, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => analysisService.GetRenamePreviewAsync(
            GetProjectPath(),
            "M:Fixture.IGreeter.Greet(System.String)",
            refactorKind: "rename",
            newName: "Welcome",
            newSignature: null,
            projectName: null,
            renameInStrings: false,
            renameInComments: false,
            renameOverloads: false,
            renameFile: false,
            expectedFingerprint: "stale",
            cursor: null,
            maxResults: 10,
            cancellationToken: CancellationToken.None));
    }

    [Fact]
    [ToolCoverage("unused_symbol_audit")]
    public async Task UnusedSymbolAuditCoversTypesPropertiesFieldsAndEvents()
    {
        var result = await analysisService.AuditUnusedSymbolsAsync(
            GetProjectPath(),
            projectName: "Fixture",
            includeTestProjectsAsCandidates: false,
            symbolKinds: "NamedType,Property,Field,Event",
            maxSymbols: 500,
            maxResults: 100,
            maxReferencesPerSymbol: 5,
            cancellationToken: CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("T:Fixture.UnusedType", json, StringComparison.Ordinal);
        Assert.Contains("P:Fixture.UnusedMembers.UnusedProperty", json, StringComparison.Ordinal);
        Assert.Contains("F:Fixture.UnusedMembers.unusedField", json, StringComparison.Ordinal);
        Assert.Contains("E:Fixture.UnusedMembers.UnusedEvent", json, StringComparison.Ordinal);
        Assert.Contains("runtime-discovery-not-ruled-out", json, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("project_dependencies", "affected_symbols", "diagnostics")]
    public async Task DependencyImpactAndDiagnosticsExposeExpandedSections()
    {
        var dependencies = await analysisService.GetProjectDependenciesAsync(
            GetSolutionPath(), projectName: null, includeNamespaceEdges: true, maxResults: 100,
            cancellationToken: CancellationToken.None);
        var impact = await analysisService.GetAffectedSymbolsAsync(
            GetSolutionPath(), "M:Fixture.IGreeter.Greet(System.String)", projectName: null,
            maxContracts: 5, maxImplementations: 5, maxReferences: 5, maxCallers: 5, maxTests: 5,
            maxDependentProjects: 5, cancellationToken: CancellationToken.None);
        var diagnostics = await analysisService.GetDiagnosticsAsync(
            GetProjectPath(), projectName: "Fixture", minimumSeverity: "info", includeAnalyzers: true,
            documentPath: null, diagnosticIds: "CA1822", includeSuppressed: true, maxResults: 100,
            cancellationToken: CancellationToken.None);

        var dependencyJson = JsonSerializer.Serialize(dependencies);
        var impactJson = JsonSerializer.Serialize(impact);
        var diagnosticsJson = JsonSerializer.Serialize(diagnostics);
        Assert.Contains("transitiveProjectDependencies", dependencyJson, StringComparison.Ordinal);
        Assert.Contains("namespaceEdges", dependencyJson, StringComparison.Ordinal);
        Assert.Contains("transitiveReferencedBy", dependencyJson, StringComparison.Ordinal);
        Assert.Contains("callers", impactJson, StringComparison.Ordinal);
        Assert.Contains("sectionLimits", impactJson, StringComparison.Ordinal);
        Assert.Contains("analyzers", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("origin", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"Assembly\":", diagnosticsJson, StringComparison.Ordinal);
        Assert.Contains("\"Type\":", diagnosticsJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("attribute_usage")]
    public async Task AttributeUsageReturnsConstructorInheritanceAndMigrationGroups()
    {
        var inherited = await analysisService.GetAttributeUsageAsync(
            GetProjectPath(), "T:Fixture.MarkerAttribute", projectName: null, targetKinds: "NamedType",
            includeInherited: true, includeMigrationGroups: false, maxResults: 100,
            cancellationToken: CancellationToken.None);
        var obsolete = await analysisService.GetAttributeUsageAsync(
            GetProjectPath(), "T:System.ObsoleteAttribute", projectName: null, targetKinds: "NamedType",
            includeInherited: false, includeMigrationGroups: true, maxResults: 100,
            cancellationToken: CancellationToken.None);

        var inheritedJson = JsonSerializer.Serialize(inherited);
        var obsoleteJson = JsonSerializer.Serialize(obsolete);
        Assert.Contains("M:Fixture.MarkerAttribute.#ctor(System.String)", inheritedJson, StringComparison.Ordinal);
        Assert.Contains("T:Fixture.MarkedDerived", inheritedJson, StringComparison.Ordinal);
        Assert.Contains("inheritedFrom", inheritedJson, StringComparison.Ordinal);
        Assert.Contains("Use MarkedService", obsoleteJson, StringComparison.Ordinal);
        Assert.Contains("migrationGroups", obsoleteJson, StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("trust_solution", "list_trusted_paths", "revoke_trust")]
    public void SolutionTrustRequiresExplicitAuthorizationAndSupportsRevocation()
    {
        var trustPath = Path.Combine(testRoot, ".isolated-trust", "trusted.json");
        var store = new SolutionTrustStore(trustPath, NullLogger<SolutionTrustStore>.Instance);

        Assert.Throws<UntrustedWorkspaceException>(() => store.EnsureTrusted(GetProjectPath()));
        var entry = store.Trust(GetSolutionPath(), persist: true);
        Assert.True(entry.SessionTrusted);
        Assert.True(entry.Persisted);
        Assert.True(store.IsTrusted(GetProjectPath()));
        Assert.Contains(store.List(), trusted =>
            trusted.RootPath.Equals(entry.RootPath, StringComparison.OrdinalIgnoreCase) &&
            trusted.SessionTrusted &&
            trusted.Persisted);

        var revocation = store.Revoke(GetSolutionPath());
        Assert.True(revocation.SessionTrustRemoved);
        Assert.True(revocation.PersistentTrustRemoved);
        Assert.False(store.IsTrusted(GetProjectPath()));
    }

    [Fact]
    public void McpCatalogContainsMergedReadOnlyPortfolioWithStructuredContent()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "solution_overview",
            "symbol_info",
            "find_references",
            "call_hierarchy",
            "implementation_map",
            "type_usage",
            "diagnostics",
            "project_dependencies",
            "semantic_search",
            "unused_symbol_audit",
            "affected_symbols",
            "symbol_at_position",
            "invocation_binding",
            "member_surface",
            "inheritance_graph",
            "rename_preview",
            "diagnostics_delta",
            "test_impact",
            "source_generator_inventory",
            "conditional_compilation_matrix",
            "workspace_health",
            "symbol_source",
            "event_flow",
            "attribute_usage",
            "dependency_injection_map",
            "construction_options",
            "api_compatibility",
            "region_flow",
            "architecture_rule_check",
            "resolve_stack_trace",
            "context_bundle",
            "trust_solution",
            "list_trusted_paths",
            "revoke_trust"
        };
        var advertisedMethods = typeof(RoslynTools).GetMethods()
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)
                    .Cast<McpServerToolAttribute>()
                    .SingleOrDefault()
            })
            .Where(item => item.Attribute?.Name is not null)
            .ToArray();
        var advertised = advertisedMethods
            .Select(item => item.Attribute!.Name!)
            .ToHashSet(StringComparer.Ordinal);
        var behaviorallyCovered = typeof(RoslynAnalysisServiceTests).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(ToolCoverageAttribute), inherit: false)
                .Cast<ToolCoverageAttribute>())
            .SelectMany(attribute => attribute.ToolNames)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(expected, name => Assert.Contains(name, advertised));
        Assert.Equal(34, advertised.Count);
        Assert.Equal(advertised.Order(StringComparer.Ordinal), behaviorallyCovered.Order(StringComparer.Ordinal));
        Assert.All(advertisedMethods, item => Assert.True(item.Attribute!.UseStructuredContent, item.Attribute.Name));
        Assert.All(advertisedMethods, item => Assert.NotNull(item.Attribute!.OutputSchemaType));
        Assert.All(advertisedMethods, item => Assert.Equal(typeof(McpToolResponse), UnwrapTask(item.Method.ReturnType)));
        Assert.All(advertisedMethods, item => Assert.False(string.IsNullOrWhiteSpace(item.Attribute!.Title), item.Attribute.Name));
        Assert.All(advertisedMethods, item => Assert.False(item.Attribute!.OpenWorld, item.Attribute.Name));
    }

    [Fact]
    [ToolCoverage("find_references")]
    public async Task FindReferencesClassifiesDependencyInjectionRegistration()
    {
        var result = await analysisService.FindReferencesAsync(
            GetProjectPath(),
            "T:Fixture.Greeter",
            projectName: "Fixture",
            referenceKinds: "dependency_injection_registration",
            includeDeclarations: false,
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var references = document.RootElement
            .GetProperty("Data")
            .GetProperty("references")
            .EnumerateArray()
            .ToArray();

        var reference = Assert.Single(references);

        var kinds = reference
            .GetProperty("Kinds")
            .EnumerateArray()
            .Select(kind => kind.GetString())
            .ToArray();

        Assert.Contains("dependency_injection_registration", kinds);

        var location = reference.GetProperty("Location");

        Assert.Equal(
            "Fixture",
            location.GetProperty("Project").GetString());

        Assert.Equal(
            "DevelopmentFeatures.cs",
            Path.GetFileName(location.GetProperty("File").GetString()));

        Assert.Contains(
            "AddSingleton",
            location.GetProperty("Excerpt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("find_references")]
    public async Task FindReferencesClassifiesEventSubscriptionAndUnsubscription()
    {
        var result = await analysisService.FindReferencesAsync(
            GetProjectPath(),
            "E:Fixture.EventPublisher.Changed",
            projectName: "Fixture",
            referenceKinds: "event_subscribe,event_unsubscribe",
            includeDeclarations: false,
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var references = document.RootElement
            .GetProperty("Data")
            .GetProperty("references")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, references.Length);

        var kinds = references
            .SelectMany(reference => reference
                .GetProperty("Kinds")
                .EnumerateArray()
                .Select(kind => kind.GetString()))
            .ToArray();

        Assert.Equal(1, kinds.Count(kind => kind == "event_subscribe"));
        Assert.Equal(1, kinds.Count(kind => kind == "event_unsubscribe"));

        Assert.All(
            references,
            reference =>
            {
                var location = reference.GetProperty("Location");

                Assert.Equal("Fixture", location.GetProperty("Project").GetString());
                Assert.Equal(
                    "DevelopmentFeatures.cs",
                    Path.GetFileName(location.GetProperty("File").GetString()));
            });
    }

    [Fact]
    [ToolCoverage("find_references")]
    public async Task FindReferencesClassifiesTestProjectReference()
    {
        var result = await analysisService.FindReferencesAsync(
            GetSolutionPath(),
            "M:Fixture.CandidateMethods.TestOnly",
            projectName: "Fixture",
            referenceKinds: "test_reference",
            includeDeclarations: false,
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var references = document.RootElement
            .GetProperty("Data")
            .GetProperty("references")
            .EnumerateArray()
            .ToArray();

        var reference = Assert.Single(references);

        Assert.Contains(
            "test_reference",
            reference.GetProperty("Kinds")
                .EnumerateArray()
                .Select(kind => kind.GetString()));

        Assert.Contains(
            "invocation",
            reference.GetProperty("Kinds")
                .EnumerateArray()
                .Select(kind => kind.GetString()));

        var location = reference.GetProperty("Location");

        Assert.Equal("Fixture.Tests", location.GetProperty("Project").GetString());
        Assert.Equal("TestUsages.cs", Path.GetFileName(location.GetProperty("File").GetString()));
        Assert.Contains(
            "TestOnly()",
            location.GetProperty("Excerpt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("find_references")]
    public async Task FindReferencesCanReturnSymbolDeclaration()
    {
        var result = await analysisService.FindReferencesAsync(
            GetProjectPath(),
            "M:Fixture.IGreeter.Greet(System.String)",
            projectName: "Fixture",
            referenceKinds: "declaration",
            includeDeclarations: true,
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var references = document.RootElement
            .GetProperty("Data")
            .GetProperty("references")
            .EnumerateArray()
            .ToArray();

        var reference = Assert.Single(
            references,
            item => item.GetProperty("Role").GetString() == "declaration");

        var kinds = reference
            .GetProperty("Kinds")
            .EnumerateArray()
            .Select(kind => kind.GetString())
            .ToArray();

        Assert.Contains("declaration", kinds);

        var location = reference.GetProperty("Location");

        Assert.Equal(
            "Fixture",
            location.GetProperty("Project").GetString());

        Assert.Equal(
            "Services.cs",
            Path.GetFileName(location.GetProperty("File").GetString()));

        Assert.Contains(
            "Greet(string name)",
            location.GetProperty("Excerpt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("type_usage")]
    public async Task TypeUsageClassifiesDependencyInjectionRegistration()
    {
        var result = await analysisService.GetTypeUsageAsync(
            GetProjectPath(),
            "T:Fixture.IGreeter",
            projectName: "Fixture",
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var usages = document.RootElement
            .GetProperty("Data")
            .GetProperty("usages")
            .EnumerateArray()
            .ToArray();

        Assert.Contains(
            usages,
            usage => usage.GetProperty("Role").GetString() == "di-registration-candidate");

        Assert.Contains(
            usages,
            usage => usage.GetProperty("Role").GetString() == "api-signature");

        var summary = document.RootElement
            .GetProperty("Data")
            .GetProperty("summary");

        Assert.Equal(
            1,
            summary.GetProperty("di-registration-candidate").GetInt32());
    }

    [Fact]
    [ToolCoverage("symbol_source")]
    public async Task SymbolSourceReportsAmbiguousSymbolQuery()
    {
        var result = await analysisService.GetSymbolSourceAsync(
            GetProjectPath(),
            ["Echo"],
            projectName: "Fixture",
            includeBody: true,
            maxLines: 50,
            maxCharacters: 5000,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var item = Assert.Single(
            document.RootElement
                .GetProperty("Data")
                .GetProperty("items")
                .EnumerateArray());

        Assert.Equal("ambiguous", item.GetProperty("status").GetString());

        var candidates = item
            .GetProperty("candidates")
            .EnumerateArray()
            .Select(candidate => candidate.GetString())
            .ToArray();

        Assert.Equal(2, candidates.Length);
        Assert.Contains("M:Fixture.Overloads.Echo(System.String)", candidates);
        Assert.Contains("M:Fixture.Overloads.Echo(System.Int32)", candidates);
    }

    [Fact]
    [ToolCoverage("semantic_search")]
    public async Task SemanticSearchRejectsUnknownSymbolKind()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            analysisService.SemanticSearchAsync(
                GetProjectPath(),
                "Greeter",
                projectName: "Fixture",
                symbolKinds: "DefinitelyNotASymbolKind",
                maxResults: 20,
                CancellationToken.None));

        Assert.Equal("symbolKinds", exception.ParamName);
    }

    [Fact]
    [ToolCoverage("find_references")]
    public async Task FindReferencesClassifiesPropertyReadAndWrite()
    {
        var result = await analysisService.FindReferencesAsync(
            GetProjectPath(),
            "P:Fixture.FactoryProduct.Name",
            projectName: "Fixture",
            referenceKinds: "read,write",
            includeDeclarations: false,
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var references = document.RootElement
            .GetProperty("Data")
            .GetProperty("references")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, references.Length);

        var kinds = references
            .SelectMany(reference => reference
                .GetProperty("Kinds")
                .EnumerateArray()
                .Select(kind => kind.GetString()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("read", kinds);
        Assert.Contains("write", kinds);
    }

    [Fact]
    [ToolCoverage("diagnostics")]
    public async Task DiagnosticsCanBeFilteredToDocument()
    {
        var result = await analysisService.GetDiagnosticsAsync(
            GetProjectPath(),
            projectName: "Fixture",
            minimumSeverity: "warning",
            includeAnalyzers: true,
            documentPath: GetServicesPath(),
            diagnosticIds: "CA1822",
            includeSuppressed: true,
            maxResults: 100,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var diagnostics = document.RootElement
            .GetProperty("Data")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .ToArray();

        Assert.NotEmpty(diagnostics);

        Assert.All(
            diagnostics,
            diagnostic =>
            {
                Assert.Equal("CA1822", diagnostic.GetProperty("Id").GetString());

                var location = diagnostic.GetProperty("location");

                Assert.Equal(
                    "Fixture",
                    location.GetProperty("Project").GetString());

                Assert.Equal(
                    "Services.cs",
                    Path.GetFileName(location.GetProperty("File").GetString()));
            });
    }

    [Fact]
    [ToolCoverage("call_hierarchy")]
    public async Task CallHierarchyRejectsNonMethodSymbol()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analysisService.GetCallHierarchyAsync(
                GetProjectPath(),
                "T:Fixture.IGreeter",
                projectName: "Fixture",
                direction: "callees",
                maxDepth: 1,
                maxResults: 20,
                CancellationToken.None));

        Assert.Contains(
            "call_hierarchy requires a method",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    [ToolCoverage("call_hierarchy")]
    public async Task CallHierarchyFindsMethodCaller()
    {
        var result = await analysisService.GetCallHierarchyAsync(
            GetProjectPath(),
            "M:Fixture.CandidateMethods.UsedProduction",
            projectName: "Fixture",
            direction: "callers",
            maxDepth: 1,
            maxResults: 20,
            CancellationToken.None);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var edges = document.RootElement
            .GetProperty("Data")
            .GetProperty("edges")
            .EnumerateArray()
            .ToArray();

        var edge = Assert.Single(
            edges,
            item => item.GetProperty("direction").GetString() == "caller");

        Assert.Equal(
            "M:Fixture.Consumer.Run(Fixture.IGreeter,Fixture.CandidateMethods)",
            edge.GetProperty("from")
                .GetProperty("Id")
                .GetString());

        Assert.Equal(
            "M:Fixture.CandidateMethods.UsedProduction",
            edge.GetProperty("to")
                .GetProperty("Id")
                .GetString());

        Assert.Equal(1, edge.GetProperty("depth").GetInt32());
    }

    public void Dispose()
    {
        workspaceCache.Dispose();
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (IOException)
        {
            // Roslyn build hosts can release files shortly after the test process leaves the workspace.
        }
        catch (UnauthorizedAccessException)
        {
            // Antivirus scanning can transiently hold generated test assets on Windows.
        }
    }

    private string GetProjectPath()
    {
        return Path.Combine(testRoot, "Fixture.csproj");
    }

    private string GetSolutionPath()
    {
        return Path.Combine(testRoot, "Fixture.slnx");
    }

    private string GetServicesPath()
    {
        return Path.Combine(testRoot, "Services.cs");
    }

    private static (int Line, int Column) FindPosition(string path, string marker)
    {
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var column = lines[index].IndexOf(marker, StringComparison.Ordinal);
            if (column >= 0)
            {
                return (index + 1, column + 1);
            }
        }

        throw new InvalidOperationException($"Marker '{marker}' was not found in '{path}'.");
    }

    private static Type UnwrapTask(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)
            ? type.GetGenericArguments()[0]
            : type;
    }
}
