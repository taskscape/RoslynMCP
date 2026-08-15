# RoslynMCP

RoslynMCP (CSharpMCP) is a read-only Model Context Protocol server that gives coding agents compiler-accurate C# solution intelligence through Roslyn and `MSBuildWorkspace`.

The design deliberately separates responsibilities:

- Roslyn loads the real solution/project graph and answers compile-time semantic questions.
- MCP exposes small, bounded, structured results to Codex over standard input/output.
- Codex still edits files normally and runs the repository's authoritative builds and tests.

This is preferable to embedding an entire solution or relying only on text search. It resolves overloads, namespaces, generic symbols, interface implementations, partial types, compiler diagnostics, and project references while keeping model context bounded. It does not claim to resolve runtime reflection, convention-only dependency injection, dynamically loaded assemblies, external configuration, database behavior, or distributed routing.

## Projects

- `src\CSharpMcp.Server`: .NET 10 stdio MCP server.
- `tests\CSharpMcp.Server.Tests`: real `MSBuildWorkspace` fixture tests for symbol resolution, references, and implementations.
- `.agents\skills`: 34 canonical portable Agent Skills, one for every exposed MCP tool.
- `.claude\skills`: thin Claude Code adapters that delegate to the canonical skill bodies.
- `installer\CSharpMCP.iss`: per-user Inno Setup package for the self-contained Windows server.
- `scripts\Install-RoslynSkills.ps1`: user- or project-scoped installer for Codex, Claude Code, or both.

Package roles are documented in `Directory.Packages.props`. The server uses the stable `ModelContextProtocol` 2.0.0 release, the compatible Microsoft Extensions 10.0.10 hosting line, and Roslyn 5.6.0.

## Build and test

Run the complete verification process from PowerShell with one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-CSharpMcp.ps1
```

The script first proves that every exposed MCP tool has a valid canonical skill, Claude adapter, and OpenAI MCP dependency manifest. It then restores NuGet packages and the checked-in ApiCompat tool, verifies formatting and analyzer rules, builds the complete solution in Release with one worker, and runs every Roslyn behavior and MCP stdio protocol test. It stops immediately and returns a non-zero exit code when any stage fails.

The equivalent individual commands are:

```powershell
cd C:\Projects\CSharpMCP
dotnet restore .\CSharpMCP.slnx
dotnet tool restore
dotnet build .\CSharpMCP.slnx --no-restore -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
dotnet test .\CSharpMCP.slnx --no-build -m:1
```

## Windows installer

The Inno Setup package is the recommended local-machine installation path for Windows. It publishes an untrimmed, self-contained `win-x64` server, packages the complete MCP runtime, the optional ApiCompat manifest, configuration metadata, all 34 canonical skills, registration helpers, and this README.

Build and verify the installer with one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

The build follows the established local Inno Setup pattern used by the other Taskscape desktop projects: it runs the authoritative repository tests, safely resets only `installer\artifacts`, publishes the server self-contained, dry-runs client discovery, locates `ISCC.exe`, compiles the setup definition, and performs an isolated install/uninstall smoke test with post-install registration disabled. Use `-SkipTests` only after the same source revision has already passed `Test-CSharpMcp.ps1`; use `-SkipInstallerSmokeTest` only when the package itself has already passed on the same artifact.

The generated installer is:

```text
C:\Projects\CSharpMCP\installer\output\CSharpMCP-2.0.0-win-x64-Setup.exe
```

Normal CSharpMCP installation uses `%LOCALAPPDATA%\Programs\CSharpMCP`. The application payload and client configuration remain per-user. Before files are installed or either client is registered, Setup checks every common `dotnet.exe` location for the SDK version pinned by `global.json` (currently 10.0.302). If it is missing, Setup:

1. Downloads the official Microsoft x64 SDK installer from `builds.dotnet.microsoft.com` on an Inno Setup progress page.
2. Rejects the download unless its pinned SHA-256 matches the release artifact.
3. Requests UAC elevation only for Microsoft's system-wide SDK installer.
4. Runs that installer as `/install /quiet /norestart`, accepting Microsoft's success code `0` and treating `3010` as a required restart.
5. Queries `dotnet --list-sdks` again and proceeds only after the required SDK and MSBuild files are visible.

The Microsoft SDK installation itself is silent, but Windows still displays a UAC consent or credential prompt because the supported installer is system-wide. Declining UAC, losing network access, failing checksum validation, or failing post-install detection stops CSharpMCP before its registration helpers run. Machines that already have the pinned SDK do not download anything and do not request elevation.

After the prerequisite gate, Setup performs these operations:

1. Installs the complete self-contained server under `server`.
2. Creates the per-user runtime log directory under `%LOCALAPPDATA%\CSharpMCP\logs`.
3. Writes the resolved stdio command and selected tool catalog to `config\installation.ini`.
4. Deploys canonical skill packages inside the application directory.
5. Copies every missing skill into both `%USERPROFILE%\.agents\skills` for Codex and `%USERPROFILE%\.claude\skills` for Claude Code. A skill is considered installed only when its `SKILL.md` exists; complete existing skills are preserved byte-for-byte, while incomplete directories are populated without deleting unrelated files.
6. Looks for `codex` and `claude` on the current user's `PATH`.
7. Runs `mcp get csharp_roslyn` first and preserves an existing registration unchanged.
8. When no registration exists, adds a user-level stdio registration pointing directly to the installed self-contained executable.
9. Writes exact outcomes to `config\registration-state.json`. A missing client or failed registration is non-fatal and can be repaired later.

The optional setup task **Enable optional API compatibility and architecture tools** sets `CSHARPMCP_TOOL_GROUPS=all` for newly created registrations and attempts to restore Microsoft's ApiCompat tool through the installed manifest. The default installation exposes the focused 32-tool catalog and does not require the optional tool restore.

The server carries its own .NET runtime, but Roslyn still needs the compatible SDK/MSBuild toolset installed by the prerequisite gate to evaluate SDK-style solutions. Private feeds, workloads, proprietary references, analyzers, and source-generator prerequisites remain repository-specific.

For unattended installation:

```powershell
.\installer\output\CSharpMCP-2.0.0-win-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Add `/TASKS="alltools"` to enable both optional tool groups. `/SKIPPOSTINSTALL=1` is reserved for package verification and installs files without copying skills or changing client registrations. Unattended deployment still requires an already elevated process or an interactive UAC response when the SDK is absent; `/VERYSILENT` cannot and should not bypass Windows elevation policy.

