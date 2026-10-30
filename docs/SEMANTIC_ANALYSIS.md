# Semantic C# analysis (0.7.0)

CodeAtlas uses Roslyn to build a best-effort compilation for each discovered `.csproj`.

## Inputs

- All `.cs` source files owned by the project directory, excluding nested project folders and generated/dependency directories.
- Trusted platform assemblies from the currently running .NET host.
- NuGet `compile` assemblies from `obj/project.assets.json`, when that file exists and can be read.

CodeAtlas does not run MSBuild, restore packages, execute generators, or modify the repository while scanning. If the asset file is absent or stale, analysis degrades to syntax-level candidates rather than trying to repair the project.

## Evidence emitted

`ProjectNode.codeEvidence` contains records with category, symbol, repository-relative file, one-based line, confidence and a short explanation.

- `api-endpoint`: Minimal API map invocation or controller HTTP attribute.
- `message-published`: `Publish`, `PublishAsync`, `Send` or `SendAsync` contract candidate.
- `message-consumed`: `IConsumer<T>` contract.
- `service-registration`: common generic DI registrations (`AddScoped`, `AddTransient`, `AddSingleton`, and keyed variants).
- `constructor-injection`: typed parameters in explicit constructors and C# 12 primary constructors on classes/structs that may be injected by the container. Positional record parameters are excluded to reduce false positives.

The confidence field is either `resolved` (the relevant type and, when required, the method/interface symbol were resolved by Roslyn) or `candidate` (syntax was recognized but semantic confirmation is missing).

## Message matching rules

- Prefer exact normalized fully-qualified contract type equality. Message-edge identities include the fully-qualified contract key, so changing a contract namespace appears in the architecture delta even if its short name is unchanged.
- A short-name fallback is allowed only if the scan has not found multiple different qualified symbols with that short name.
- Message edges remain `inferred`: source analysis does not prove queue topology, routing keys, broker delivery, retries, or runtime execution.

## Known limits

- `IConsumer<T>` detection currently recognizes direct base-list syntax.
- Generic DI registrations and constructor parameters are catalogued, but the engine does not yet construct a complete service-provider graph or resolve factory/lambda registrations.
- It does not yet trace a consumer's `Consume` body through MediatR, handlers, repositories and outbound calls as a complete business flow.
- Each project compilation uses available package compile assets, but CodeAtlas does not build project references or run MSBuild/source generators. A repository-wide source type index can supply an exact namespace-qualified contract name when Roslyn cannot bind a cross-project message type; that is source-index evidence, not a Roslyn-resolved symbol. The index is bounded to 40,000 C# source files.
- Snapshot comparison ignores evidence line-number changes to avoid marking an architecture as changed solely because lines moved; file, symbol, category, confidence and semantic description changes remain significant.

Run `./scripts/smoke-test.sh` on a Mac with .NET SDK 10 installed to exercise the scanner against the shared namespaced message-contract fixture and DI patterns.


## Business flow tracing (0.7.0)

- Each discovered ASP.NET Core Minimal API lambda or controller action can become a `BusinessFlow` entry point.
- The tracer follows source-resolved method invocations up to depth 7 and 24 steps, avoiding recursive method loops.
- Common mediator dispatch (`Send(new Request(...))`) is mapped to an `IRequestHandler<TRequest>` implementation when Roslyn resolves the interface; a request-name + `Handler` convention is a candidate fallback.
- Generic DI registrations are mapped from service type to implementation type only when a unique implementation exists. Interface calls can then link to the implementation method with file/line evidence.
- MassTransit `Publish` calls produce a message step when the target symbol or containing type supports that inference. The tracer does not claim queue names, routing keys, delivery guarantees, or runtime traffic unless those are separately verified.
- Next.js route files are represented as route-only flow entries for now. TypeScript module import analysis is separate; tracing inside route handlers and cross-service frontend/API chains remains a future increment.
- When a referenced message type cannot be bound in the current project compilation, the flow tracer consults the repository source type index and imports to qualify the type. The result is labelled in step details as source-based evidence and does not imply that the project currently compiles.
- `businessFlows` and `flowChanges` are optional JSON fields so historical snapshots decode. Flow comparison ignores source-line-only shifts and compares the meaningful step signature.


## Distributed message paths (0.8.0)

CodeAtlas joins publisher and consumer source evidence through the stable fully-qualified contract key on a `message-flow` relation. For a producer flow, it uses locally traced `message-publish` steps when available; otherwise it emits a source-evidence fallback. On the consumer side it detects classes that implement a syntactically identified `IConsumer<T>` in a MassTransit context and traces the `Consume`/`ConsumeAsync` method where possible.

The resulting optional `distributedFlows` snapshot field contains three conceptual phases: `producer`, `transport`, and `consumer`. Each step carries its owning project and source evidence. A contract match does **not** establish a broker exchange, routing key, queue binding, retry policy, successful delivery, or runtime execution. Those details must be added from configuration analysis or telemetry in a later phase. If a consumer or publish site cannot be traced, the snapshot retains a lower-confidence fallback rather than inventing a method path.

Distributed paths are compared between snapshots. A path is considered removed when its producer/consumer contract relation is no longer detected, and modified when the source-backed step signature changes. Source line movement alone does not define a change.


## Multi-repository workspace state (0.8.0)

A selected folder may contain separate Git repositories, one per service. `GitWorkspaceInspector` discovers Git roots without entering excluded build/dependency folders, and the snapshot records branch, HEAD and local changes for each repository. Paths in the combined changes list are prefixed relative to the selected workspace.

Auto-refresh fingerprints include file content for dirty and untracked paths, not only the Git status string. This catches edits to a file that was already modified before the last poll. Files up to 8 MiB use SHA-256 over the full content; larger files use bounded first/last samples plus size and modification time to limit polling cost. Non-Git workspaces use an excluded-directory-aware file walk.

Nested repositories are independent Git histories. Their branch and HEAD values are recorded separately; the synthetic workspace commit is a stable hash of their path/branch/HEAD tuple and should not be interpreted as a Git commit.


## Grafo de llamadas (Fase 3)

El escaneo sigue compilando cada proyecto de forma aislada. El grafo de llamadas, el análisis de impacto y las reglas interprocedimentales usan compilaciones enlazadas por `ProjectReference` y están documentados en `CALL_GRAPH.md`; el trazador de flujos de este documento se mantiene y una prueba comprueba que la proyección de flujos del grafo no pierda sus pasos resolubles.
