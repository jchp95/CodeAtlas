# CodeAtlas + StackPilot — MVP 0.23.0

macOS desktop developer control center that combines a block-based architecture map, Git snapshot comparisons, local microservice operations, HTTP endpoint testing, and an optional AI engineering assistant.

## What's new in 0.22.0

- Shows the complete HTTP catalog diagnostic list instead of silently hiding every warning after the first one. This includes unresolved dynamic route prefixes, missing `.http` files, unmatched requests, ambiguous outgoing calls and source parsing failures.
- Adds a bounded, scrollable diagnostics area with a warning count, readable multi-line messages, selectable text and severity cues; diagnostics remain tied to the currently loaded catalog.
- Synchronizes the visible app version and LSP client identity with the release version.
- Keeps route discovery conservative: unresolved dynamic routes are reported, not presented as complete endpoints.
- Validation: portable Swift/Node regression suites and source parsing run in this environment; full SwiftUI and .NET engine builds still require macOS with the required SDKs.

## What's new in 0.23.0

- Fixes a build/test console output edge case where UTF-8 characters split across pipe reads could be dropped because each chunk was decoded independently.
- Adds a streaming UTF-8 decoder that buffers incomplete multi-byte sequences and flushes remaining bytes when the stream closes.
- Adds regression coverage that splits accented text and emoji at arbitrary byte boundaries.
- Validation: portable UTF-8 decoder tests and the existing Swift/Node regression suites. Full SwiftUI/.NET builds remain a macOS SDK validation step.

## What's new in 0.22.0

- Shows the complete HTTP catalog diagnostic list instead of silently hiding every warning after the first one. This includes unresolved dynamic route prefixes, missing `.http` files, unmatched requests, ambiguous outgoing calls and source parsing failures.
- Adds a bounded, scrollable diagnostics area with a warning count, readable multi-line messages, selectable text and severity cues; diagnostics remain tied to the currently loaded catalog.
- Synchronizes the visible app version and LSP client identity with the release version.

## What's new in 0.21.0

- Resolves cross-file `const string` route values for controllers, Minimal APIs and FastEndpoints when unambiguous.
- Resolves nested `MapGroup` prefixes independent of declaration order and applies prefixes to `MapMethods`.
- Avoids publishing misleading partial routes for dynamic/unresolved group prefixes and emits file/line diagnostics.
- Splits Next.js endpoint detection and route resolution into dedicated engine files and adds route regression coverage.

## What's new in 0.20.0

- Adds bounded process execution shared by engine and Git commands. Both stdout and stderr are drained concurrently, output is capped, exit codes are preserved and cancellation terminates the child process.
- Workspace scanning can be cancelled from the progress strip. The coordinator waits for the prior scan to finish before starting a replacement, preventing multiple dotnet processes from racing over the same NuGet cache.
- Moves snapshot JSON decoding, multi-repository merging and changed-file fingerprint hashing off the SwiftUI main actor.
- Uses the compiled `CodeAtlas.Engine.dll` produced by the mandatory startup build for subsequent commands instead of invoking `dotnet run` for each engine request. A clean source checkout retains a fallback until the first build.
- Makes Git fingerprint paths NUL-safe and avoids holding the full binary diff in memory; changed tracked files are hashed individually with a bounded fallback for very large files.
- Adds regression tests for process output, non-zero exit status, output bounds, cancellation, engine assembly selection and workspace cancellation wiring.
- The macOS/.NET full integration build must still be run on macOS; Linux verification covers the portable process tests and source-level checks only.

## What's new in 0.19.0

- Adds an LSP client over stdio with JSON-RPC request correlation, Content-Length framing, request timeouts and payload bounds.
- Adds optional C#, TypeScript/JavaScript, Swift, Python and JSON server detection without automatic installation.
- Shows real language-server diagnostics in the right inspector; adds an explicit IntelliSense action and semantic go-to-definition where the configured server supports them.
- Synchronizes document changes after a short debounce, sends didSave, and keeps the heuristic outline separate from LSP semantics.
- Rejects workspace edits and server-requested commands from the language server; all changes remain subject to explicit user approval.
- Includes an integration test with a fake LSP server, covering the stdio protocol instead of only checking for source files.
- Run `./scripts/language-server-test.sh` to validate the protocol and client locally.

## What's new in 0.15.0