Uninstall removes only client registrations that were originally created by the installer and still point to its installed executable. Pre-existing or subsequently changed registrations are preserved. User skill copies are also retained because they may have been customized after installation.

The packaging definition is [installer/CSharpMCP.iss](installer/CSharpMCP.iss). Client behavior is implemented and independently testable in [Register-CSharpMcpClients.ps1](scripts/Register-CSharpMcpClients.ps1), [Unregister-CSharpMcpClients.ps1](scripts/Unregister-CSharpMcpClients.ps1), [Test-InstallerAssets.ps1](scripts/Test-InstallerAssets.ps1), and [Test-InstallerPackage.ps1](scripts/Test-InstallerPackage.ps1).

### Automated GitHub releases

`VERSION` is the authoritative installer release version. Every push to `main` runs `.github/workflows/publish-installer.yml`. When the corresponding `v<VERSION>` release does not exist, the Windows runner executes the complete installer build and verification process, creates a SHA-256 manifest, and publishes both files on a GitHub Release. Subsequent pushes with the same version finish successfully without creating duplicates; a draft left by an interrupted run is resumed after its tag is verified against the pushed commit.

To publish a new version:

1. Change `VERSION` to a new stable three-component value such as `2.1.0`.
2. Commit the version change together with the intended release source.
3. Push that commit to `main`.
4. Confirm that the `v2.1.0` GitHub Release contains both `CSharpMCP-2.1.0-win-x64-Setup.exe` and its `.sha256` manifest.

The workflow uses only the repository-provided `GITHUB_TOKEN` with `contents: write`; no release secret is required. Run the release contract locally with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-ReleaseWorkflow.ps1
```

### Tool coverage

Every advertised tool must be named by a `[ToolCoverage]` behavioral test. `McpCatalogContainsMergedReadOnlyPortfolioWithStructuredContent` compares that coverage set with the catalog, so adding a tool without a test fails the suite. `StdioClientNegotiatesMcp2AndReceivesStructuredTools` separately verifies the default Release server over MCP stdio, including discovery, field-level schemas, catalog limits, feature-gate rejection, trust, progress, structured output, and text fallback. `AllFeatureProfileAdvertisesAndExecutesOptionalTools` starts another server with `CSHARPMCP_TOOL_GROUPS=all`, verifies the 34-tool catalog, and invokes both optional tools through MCP.

| MCP tool | Behavioral test |
| --- | --- |
| `solution_overview` | `OrientationFlowUsageAndSearchToolsReturnCompilerResolvedEvidence` |
| `symbol_info` | `SymbolInfoResolvesDocumentationId` |
| `find_references` | `FindReferencesReturnsSourceLocation` |
| `call_hierarchy` | `OrientationFlowUsageAndSearchToolsReturnCompilerResolvedEvidence` |
| `implementation_map` | `ImplementationMapFindsConcreteType` |
| `type_usage` | `OrientationFlowUsageAndSearchToolsReturnCompilerResolvedEvidence` |
| `diagnostics` | `DependencyImpactAndDiagnosticsExposeExpandedSections` |
| `project_dependencies` | `DependencyImpactAndDiagnosticsExposeExpandedSections` |
| `semantic_search` | `OrientationFlowUsageAndSearchToolsReturnCompilerResolvedEvidence` |
| `unused_symbol_audit` | `UnusedSymbolAuditSeparatesUnusedAndTestOnlyMethods`; `UnusedSymbolAuditCoversTypesPropertiesFieldsAndEvents` |
| `affected_symbols` | `DependencyImpactAndDiagnosticsExposeExpandedSections` |
| `symbol_at_position` | `PositionAndInvocationToolsUseCompilerBinding` |
| `invocation_binding` | `PositionAndInvocationToolsUseCompilerBinding` |
| `member_surface` | `MemberSurfaceAndInheritanceGraphReportTypeRelationships` |
| `inheritance_graph` | `MemberSurfaceAndInheritanceGraphReportTypeRelationships` |
| `rename_preview` | `RenamePreviewChangesImmutableSolutionWithoutWritingFiles`; `RenamePreviewUsesRoslynAndSupportsSignaturePaginationAndFreshness` |
| `diagnostics_delta` | `DiagnosticsDeltaReportsAnIntroducedCompilerError` |
| `test_impact` | `TestImpactFindsTestProjectCaller` |
| `source_generator_inventory` | `SourceGeneratorInventoryFindsGeneratedRegexOutput` |
| `conditional_compilation_matrix` | `ConditionalCompilationMatrixShowsBothBranches` |
| `workspace_health` | `WorkspaceHealthReportsCacheAndCompilationState` |
| `symbol_source` | `SourceEventAndAttributeToolsReturnResolvedEvidence` |
| `event_flow` | `SourceEventAndAttributeToolsReturnResolvedEvidence` |
| `attribute_usage` | `SourceEventAndAttributeToolsReturnResolvedEvidence`; `AttributeUsageReturnsConstructorInheritanceAndMigrationGroups` |
| `dependency_injection_map` | `DependencyInjectionAndConstructionToolsExplainComposition` |
| `construction_options` | `DependencyInjectionAndConstructionToolsExplainComposition` |
| `api_compatibility` | `ApiArchitectureAndContextToolsStayBoundedAndSemantic`; `ApiCompatibilityDelegatesDllBaselinesToOfficialApiCompat` |
| `region_flow` | `RegionFlowAndStackTraceResolveCompilerContext` |
| `architecture_rule_check` | `ApiArchitectureAndContextToolsStayBoundedAndSemantic` |
| `resolve_stack_trace` | `RegionFlowAndStackTraceResolveCompilerContext` |
| `context_bundle` | `ApiArchitectureAndContextToolsStayBoundedAndSemantic` |
| `trust_solution` | `SolutionTrustRequiresExplicitAuthorizationAndSupportsRevocation` |
| `list_trusted_paths` | `SolutionTrustRequiresExplicitAuthorizationAndSupportsRevocation` |
| `revoke_trust` | `SolutionTrustRequiresExplicitAuthorizationAndSupportsRevocation` |

For a durable executable path, publish or build in Release:

```powershell
dotnet build .\CSharpMCP.slnx --configuration Release -m:1 /p:BuildInParallel=false /p:UseSharedCompilation=false
```

## Connect Codex

`AGENTS.md` instructions tell Codex when and why to use Roslyn, but they do not install or expose an MCP server. Register `csharp_roslyn` once on the Codex host, restart the active Codex client, and keep repository-specific usage rules in `AGENTS.md`.

The Windows installer described above performs this registration automatically when `csharp_roslyn` is not already configured. The manual options below are primarily for source checkouts and custom deployments.

Codex desktop, Codex CLI, and the IDE extension share MCP configuration when they use the same Codex host. The server is local and uses stdio, so it must be registered with a command and DLL path rather than an HTTP URL. See the [official Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp.md).

### Prerequisites

1. Install the .NET 10 SDK.
2. Build and verify the Release server:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\Projects\CSharpMCP\scripts\Test-CSharpMcp.ps1
   ```

