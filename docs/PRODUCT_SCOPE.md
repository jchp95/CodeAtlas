# Product scope — CodeAtlas + StackPilot

## North star
Understand how the software works, see the impact of source changes, and operate the local stack from one macOS app.

## Core analysis and operations capabilities
- Scan `.csproj`, `package.json`, selected C#/TypeScript/JavaScript source files and common configuration files.
- Build a best-effort Roslyn compilation for each C# project, with platform and NuGet compile assets where available; extract endpoint, messaging and DI evidence with file/line locations.
- Resolve fully-qualified message contract symbols where possible, matching publish/send candidates to `IConsumer<T>` declarations without relying on short names when those names are ambiguous.
- Catalogue generic DI registrations and constructor-injected dependencies; compare their structural evidence in snapshots while ignoring pure line-number shifts.
- Keep legacy 0.5 snapshots readable when the optional `codeEvidence` collection is absent.
- Resolve TypeScript import/export/dynamic import/`require` edges across local workspace packages with the TypeScript Compiler API when already installed.
- Render five lanes of block-based architecture (frontend, APIs and grouped Clean Architecture services, workers/consumers, shared libraries/contracts, infrastructure dependencies) with labeled directed connectors.
- Use added/modified/removed states to compare the current diagram to the previously persisted snapshot.
- Keep evidence, confidence and source paths available in the project inspector and relation list.
- Poll Git state and re-scan when branch, HEAD or working-tree contents change.
- Generate additive local runtime profiles for executable .NET projects and Node packages, with all generated entries disabled by default.
- Manage explicitly enabled local processes and configured Docker Compose services from the desktop UI and CLI.
- Bind architecture project blocks to a matching StackPilot service by exact project argument, package working directory, or unique matching name.
- Show local service status and available controls on the architecture diagram and in the selected project's operations card.
- Expose start/stop, local log viewing, PID and configured health-check result from the diagram; keep disabled generated templates gated behind the StackPilot configuration dashboard.
- Fail closed on ambiguous service matches instead of attaching the wrong controls to a source project.
- Store managed PID/start-time metadata and process logs under user application support; support stop by saved process ownership, not by port.
- Provide profile start/stop dependency ordering, basic health URL checks, and local log tailing.

## Core entities
- Repository: local path, branch, HEAD, dirty state and latest analysis.
- Snapshot: timestamp, commit, extracted projects and relationships, warnings and local Git status.
- Component: API, frontend, worker, shared library, or package.
- Relationship: HTTP candidate, message-flow candidate, package/project reference, TypeScript module import.
- Evidence: source file, URL or contract type, detection method and confidence.
- Runtime profile: selected services, dependencies, startup order, health checks and stop strategy.

## Architecture visualization rules
- Primary layout is layered blocks and labeled directional flows, not a free-form node graph.
- Carril/role classification is heuristic and must not be confused with authoritative domain ownership.
- A connection always carries its type and evidence.
- Separate code-level resolution, inference and runtime-observed traffic.
- Clicking a block opens its routes, packages, project references, event contracts, HTTP URLs and source locations.
- Added, modified and removed projects/relations are highlighted against the previous snapshot.

## Safety for local process management
- Never kill a process based on port alone without showing PID, command and confirmation.
- Prefer stopping processes started by the app, or use explicitly declared Docker Compose profiles.
- Keep secrets out of snapshots and logs.
- Do not automatically run arbitrary scripts from a repository without explicit user approval.

## Remaining milestones
1. Expand Roslyn analysis to resolve project-reference outputs, full constructor/service-provider graphs, named/typed HTTP clients and controller route composition.
2. Expand TypeScript analysis with package exports, workspace configs, contract lookup and richer source-location navigation.
3. Resolve more shared-contract patterns, MassTransit routing conventions, outbox/inbox and forwarding patterns.
4. Add optional runtime evidence for HTTP and RabbitMQ, clearly separate from static inference.
5. Expand StackPilot with editable in-app launch settings, grouped log timeline, runtime telemetry, and deeper Compose health/state information.
6. Add end-to-end integration tests on macOS, a packaged `.app`, signing and notarization.

## Current limitations
The scanner is not a complete semantic analysis or runtime trace. See `README.md` and `docs/ARCHITECTURE.md` for the exact relation rules and limitations.


## 0.7.0 scope: Business Flow Explorer

Implemented: searchable business flow cards, static C# endpoint-to-method trace, common request-handler mapping, unique DI interface-to-implementation mapping, message publication steps, and snapshot flow deltas. Next.js routes appear as route-only entries; TypeScript body-level tracing, full inter-service execution tracing, runtime distributed traces, and source-generated/dynamic dispatch analysis remain out of scope for this increment.

## 0.10.0 scope: grouped architecture, multi-root workspace and AI

- Combine multiple selected repository roots without project-ID collisions; preserve repository ownership for endpoints, HTTP requests, source locations and distributed flows.
- Group common Clean Architecture project suffixes into one top-level service block and expose layer-by-layer inspection from the selected block.
- Show detected infrastructure candidates as independent blocks, with inferred source/configuration evidence.
- Initialize and operate StackPilot profiles for multiple selected roots from one dashboard; generated launch entries stay disabled for review.
- Offer an optional single-turn assistant through Ollama local or Anthropic API. It receives bounded structured metadata when the user leaves the context toggle enabled; it does not automatically read source files, execute commands or modify a repository.

## Roadmap towards a local engineering IDE

1. **File explorer and source context:** read-only file tree, syntax-highlighted source preview, cross-navigation from endpoint/evidence/contract to the exact file and line; do not edit files in this stage.
2. **Integrated console:** launch an explicit shell in a selected repository with a pseudo-terminal, per-workspace sessions, cwd tracking and user-confirmed commands. Retain StackPilot process ownership rules and never treat AI-generated commands as pre-approved.
3. **Deeper code intelligence:** Roslyn semantic references for project outputs and typed HTTP clients; TypeScript symbol navigation; contract schema comparison; richer OpenAPI import; relationship provenance and false-positive controls.
4. **IDE bridge:** a small optional VS Code extension for open-file/open-symbol, selected diagnostics and explicit commands. Start with read-only navigation and keep the extension boundary versioned; no hidden collection of source or editor contents.
5. **AI-assisted engineering tasks:** explicit opt-in context selection (current project, changed files, flow or endpoint), diff review, test-plan generation and proposed patches. Show citations to local file paths/lines and require manual approval before file edits, command execution, commits, or service operations.
6. **Production readiness:** real macOS integration tests, robust cancellation/timeouts, logs and privacy review, signed/notarized app packaging, migration tests for settings/snapshots, and clear diagnostics when optional tools/SDKs are absent.

A goal of becoming an IDE does not mean replacing Rider, Cursor or VS Code immediately. The intended first step is an architecture-aware control center that can launch or link out to the existing editor and later add file/console/extension capabilities without duplicating its compiler/debugger responsibilities.