- Adds a build/test dock with live output and process cancellation for detected .NET, npm and Swift targets.
- Adds standalone StackPilot Profiles and Diagnostics pages, with per-profile lifecycle controls, service health results and direct access to logs.
- Adds a Git workbench for local branches, guarded checkout, status, staged/unstaged diffs, recent history and optional GitHub pull-request listing through the authenticated `gh` CLI.
- Adds extension-aware formatting integrations for C#, Swift, JS/TS, JSON, Markdown, YAML, HTML/CSS and shell. Formatters are invoked only when the user asks and external package installs are disabled.
- Adds provider-backed AI code completion at the caret. Suggestions are reviewed before insertion and do not write a source file automatically.
- Keeps the code explorer, source-linked analysis and FastEndpoints detection introduced in 0.14.0.
- The native editor now supports on-demand LSP completion, source diagnostics and definition navigation for installed servers; automatic inline completion, code actions, rename and a full terminal remain planned work.

## What's new in 0.14.0

- Adds a multi-root file explorer and native editable source view with line numbers, search, save/reload, unsaved-change protection and a right-hand inspector.
- Links source-backed findings from endpoints, outgoing HTTP calls and business-flow steps into the editor at the relevant file and line.
- Detects FastEndpoints route declarations and improves nested Minimal API group-prefix resolution and controller methods with multiple HTTP verb attributes.
- Exposes per-repository workspace-analysis progress without performing the .NET scan on the SwiftUI main thread.
- Adds a runnable regression test for workspace file-tree filtering and places it in the startup gate.
- This is an incremental IDE foundation; the LSP-backed editor was added in 0.19.0. Native full build and .NET integration suite must be verified on macOS with .NET 10 / Xcode.

## What's new in 0.13.1

- Fixes source-backed distributed tracing when Roslyn cannot resolve a local method due to incomplete project metadata; the fallback requires a unique declared receiver type and method signature.

- Refactors the .NET engine into vertical slices for architecture analysis, business flows, HTTP testing, StackPilot and workspace/Git inspection.
- Splits the former 40–100 KB implementation files into responsibility-focused partial types, models, utilities and focused UI extensions without intending to change business rules or CLI/JSON contracts.
- Moves SwiftUI screens and reusable components into `Views/` and feature models/services into `Features/`; the engine process boundary is isolated in `Infrastructure/EngineClient.swift`.
- Adds `docs/CODE_STRUCTURE.md` and `scripts/vertical-slice-structure-test.sh`; the startup gate blocks launch if the refactored layout regresses.
- Run `./scripts/vertical-slice-structure-test.sh` and the focused Swift tests locally. Run `./scripts/verify-before-launch.sh` on macOS to require the .NET 10 build, regression suite and native SwiftUI build before launch.

Read [`docs/CODE_STRUCTURE.md`](docs/CODE_STRUCTURE.md) for the directory map and contribution rules.

## What's new in 0.11.0

- Introduces a shared adaptive design system with a blurred workspace backdrop, regular-material glass cards, soft borders and restrained shadows; visual tokens adapt to system/light/dark appearance.
- Removes the forced dark appearance. A persisted appearance selector in the sidebar supports System, Light and Dark and is coordinated with the application root.
- Reorganizes navigation into collapsible groups: Inicio, Arquitectura, Desarrollo y pruebas, Entorno local and Inteligencia. Workspace roots and discovered projects remain visible in the sidebar.
- Separates the combined dashboard into focused **Resumen**, **Mapa de arquitectura**, **Relaciones y dependencias**, **Flujos de negocio**, **Análisis y evidencias**, **Cambios recientes**, **Endpoints HTTP**, and StackPilot views.
- Adds a consistent section header, status footer and shortcut cards that navigate to architecture, business flows, HTTP testing and local operations.
- Applies adaptive glass styling across architecture, flow, endpoint, AI and StackPilot surfaces while preserving solid, mode-aware backgrounds for text editors and request bodies to keep code readable.
- Adds `docs/VISUAL_SYSTEM_AND_NAVIGATION.md` and `scripts/ui-structure-test.sh` for navigation/theme/source-syntax regression checks.
- Run `./scripts/ui-structure-test.sh` to verify the navigation groups, appearance settings, shared design primitives, status bar and Swift source syntax.

## What's new in 0.10.0