3. Confirm that this file exists:

   ```text
   C:\Projects\CSharpMCP\src\CSharpMcp.Server\bin\Release\net10.0\CSharpMcp.Server.dll
   ```

### Option 1: register with Codex CLI

This is the simplest user-level installation and is shared with the desktop app and IDE extension on the same host:

```powershell
codex mcp add csharp_roslyn -- dotnet "C:\Projects\CSharpMCP\src\CSharpMcp.Server\bin\Release\net10.0\CSharpMcp.Server.dll"
codex mcp list
codex mcp get csharp_roslyn
```

To expose the two optional tools as well, remove and re-add the registration with the `all` feature profile:

```powershell
codex mcp remove csharp_roslyn
codex mcp add csharp_roslyn --env CSHARPMCP_TOOL_GROUPS=all -- dotnet "C:\Projects\CSharpMCP\src\CSharpMcp.Server\bin\Release\net10.0\CSharpMcp.Server.dll"
```

Use `CSHARPMCP_TOOL_GROUPS=api`, `architecture`, or `api,architecture` instead of `all` when only one specialized group is needed.

### Option 2: user-level config.toml

Add the following to `%USERPROFILE%\.codex\config.toml` to make the server available in every repository opened by this Codex host:

```toml
[mcp_servers.csharp_roslyn]
command = "dotnet"
args = ["C:\\Projects\\CSharpMCP\\src\\CSharpMcp.Server\\bin\\Release\\net10.0\\CSharpMcp.Server.dll"]
startup_timeout_sec = 30
tool_timeout_sec = 300
default_tools_approval_mode = "writes"
enabled = true

# Uncomment to require the API and architecture tools as well.
# [mcp_servers.csharp_roslyn.env]
# CSHARPMCP_TOOL_GROUPS = "all"
```

`writes` allows the read-only analysis tools automatically while prompting for trust-store mutations such as `trust_solution` and `revoke_trust`. Use `auto` only when you intentionally want Codex to manage this local trust store without a prompt.

### Option 3: repository-scoped config.toml

Place the same configuration in `<repository>\.codex\config.toml` when only that trusted repository should expose `csharp_roslyn`. For example:

```toml
# C:\repo\MyApplication\.codex\config.toml
[mcp_servers.csharp_roslyn]
command = "dotnet"
args = ["C:\\Projects\\CSharpMCP\\src\\CSharpMcp.Server\\bin\\Release\\net10.0\\CSharpMcp.Server.dll"]
startup_timeout_sec = 30
tool_timeout_sec = 300
default_tools_approval_mode = "writes"
enabled = true
```

Project-scoped MCP configuration is loaded only for trusted projects. This option is useful when a repository depends on this server but other Codex work should retain a smaller tool catalog.

### Option 4: Codex desktop or IDE settings

1. Open **Settings** and select **MCP servers**.
2. Select **Add server** and choose **STDIO**.
3. Use `csharp_roslyn` as the name.
4. Set the command to `dotnet`.
5. Add this argument:

   ```text
   C:\Projects\CSharpMCP\src\CSharpMcp.Server\bin\Release\net10.0\CSharpMcp.Server.dll
   ```

6. Optionally set `CSHARPMCP_TOOL_GROUPS=all` in the server environment.
7. Save and restart the desktop app or IDE extension.

### Verify and troubleshoot registration