- Groups commonly named Clean Architecture .NET projects (for example `ZasExpress.Api`, `.Application`, `.Domain`, `.Infrastructure`) into one service block; choose a layer to inspect its project details.
- Adds infrastructure blocks for detected RabbitMQ/MassTransit, MinIO, Redis, SQL Server, PostgreSQL, MongoDB, Meilisearch, Kafka and Elasticsearch candidates, with inferred evidence-linked relationships.
- Links infrastructure blocks to explicitly configured, uniquely matching Docker Compose services (including across selected repositories) so RabbitMQ/MinIO can be started and stopped from the diagram without guessing generic service names.
- Adds a multi-root workspace merger with namespaced project IDs, cross-repository event-contract links, a repository filter in Flujos, and owner-root-aware HTTP source opening/execution.
- Updates StackPilot to initialize/manage profiles for multiple selected roots from one operations dashboard; generated launch templates remain disabled until reviewed.
- Adds the AI settings and engineering-assistant menu with Ollama local and Anthropic API providers, model discovery, quick prompts for architecture/change/risk/test analysis, and bounded structured context.
- Stores Anthropic API keys in macOS Keychain; AI requests are explicit user actions and do not automatically attach source code, `.http` bodies or private environment files.
- Adds pure Swift regression tests for clean-layer grouping and multi-repository snapshot merging.

Read [`docs/VISUAL_SYSTEM_AND_NAVIGATION.md`](docs/VISUAL_SYSTEM_AND_NAVIGATION.md), [`docs/AI_INTEGRATION.md`](docs/AI_INTEGRATION.md), [`docs/CLEAN_ARCHITECTURE_AND_INFRA.md`](docs/CLEAN_ARCHITECTURE_AND_INFRA.md), and [`docs/HTTP_ENDPOINT_EXPLORER.md`](docs/HTTP_ENDPOINT_EXPLORER.md) before using these workflows.

## What is new in 0.9.0

- Adds an **Endpoints HTTP** menu grouped by Git repository, with separate lists for discovered endpoints, `.http` / `.rest` requests, and outgoing HTTP client calls.
- Associates Rider / VS Code REST Client requests with endpoints by method and normalized route. If association fails, requests remain independently executable.
- Lets you test a discovered endpoint from its source-derived sample body, edit method / URL / headers / body, execute it, inspect status / headers / body, and generate a reusable `.http` file.
- Parses the common `.http` subset: `###`, method + URL, headers, body, `@variables`, and `http-client.env.json` / `http-client.private.env.json` environments. Nested repository environment files override parent defaults; private settings override public settings at the same folder.
- Adds best-effort cross-service call discovery for common C# `HttpClient` methods and TypeScript `fetch` / Axios-style calls. Matching method + path to another project’s endpoint is shown as an **inferred candidate**, not runtime proof.
- The direct request runner requires an absolute HTTP/HTTPS URL, uses a 30-second timeout, and limits captured response bodies to 512 KiB. Requests that write data may modify the selected environment; inspect the target before running.
- HTTP request/test catalog contents are loaded on demand and are not stored in architecture snapshots. No se guardan los valores de las peticiones en snapshots. Asegúrate de excluir del control de versiones los archivos de entorno privados que contengan credenciales reales.

## What is new in 0.8.0

- Adds a `distributedFlows` snapshot model that joins a producer code path, an inferred RabbitMQ/MassTransit transport segment, and a consumer code path when source contracts match across discovered projects.
- Detects `IConsumer<T>` implementations as message-consumer entry points and traces their `Consume` method into local calls such as projection/service methods.
- Distributed path steps retain phase, owning project, source file/line, confidence and explanatory details; the inspector can jump from the flow to either producer or consumer service.
- The Flujos view now separates **Flujos entre microservicios** from **Flujos dentro de cada servicio**, with search across contract, services, entry points and evidence.
- Snapshot comparison reports added, removed or changed distributed paths. If the producer stops publishing a contract consumed by another service, the previously matched path is recorded as removed.
- Adds a consumer implementation fixture and assertions for `POST /api/cargo` → `CargoRegistered` → `ZasTrack.Api` → `CargoProjection.ApplyAsync`.
- Keeps `distributedFlows`, relation `contract`, and `distributedFlowChanges` optional so older snapshots remain readable.

## What is new in 0.7.0

- New **Flujos de negocio** workspace view with a searchable, step-by-step path from declared HTTP endpoints to request handlers, DI-selected service implementations, and message publication where the source can be resolved.
- Roslyn call tracing uses method symbols and source references, with depth/step limits and confidence/evidence on each step.
- A bounded repository-wide C# source type index helps retain fully qualified message contract names when a project reference assembly is unavailable, without pretending that unresolved symbols compiled successfully.
- Compares flow steps between snapshots; line-only shifts do not count as business-flow changes.
- Indexes Next.js routes as route-only entries; the TypeScript route body trace is a future increment and is explicitly marked as such.
- TypeScript analyzer now searches local and common global compiler installations and the smoke fixture contains two packages with a path-alias import.
- Adds smoke assertions for `POST /api/cargo` → `RegisterCargoCommandHandler` → `TrackingClient.RegisterAsync` → `Zas.Contracts.CargoRegistered`.

## What is new in 0.6.1

- Fixes the C# fixture smoke test by including the expected `/api/health` minimal endpoint.
- Detects typed parameters in C# 12 primary constructors on classes and structs as constructor-injection candidates/evidence.
- Reports TypeScript compiler absence as a skipped optional analysis instead of a successful cross-package test.

## What is new in 0.6.0

- Builds a Roslyn `CSharpCompilation` and semantic models from each discovered project source set using trusted platform assemblies plus compile references recorded in `obj/project.assets.json` when available.
- Resolves message contract symbols to fully-qualified type names when possible, with conservative simple-name fallback only when there are no competing qualified contracts.
- Detects ASP.NET Core endpoint candidates, MassTransit `IConsumer<T>` contracts and `Publish` / `Send` candidates with file and line evidence; resolved method symbols are checked to avoid unrelated methods named `Publish`/`Send`.
- Detects common generic DI registrations (`AddScoped`, `AddTransient`, `AddSingleton` and keyed variants) and constructor-injected dependencies. Evidence is available in the project inspector and participates in snapshot comparisons.
- Adds namespaced shared-contract and DI evidence fixtures for the scanner smoke test.
- Keeps 0.5.0 snapshots readable when the new optional `codeEvidence` field is absent; a dedicated Swift compatibility test covers legacy and current snapshot shapes.

## What is new in 0.5.0

- **Architecture and operations now share a project-to-service binding.** Project blocks display the matching configured StackPilot service state and managed PID.
- Context-menu actions start/stop enabled services. Selecting a configured project opens an operations card with command, working directory, health result, logs, and lifecycle buttons.
- Runtime actions refresh the status response after each operation; a manual refresh is available on the architecture map.
- Service matching prefers an exact .NET `--project` path or matching Node package working directory, and declines ambiguous matches instead of guessing.
- Compose entries are treated as infrastructure and remain in the StackPilot operations dashboard rather than being incorrectly attached to source project blocks.
- Added `scripts/runtime-linker-test.sh` and a pure Swift test harness for project/profile/service association.

## What is new in 0.4.0

- **StackPilot service dashboard** in the SwiftUI app, available without opening an architecture repository first.
- Creates an additive per-workspace starter profile from a local repo. Detected .NET web/executable projects and Node packages with `dev`/`start` scripts are added as **disabled templates**.
- Start/stop individual managed process services, start/stop an entire profile in dependency order, and enable/disable configured services.
- Docker Compose service actions (`up -d`, `stop`, `ps`, `logs`) by configured Compose file and service name.
- Process PID/start-time ownership metadata, log redirection, bounded log tail, and process-tree stop for services started by StackPilot.
- Optional HTTP health probes and explicit `dependsOn` relationships.
- Per-workspace profiles and configuration stored outside source repositories by default.
- CLI lifecycle smoke test using an isolated `/bin/sleep` process.

## Multi-repository workspace support (0.8.0)

A selected workspace can contain several independent Git repositories (for example, one repository per microservice). The engine discovers nested `.git` directories/files, records each repository's branch, HEAD and local changes, and prefixes change paths with the workspace-relative repository path. The global Auto fingerprint includes changed-file content (using full SHA-256 for files up to 8 MiB and bounded first/last samples for larger files), so another edit to a file that was already dirty still triggers a refresh. The scanner avoids traversing excluded build/dependency directories when fingerprinting non-Git folders.

Run the dedicated test on macOS with .NET 10 and Git installed:

```bash
./scripts/git-workspace-test.sh
```

## Endpoints HTTP and `.http` request testing

Open **Endpoints HTTP** after selecting a workspace. The sidebar groups all results by repository so a workspace containing several independent microservices does not merge their request collections.

The catalog tries to discover ASP.NET Core controller and Minimal API routes plus Next.js App Router / Pages API routes. If the code exposes a request DTO / body parameter that can be resolved safely, CodeAtlas builds an editable example JSON body from its source fields. This is a starting template, not a substitute for OpenAPI, validation rules, authorization policies, or business-specific required values. Unknown or ambiguous request types are not fabricated.