- Run `codex mcp list` and confirm that `csharp_roslyn` is `enabled`.
- Run `codex mcp get csharp_roslyn` and confirm that it points to the Release DLL above.
- In Codex CLI or the desktop composer, use `/mcp` to confirm that the connected server exposes its tools.
- Restart the current Codex app, CLI session, or IDE extension after changing MCP configuration; existing tasks may retain the catalog loaded when they started.
- If Codex reports that `csharp_roslyn` is not discoverable, verify the DLL exists, run the one-command test process, and check for a second project-level configuration that disables or filters the server.
- If analysis reports an untrusted workspace, call `trust_solution`; registration alone does not authorize repository-controlled MSBuild evaluation.

### Runtime logs

The server writes operational logs to stderr because stdout is reserved for MCP protocol messages. It also writes durable daily log files to `%LOCALAPPDATA%\CSharpMCP\logs` by default. Files are named `csharpmcp-YYYYMMDD.log`; a new file is opened each day, oversized files receive a numeric suffix, and Serilog removes files older than 30 days. Multiple MCP server processes can safely append to the same daily file.

Set `CSHARPMCP_LOG_DIRECTORY` in the MCP server environment to use a different absolute directory. This is useful for portable installations and isolated test runs. The configured user must be able to create and write to that directory; a logging initialization failure stops server startup rather than silently discarding runtime diagnostics.

Before the first analysis of a repository, call `trust_solution` with its solution, project, or repository path. Session trust is the default; pass `persist: true` only for repositories you intend to authorize across server restarts. Workspace loading evaluates repository-controlled MSBuild files and can load configured analyzers and source generators, so analysis calls reject untrusted paths. Use `list_trusted_paths` and `revoke_trust` to audit or remove decisions.

### Recommended repository instructions

After registering the server, add durable selection and verification guidance to the target repository's `AGENTS.md`. For example:

```md
## Roslyn code-intelligence workflow

- For non-trivial C# work, prefer the configured `csharp_roslyn` MCP server. If it is unavailable, use an equivalent local Roslyn/MSBuildWorkspace query or the compiler and state the fallback.
- Run `workspace_health` when solution loading may be incomplete and `solution_overview` when the project graph is unfamiliar.
- Run `symbol_info` before changing an unfamiliar symbol.
- Run `find_references`, `affected_symbols`, and `test_impact` before changing a contract or signature.
- Run `implementation_map` before editing an interface, abstract member, handler, or dependency-injection contract.
- After meaningful edits, run `diagnostics` for affected projects and then the repository's authoritative build and relevant tests.
- Treat Roslyn results as compile-time facts, not proof of reflection, dynamic loading, configuration, database behavior, or distributed routing.
```

## Portable Agent Skills for Codex and Claude Code