Existing Rider / IntelliJ HTTP Client and VS Code REST Client files using `###`, HTTP method + URL, headers, request body, `@variables`, and `http-client.env.json` / `http-client.private.env.json` are indexed. Requests are associated by method and normalized path, preferring the owning project and Git repository. A request that cannot be associated is still available for execution. The parser does not yet support every IDE feature (for example response-handler scripts, dynamic response references, all built-in dynamic variables, or arbitrary pre-request scripts).

To create a reusable test, select an endpoint and choose **Crear .http**. To run a request, select an existing `.http` entry or an endpoint, review/edit its method, URL, headers, body and environment, and press the execute button. UI edits are temporary for `.http` files; the original request file is not overwritten. The engine only accepts absolute `http://` or `https://` URLs, times out after 30 seconds and caps the captured response body at 512 KiB. `POST`, `PUT`, `PATCH` and `DELETE` may modify data, so check the host/environment first.

The outgoing-call list recognizes common statically analyzable `HttpClient` calls and basic `fetch` / Axios-style calls. When method + path matches another service’s discovered endpoint, it displays that endpoint as a candidate along with source file/line. This is source-level inference: dynamic `BaseAddress`, service discovery, reverse proxies, path rewriting, auth gateways, and runtime traffic can change the actual destination.

Useful checks on macOS with .NET 10 installed:

```bash
./scripts/http-catalog-test.sh
./scripts/http-request-execution-test.sh
./scripts/smoke-test.sh
```

HTTP catalog data is generated on demand and not persisted to architecture snapshots, because request files and environment configuration may contain credentials or test data. Do not place real secrets in a public `.http` file or commit private environment files.

HTTP Explorer reference: [`docs/HTTP_ENDPOINT_EXPLORER.md`](docs/HTTP_ENDPOINT_EXPLORER.md) covers the supported `.http` syntax, environment precedence, route association and execution limits.

## Business flow explorer

Open **Flujos de negocio** in the sidebar to search local paths and cross-service message paths. The scanner can discover several independent Git repositories inside one selected workspace folder, such as one repository per microservice. A distributed path joins a traced producer flow, an inferred RabbitMQ/MassTransit contract match, and an `IConsumer<T>` flow when the source evidence is available. The view includes both projects, the contract, source file/line and confidence for each step. Next.js routes are indexed but their TypeScript function bodies are not yet traced.

Static tracing is deliberately bounded (maximum depth and step count), may miss reflection, source-generated endpoints, delegate/method-group handlers, dynamic dispatch or external configuration, and does not prove which conditional branch executes at runtime. Treat inferred entries as a navigation aid, not as a runtime trace.

## Architecture analysis features

- Finds `.csproj` and `package.json` projects while excluding generated/dependency directories.
- Parses .NET target frameworks, package references and `ProjectReference` relationships.
- Uses Roslyn syntax trees and best-effort semantic models for ASP.NET Core endpoints, fully-qualified MassTransit contract candidates, generic DI registrations and constructor injection evidence.
- Detects Next.js App Router / Pages Router route conventions.
- Optional TypeScript Compiler API pass for cross-package imports when TypeScript is already installed in the repo.
- Directed block diagram with labelled relations and evidence/confidence.
- Snapshots outside the repo and project/relation delta from the previous scan.
- Auto refresh checks Git head, branch, changed-file status and content fingerprints every 15 seconds; a second edit to an already-dirty file still triggers a scan.

## Requirements

- macOS 14+
- Xcode 16+ / Swift 6 toolchain
- .NET SDK 10+
- Git
- Node.js is optional for TypeScript analysis; TypeScript must already be available in the analyzed repo.
- Docker CLI / Docker Desktop is optional and required only for configured Compose services.

## Run on macOS (fail-closed startup)

**Use the launcher instead of `swift run` directly.** The launcher blocks startup unless the .NET engine builds, the complete smoke/integration suite passes, distributed-flow and UI-structure regressions pass, and the native SwiftUI target builds successfully.

```bash
./scripts/launch-macos.sh
```

To run the verification gate without launching the app:

```bash
./scripts/verify-before-launch.sh
```

If any required test or build fails, the script exits with a non-zero status and does not start CodeAtlas. A missing required toolchain also blocks startup. The TypeScript analyzer may explicitly report `SKIPPED` when a TypeScript compiler is not discoverable in its fixture; this is an optional analyzer capability, not a failed test. All executed assertions must pass.

Keep `macOSApp/` and `engine/` in the original directory structure. `swift run --skip-build` is only called after the verification gate has completed successfully. A signed/bundled `.app` and installer are not included yet.

## Run the architecture engine

```bash
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- scan /path/to/repository
```

For the Git workspace fingerprint and nested-repository test (requires Git and the .NET SDK):

```bash
./scripts/git-workspace-test.sh
```

For the architecture-to-runtime binding test (does not require the .NET SDK):

```bash
./scripts/runtime-linker-test.sh
```

For Clean Architecture layer grouping and multi-repository snapshot-merging tests (do not require the .NET SDK):

```bash
./scripts/clean-architecture-grouper-test.sh
./scripts/workspace-merger-test.sh
```

For the full scanner and StackPilot process smoke test (requires the .NET SDK):

```bash
./scripts/smoke-test.sh
```

To focus on the producer-to-consumer message path (also requires the .NET SDK):

```bash
./scripts/distributed-flow-test.sh
```

## StackPilot quick start

In the desktop app, choose **Servicios locales**. Select **Crear perfil desde un repositorio…** or **Añadir workspace**. CodeAtlas generates a profile based on project metadata, but every generated service is disabled. Open the JSON, review each command and working directory, add ports/health URLs and dependencies, then enable the services you trust.

Default macOS configuration:

```text
~/Library/Application Support/CodeAtlas/StackPilot/profiles.json
```

Control services directly from the UI or use the CLI:

```bash
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- stackpilot status
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- stackpilot start <profile-id> <service-id>
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- stackpilot stop <profile-id> <service-id>
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- stackpilot start-profile <profile-id>
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- stackpilot stop-profile <profile-id>
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- stackpilot logs <profile-id> <service-id> --lines 200
```

See [`docs/STACKPILOT.md`](docs/STACKPILOT.md) for profile schema, Compose examples, ownership guarantees and limitations. See [`docs/ARCHITECTURE_OPERATIONS.md`](docs/ARCHITECTURE_OPERATIONS.md) for the integration between the block diagram and local runtime controls, [`docs/SEMANTIC_ANALYSIS.md`](docs/SEMANTIC_ANALYSIS.md) for symbol resolution and evidence confidence rules, and [`docs/AI_INTEGRATION.md`](docs/AI_INTEGRATION.md) for local Ollama / Anthropic setup and data handling.

## Important safety and correctness limits

- Generated launch commands are disabled by default; they must be reviewed and enabled manually.
- StackPilot stops processes only when its saved PID and process start timestamp match. It does not kill processes by port.
- Docker Compose is an optional local integration; it is not remote orchestration.
- HTTP and message relationships from the architecture engine can be inferred, not proof of runtime traffic.
- Roslyn semantic analysis is best-effort: it uses source trees plus platform assemblies and package compile assets from `obj/project.assets.json` when present. It does not run MSBuild, execute source generators, fully resolve every project reference, or prove MassTransit routing and runtime delivery.
- In this generation environment, the pure Swift runtime-linker tests, Swift syntax parsing, and TypeScript analyzer JSON smoke check passed. The complete .NET engine and native SwiftUI app could not be compiled/run here because the .NET and macOS SDKs are unavailable. Run `./scripts/smoke-test.sh` on the target Mac before relying on this release.


## Integración con Visual Studio Code

La carpeta `vscode-extension/` contiene una extensión local para abrir archivos y workspaces desde VS Code en CodeAtlas. El puente nativo escucha exclusivamente en `127.0.0.1:38471` y valida un token almacenado con permisos de usuario en `~/Library/Application Support/CodeAtlas/vscode-bridge.json`.

En macOS, inicia CodeAtlas y después ejecuta desde el directorio del proyecto `./scripts/package-vscode-extension.sh`. Instala el archivo generado en `artifacts/vscode-extension/CodeAtlas-VSCode-Bridge-0.1.0.vsix` mediante `code --install-extension <ruta-al-vsix>`. Los comandos aparecen en la paleta como `CodeAtlas: Abrir archivo activo en CodeAtlas`, `CodeAtlas: Abrir workspace en CodeAtlas` y `CodeAtlas: Comprobar conexión local`. Esta primera integración facilita navegación entre editores; no pretende sincronizar una sesión de depuración ni sustituir las extensiones de lenguaje de VS Code.