Codex and Claude Code both implement the open Agent Skills directory format: a skill is a directory whose entry point is `SKILL.md` with `name` and `description` YAML frontmatter. Both clients initially load the small discovery metadata and load the body when the skill is selected, so the detailed workflow can stay out of normal context. The portable core is documented by the [Agent Skills specification](https://agentskills.io/specification), [OpenAI's Codex skills guidance](https://developers.openai.com/plugins/concepts/skills), and [Anthropic's Claude Code skills guidance](https://code.claude.com/docs/en/slash-commands).

The relevant client differences are intentionally isolated:

| Concern | Codex | Claude Code |
| --- | --- | --- |
| Project discovery | `.agents\skills\<name>\SKILL.md` | `.claude\skills\<name>\SKILL.md` |
| User discovery | `%USERPROFILE%\.agents\skills\<name>` | `%USERPROFILE%\.claude\skills\<name>` |
| Explicit invocation | `$skill-name` | `/skill-name` |
| Automatic selection | Matches the frontmatter description | Matches the frontmatter description |
| Client metadata | Optional `agents\openai.yaml` | Claude-specific frontmatter is optional and is not used here |

The canonical source is `.agents\skills`. Each canonical skill contains the exact MCP tool workflow, common programming and refactoring scenarios, output bounds, follow-up tools, and the limits of compile-time evidence. Its `agents\openai.yaml` declares `csharp_roslyn` as an MCP dependency for Codex. The checked-in `.claude\skills` entry with the same name is a thin adapter that reads the canonical body, which prevents the operational guidance from drifting. Claude Code also supports directory symlinks in current releases, but checked-in text adapters are more reliable across Windows clones and Git configurations.

### Install the skills

The checked-in skills are discovered automatically while working in this repository. To make the same canonical skills available in every repository for both clients, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-RoslynSkills.ps1 -Client Both -Scope User
```

To install into one repository instead:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-RoslynSkills.ps1 -Client Both -Scope Project -ProjectPath C:\repo\MyApplication
```

Use `-Client Codex` or `-Client Claude` to install one client only. Existing skill folders are protected by default; pass `-Force` to update files in an existing installation. The installer copies the canonical portable skill package into each client's native discovery directory, so user- and project-scoped installations do not depend on relative adapter paths. Restart the active client session after installation.

Skills teach the agent when and how to use a tool; they do not register the MCP server. Codex registration is described above. Register the same local stdio server for Claude Code with the current CLI option order documented by [Anthropic's MCP guide](https://code.claude.com/docs/en/mcp):

```powershell
claude mcp add --transport stdio --scope user csharp_roslyn -- dotnet "C:\Projects\CSharpMCP\src\CSharpMcp.Server\bin\Release\net10.0\CSharpMcp.Server.dll"
claude mcp list
claude mcp get csharp_roslyn
```

Use `--scope project` to write a shared `.mcp.json` for one repository, or omit `--scope` for Claude's private local-project scope. Project-scoped MCP definitions require approval when first used. To expose both optional tool groups, place `--env CSHARPMCP_TOOL_GROUPS=all` before `csharp_roslyn` in the add command. In a Claude Code session, `/mcp` shows connection and tool status.

### Invoke the skills

Ask in normal engineering language and include the workspace path and stable symbol or source coordinate. Explicit invocation is useful when deterministic tool selection matters:

```text
# Codex
$csharp-roslyn-affected-symbols Plan the impact of changing M:MyApp.IOrderService.PlaceAsync(System.Threading.CancellationToken) in C:\repo\MyApp\MyApp.sln. Do not edit yet.

# Claude Code
/csharp-roslyn-affected-symbols Plan the impact of changing M:MyApp.IOrderService.PlaceAsync(System.Threading.CancellationToken) in C:\repo\MyApp\MyApp.sln. Do not edit yet.
```

Every exposed tool has a matching portable skill:

| MCP tool | Portable skill name |
| --- | --- |
| `solution_overview` | `csharp-roslyn-solution-overview` |
| `symbol_info` | `csharp-roslyn-symbol-info` |
| `find_references` | `csharp-roslyn-find-references` |
| `call_hierarchy` | `csharp-roslyn-call-hierarchy` |
| `implementation_map` | `csharp-roslyn-implementation-map` |
| `type_usage` | `csharp-roslyn-type-usage` |
| `diagnostics` | `csharp-roslyn-diagnostics` |
| `project_dependencies` | `csharp-roslyn-project-dependencies` |
| `semantic_search` | `csharp-roslyn-semantic-search` |
| `unused_symbol_audit` | `csharp-roslyn-unused-symbol-audit` |
| `affected_symbols` | `csharp-roslyn-affected-symbols` |
| `symbol_at_position` | `csharp-roslyn-symbol-at-position` |
| `invocation_binding` | `csharp-roslyn-invocation-binding` |
| `member_surface` | `csharp-roslyn-member-surface` |
| `inheritance_graph` | `csharp-roslyn-inheritance-graph` |
| `rename_preview` | `csharp-roslyn-rename-preview` |
| `diagnostics_delta` | `csharp-roslyn-diagnostics-delta` |
| `test_impact` | `csharp-roslyn-test-impact` |
| `source_generator_inventory` | `csharp-roslyn-source-generator-inventory` |
| `conditional_compilation_matrix` | `csharp-roslyn-conditional-compilation-matrix` |
| `workspace_health` | `csharp-roslyn-workspace-health` |
| `symbol_source` | `csharp-roslyn-symbol-source` |
| `event_flow` | `csharp-roslyn-event-flow` |
| `attribute_usage` | `csharp-roslyn-attribute-usage` |
| `dependency_injection_map` | `csharp-roslyn-dependency-injection-map` |
| `construction_options` | `csharp-roslyn-construction-options` |
| `api_compatibility` | `csharp-roslyn-api-compatibility` |
| `region_flow` | `csharp-roslyn-region-flow` |
| `architecture_rule_check` | `csharp-roslyn-architecture-rule-check` |
| `resolve_stack_trace` | `csharp-roslyn-resolve-stack-trace` |
| `context_bundle` | `csharp-roslyn-context-bundle` |
| `trust_solution` | `csharp-roslyn-trust-solution` |
| `list_trusted_paths` | `csharp-roslyn-list-trusted-paths` |
| `revoke_trust` | `csharp-roslyn-revoke-trust` |

Run the skill-only contract test during authoring with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-RoslynSkills.ps1
```

It derives the authoritative MCP catalog from `RoslynTools.cs` and verifies one-to-one canonical and adapter coverage, portable frontmatter, exact tool references, explicit Codex prompts, and the `csharp_roslyn` dependency declaration. The normal one-command verification process runs this contract first.

## MCP 2.0 protocol use

The server uses the stable Microsoft C# SDK 2.0.0 and negotiates the MCP `2026-07-28` protocol through discovery-first negotiation, while the SDK retains down-level interoperability. The implementation uses the parts of MCP 2.0 that improve a local stdio language service:

- Every tool returns a typed `McpToolResponse` directly and declares a compact, tool-specific `OutputSchemaType`. The SDK therefore advertises field-level JSON Schema 2020-12 documents and emits `structuredContent` without the legacy `{ "result": ... }` wrapper.
- `data` contains the documented fields for that tool. `metadata` is schema-validated and contains `kind`, `returned`, `truncated`, `workspaceLoadedAt`, `workspaceDiagnostics`, and `nextCursor`. Only deliberately variable nested Roslyn records remain open JSON values. The SDK also keeps a text content fallback for older clients.
- Tool titles improve display and selection, and `openWorld: false` accurately marks all operations as limited to the caller-selected local workspace or the server trust store.
- Server discovery includes a human-readable title and description in addition to its stable name and version.
- The static, user-independent tool catalog carries a public ten-minute cache hint, reducing repeated transmission of the full schema while keeping upgrades quick to discover.
- `solution_overview`, `diagnostics`, `source_generator_inventory`, `conditional_compilation_matrix`, `workspace_health`, `api_compatibility`, `architecture_rule_check`, and `context_bundle` emit request-scoped progress when the client supplies a progress token.
- Cancellation tokens continue through every async Roslyn operation, so a cancelled MCP request cancels workspace analysis rather than leaving detached work behind.

HTTP statelessness, standardized HTTP headers, and OAuth are not applicable to this stdio-only local server. MCP Apps add a user-interface surface rather than compiler facts. The separate Tasks extension is intentionally not registered: existing queries are bounded, cancellable request/response operations, and task polling would add client and server state without improving their semantic accuracy. If a future full-solution operation cannot be bounded to the normal tool timeout, it should be independently justified before adding the Tasks package.

The wire contract is exercised by an SDK client integration test, including protocol negotiation, server identity, non-empty field-level schemas for every default tool, a bounded catalog and result defaults, direct structured output, text fallback, closed-world annotations, and progress notifications. See the [Microsoft C# SDK 2.0 release notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0) and [MCP C# SDK tool documentation](https://csharp.sdk.modelcontextprotocol.io/concepts/tools/tools.html).

## Tools

| Tool | Purpose |
| --- | --- |
| `solution_overview` | Projects, target frameworks, references, analyzers, and entry points. |
| `symbol_info` | Exact declaration, stable ID, type relationships, modifiers, and documentation. |
| `find_references` | Compiler-bound references with server-side kind filters and invocation/read/write/type/DI/test classifications. |
| `call_hierarchy` | Depth-limited callers and callees. |
| `implementation_map` | Interface/abstract contract implementations and member overrides. |
| `type_usage` | Construction sites, DI-registration candidates, API signatures, and other type uses. |
| `diagnostics` | Compiler/analyzer diagnostics filtered by project, document, ID, severity, and suppression state, including responsible analyzer type/assembly identity. |
| `project_dependencies` | Direct/transitive/reverse project and package dependencies, cycle detection, and optional semantic namespace edges. |
| `semantic_search` | Source symbol-name matches ranked with qualified identity, kind, and documentation context. |
| `unused_symbol_audit` | Bounded no-reference and test-only type/member candidates with framework exclusions and runtime-risk flags. |
| `affected_symbols` | Independently bounded contracts, implementations, references, callers, tests, and dependent projects before a contract edit. |
| `symbol_at_position` | Exact bound, declared, enclosing, type, and candidate symbols at a source coordinate. |
| `invocation_binding` | Selected callable, receiver, generic arguments, conversions, and argument-to-parameter mapping. |
| `member_surface` | Explicit constructor/overload/operator/extension modes, including applicable source and referenced-assembly extension methods. |
| `inheritance_graph` | Depth-limited base, derived, interface, and implementation edges. |
| `rename_preview` | Roslyn Renamer or compiler-bound signature previews with paged edits, checksums, stale-fingerprint rejection, conflicts, and introduced diagnostics; never writes files. |
| `diagnostics_delta` | Capture and compare short-lived compiler/analyzer diagnostic baselines. |
| `test_impact` | Reverse compiler-reference paths from changed symbols/documents to test-project methods. |
| `source_generator_inventory` | Generator candidates and bounded generated documents, declarations, diagnostics, and excerpts, optionally selecting one generated document by ID/hint name. |
| `conditional_compilation_matrix` | Separate MSBuild configuration/target-framework evaluations plus explicit preprocessor-symbol variants. |
| `workspace_health` | Cache freshness, evaluated configuration/target framework, skipped projects, load timing, MSBuild identity, and optional compilation checks. |
| `symbol_source` | Bounded original declarations for a batch of symbols, with per-item resolution status. |
| `event_flow` | Compiler-bound subscribe, unsubscribe, raise, and handler evidence for an event. |
| `attribute_usage` | Exact attribute constructor identity, arguments, inherited applications, and optional obsolete-migration groups. |
| `dependency_injection_map` | Generic, `typeof`, and factory registration shapes with lifetime and confidence. |
| `construction_options` | Constructors, factories, required members, DI registrations, and project accessibility. |
| `api_compatibility` | Feature-gated API surface comparison; DLL baselines run Microsoft's official ApiCompat rules. |
| `region_flow` | Roslyn data-flow and control-flow facts for a bounded statement range. |
| `architecture_rule_check` | Feature-gated caller-supplied semantic namespace layering rules and grouped violations. |
| `resolve_stack_trace` | Source-symbol mapping for .NET stack frames, including common compiler name mangling. |
| `context_bundle` | Goal-specific `understand`, `contract-change`, or `debug-flow` compound context. |
| `trust_solution` | Authorize a repository for MSBuild, analyzer, and source-generator evaluation. |
| `list_trusted_paths` | List session and persistent trust roots. |
| `revoke_trust` | Remove session and persistent trust for one root. |

Every symbol result prefers a Roslyn documentation comment ID such as `T:Namespace.Type` or `M:Namespace.Type.Method(System.String)`. Results also include project ID, project name, path, line/column, and a short excerpt. High-volume defaults are at most 50. `truncated` and `nextCursor` report whether the caller should narrow or page; rename previews, API comparisons, and generated-source inventories expose stable opaque cursors.

## Using the tools from Codex

Prompts do not need to reproduce the JSON input schema. State the engineering goal, identify the workspace and symbol or source position, and explicitly name `csharp_roslyn` when tool selection matters. Prefer a `.sln` or `.slnx` path for whole-repository questions and a `.csproj` path for tightly scoped analysis.

A useful first prompt for a repository is:

```text
Use csharp_roslyn trust_solution to grant session-only trust to C:\repo\MyApplication\MyApplication.sln. Then run workspace_health and solution_overview in Release configuration. Stop and report workspace-load diagnostics if the solution is incomplete.
```

For a non-trivial contract edit, a good combined prompt is:

```text
Use csharp_roslyn to inspect M:MyApplication.Services.OrderService.PlaceOrderAsync(MyApplication.Order,System.Threading.CancellationToken). Run symbol_info, find_references, implementation_map, affected_symbols, and test_impact before proposing changes. Keep every result bounded to 30 items, distinguish compiler facts from runtime risks, and do not edit files yet.
```

After making an edit, ask for both semantic and executable verification:

```text
Use csharp_roslyn diagnostics for the affected projects with analyzers enabled and compare against the diagnostics_delta baseline captured before the edit. Then run the repository's normal build and relevant tests. Report newly introduced diagnostics separately from pre-existing diagnostics.
```

### Sample prompt for every exposed tool

Replace the example paths, project names, symbols, and line numbers with values from the repository being analyzed.

- `solution_overview`

  ```text
  Use csharp_roslyn solution_overview on C:\repo\MyApplication\MyApplication.sln in Release configuration. Summarize projects, target frameworks, project references, entry points, nullable settings, and workspace-load diagnostics; return at most 30 projects.
  ```

- `symbol_info`

  ```text
  Use csharp_roslyn symbol_info for T:MyApplication.Services.OrderService in C:\repo\MyApplication\MyApplication.sln. Report its exact identity, accessibility, modifiers, base type, interfaces, documentation, declaring project, and source locations.
  ```

- `find_references`

  ```text
  Use csharp_roslyn find_references for M:MyApplication.Services.OrderService.PlaceOrderAsync(MyApplication.Order,System.Threading.CancellationToken). Classify invocations, method groups, reads or writes, nameof/typeof uses, DI candidates, and test references; return at most 40 occurrences.
  ```

- `call_hierarchy`

  ```text
  Use csharp_roslyn call_hierarchy for M:MyApplication.Services.OrderService.PlaceOrderAsync(MyApplication.Order,System.Threading.CancellationToken), direction both, depth 2, maximum 40 edges. Explain the main callers and direct callees without claiming reflection-based calls are complete.
  ```

- `implementation_map`

  ```text
  Use csharp_roslyn implementation_map for T:MyApplication.Services.IOrderService. List concrete implementations and member overrides with exact symbol IDs, projects, and source locations.
  ```

- `type_usage`

  ```text
  Use csharp_roslyn type_usage for T:MyApplication.Services.OrderService. Separate construction sites, DI-registration candidates, public API signatures, and other compiler-bound type references.
  ```

- `diagnostics`

  ```text
  Use csharp_roslyn diagnostics on project MyApplication.Core with analyzers enabled, minimum severity warning, including suppressed diagnostics. Return at most 50 results and include diagnostic ID, analyzer assembly/type, source location, and workspace-load warnings.
  ```

- `project_dependencies`

  ```text
  Use csharp_roslyn project_dependencies on C:\repo\MyApplication\MyApplication.sln with namespace edges enabled. Report direct and transitive project dependencies, reverse dependants, NuGet packages, cycles, and the strongest cross-namespace edges.
  ```

- `semantic_search`

  ```text
  Use csharp_roslyn semantic_search for the concept "order retry policy" in project MyApplication.Core. Limit results to methods and named types and return the 25 best compiler symbols with their qualified identities and locations.
  ```

- `unused_symbol_audit`

  ```text
  Use csharp_roslyn unused_symbol_audit on project MyApplication.Core for named types, methods, properties, fields, and events. Return at most 40 no-reference or test-only candidates, preserve runtime-discovery risk flags, and do not describe a candidate as safe to delete without reviewing those risks.
  ```

- `affected_symbols`

  ```text
  Use csharp_roslyn affected_symbols for M:MyApplication.Contracts.IOrderService.PlaceOrderAsync(MyApplication.Order,System.Threading.CancellationToken). Independently return up to 20 contracts, implementations, references, callers, tests, and dependent projects.
  ```

- `symbol_at_position`

  ```text
  Use csharp_roslyn symbol_at_position for C:\repo\MyApplication\src\OrderController.cs at line 84, column 27. Show the bound, declared, enclosing, and candidate symbols and their stable IDs.
  ```

- `invocation_binding`

  ```text
  Use csharp_roslyn invocation_binding for the invocation at C:\repo\MyApplication\src\OrderController.cs line 84, column 27. Explain the selected overload, receiver type, generic arguments, conversions, and argument-to-parameter mapping.
  ```

- `member_surface`

  ```text
  Use csharp_roslyn member_surface for T:MyApplication.Services.OrderService in overloads mode for member PlaceOrderAsync. Include inherited members, explicit interface implementations, accessibility, overrides, and declaring types; return at most 40 members.
  ```

- `inheritance_graph`

  ```text
  Use csharp_roslyn inheritance_graph for T:MyApplication.Domain.OrderHandler in both directions to depth 3. Include interfaces and report base-type, derived-type, interface-inheritance, and implementation edges.
  ```

- `rename_preview`

  ```text
  Use csharp_roslyn rename_preview with refactorKind rename to rename T:MyApplication.Services.OrderService to PurchaseOrderService. Return paged per-document edits, conflicts, introduced diagnostics, checksums, and the snapshot fingerprint. Do not write any files.
  ```

- `diagnostics_delta`

  ```text
  Use csharp_roslyn diagnostics_delta on MyApplication.Core with analyzers enabled and no baseline token to capture a baseline. Give me the token, and after my edits use the same tool with that token to report only introduced, resolved, or changed diagnostics.
  ```

- `test_impact`

  ```text
  Use csharp_roslyn test_impact for M:MyApplication.Services.OrderService.PlaceOrderAsync(MyApplication.Order,System.Threading.CancellationToken), depth 3, maximum 40 tests. Include the compiler-reference evidence path for every candidate and label the result as static reachability rather than runtime coverage.
  ```

- `source_generator_inventory`

  ```text
  Use csharp_roslyn source_generator_inventory for project MyApplication.Core. List generator candidates, generated document IDs and hint names, generator diagnostics, and declarations; include only bounded excerpts and return the next cursor when truncated.
  ```

- `conditional_compilation_matrix`

  ```text
  Use csharp_roslyn conditional_compilation_matrix for MyApplication.Core across Debug and Release, target frameworks net8.0 and net10.0, and symbol sets FEATURE_ALPHA and empty. For ConditionalService.cs, compare declared symbols, inactive regions, and diagnostics in each evaluated variant.
  ```

- `workspace_health`

  ```text
  Use csharp_roslyn workspace_health for C:\repo\MyApplication\MyApplication.sln in Release for net10.0 with project checks enabled. Report expected and skipped projects, evaluated configuration/framework, cache freshness, load diagnostics, and whether semantic queries are complete enough to trust.
  ```

- `symbol_source`

  ```text
  Use csharp_roslyn symbol_source for M:MyApplication.Services.OrderService.PlaceOrderAsync(MyApplication.Order,System.Threading.CancellationToken) and P:MyApplication.Domain.Order.Status. Include bodies, XML documentation, attributes, and signatures, but cap each declaration at 100 lines and 12000 characters.
  ```

- `event_flow`

  ```text
  Use csharp_roslyn event_flow for E:MyApplication.Events.OrderPublisher.OrderPlaced. Report every subscribe, unsubscribe, and raise site with resolved handler identity and exact locations.
  ```

- `attribute_usage`

  ```text
  Use csharp_roslyn attribute_usage for T:System.ObsoleteAttribute across the solution. Include exact constructor identities, positional and named arguments, inherited applications, and migration groups by message and error severity.
  ```

- `dependency_injection_map`

  ```text
  Use csharp_roslyn dependency_injection_map for service T:MyApplication.Services.IOrderService. Find generic, typeof-pair, and factory registrations, classify lifetime and registration shape, and label convention-based matches with their confidence.
  ```

- `construction_options`

  ```text
  Use csharp_roslyn construction_options for T:MyApplication.Services.OrderService from project MyApplication.Tests. Report accessible constructors, required members, static factories, DI registrations, and whether InternalsVisibleTo makes each option callable from the test project.
  ```

- `api_compatibility` (optional `api` group)

  ```text
  Use csharp_roslyn api_compatibility for project MyApplication.Contracts against C:\baselines\MyApplication.Contracts.dll. Run official ApiCompat rules, return at most 50 changes, and separate breaking from non-breaking results. Do not modify the baseline.
  ```

- `region_flow`

  ```text
  Use csharp_roslyn region_flow on C:\repo\MyApplication\src\OrderService.cs from line 120, column 9 through line 145, column 10 with kind both. Report Roslyn data-flow reads, writes, captures and flows in/out plus control-flow entry, exit, return, and reachability facts.
  ```

- `architecture_rule_check` (optional `architecture` group)

  ```text
  Use csharp_roslyn architecture_rule_check with the rule "Domain cannot depend on Web": fromNamespace MyApplication.Domain, forbid MyApplication.Web. Return grouped compiler-resolved violations with reference counts and a few exact source examples.
  ```

- `resolve_stack_trace`

  ```text
  Use csharp_roslyn resolve_stack_trace on C:\repo\MyApplication\MyApplication.sln for the following .NET stack trace. Resolve async state machines, lambdas, local functions, nested types, and generic arity to source symbols and locations: <paste stack trace>
  ```

- `context_bundle`

  ```text
  Use csharp_roslyn context_bundle for T:MyApplication.Services.OrderService with profile contract-change and at most 15 results per section. Summarize the symbol, source, members, hierarchy, references, implementations, tests, and recommended next tool.
  ```

- `trust_solution`

  ```text
  Use csharp_roslyn trust_solution to grant session-only trust to C:\repo\MyApplication\MyApplication.sln. Do not persist the decision. Then confirm the normalized trusted root.
  ```

- `list_trusted_paths`

  ```text
  Use csharp_roslyn list_trusted_paths and show the normalized repository roots, which are session-trusted, and which are persisted.
  ```

- `revoke_trust`

  ```text
  Use csharp_roslyn revoke_trust for C:\repo\MyApplication\MyApplication.sln. Confirm whether session and persistent trust were each removed; do not change repository files.
  ```

## Optional tool groups

The default catalog contains 32 focused tools. API compatibility and architecture policy are intentionally feature-gated because they are specialized and add schema/context cost. Enable one or both before starting the server:

```powershell
$env:CSHARPMCP_TOOL_GROUPS = "api,architecture"
dotnet .\src\CSharpMcp.Server\bin\Release\net10.0\CSharpMcp.Server.dll
```

Use `all` to enable every optional group. Hidden tools are both omitted from `tools/list` and rejected if called directly. `api_compatibility` requires the checked-in .NET tool manifest; run `dotnet tool restore` after cloning or updating the repository.

## Cache and correctness

The server caches one `MSBuildWorkspace` per workspace path, evaluated configuration, and target framework. A recursive file watcher invalidates snapshots when relevant C#, project, solution, props, targets, configuration, or JSON files change. The next tool call reloads the workspace so post-edit diagnostics do not silently query an old immutable solution.

Workspace load failures are returned separately from compiler/analyzer diagnostics. Treat any load diagnostic as evidence that the semantic model can be incomplete, usually because an SDK, workload, private feed, generated asset, or proprietary reference is unavailable.

`affected_symbols` is intentionally conservative. It collects source symbols that reference, implement, override, or contain the target; it is not a speculative whole-program proof. `type_usage` labels convention-shaped `Add...` invocations as DI candidates rather than claiming runtime registration certainty.

`unused_symbol_audit` builds one semantic reference index across the loaded solution, rather than running a whole-solution reference search once per candidate. It supports types, methods, properties, fields, and events; excludes generated declarations, entry points, overrides, interface implementations, virtual/abstract contracts, MVC/Razor actions, attributed handlers, and known convention entry points; and returns explicit runtime-discovery risk flags. Its output is a removal-candidate list: public/protected APIs and runtime-only discovery still require explicit review.

`rename_preview` changes only an immutable in-memory `Solution`; Codex remains responsible for normal source patches. Signature previews cascade declarations and compiler-bound call sites but deliberately insert `default(Type)` for newly required arguments, which must be reviewed. `diagnostics_delta` tokens are process-local and bounded to the most recent twenty captures. `conditional_compilation_matrix` loads separate MSBuild configurations and target frameworks before applying each bounded preprocessor-symbol variant.
