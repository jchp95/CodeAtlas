## 0.19.0 — LSP semántico inicial para el editor

- Añade un cliente Language Server Protocol por stdio con framing `Content-Length`, correlación JSON-RPC, límites de tamaño y timeout por petición.
- Añade detección opcional, sin instalación automática, de `csharp-ls`, `typescript-language-server`, `sourcekit-lsp`, `pylsp` y `vscode-json-language-server` según la extensión del archivo.
- Integra `didOpen`/`didChange`/`didClose` y recepción de `publishDiagnostics`, con sincronización completa del documento tras debounce.
- Añade completado bajo demanda desde el editor, inserción revisable, navegación semántica a definición e inspector de diagnósticos.
- Maneja solicitudes comunes del servidor (`workspace/configuration`, carpetas, registro y progreso); rechaza `workspace/applyEdit` y ejecución remota de comandos para preservar la aprobación explícita.
- Añade prueba integrada con un servidor LSP falso en Node: handshake, solicitud del servidor, diagnósticos, completion, definition, sincronización y cierre.
- La ausencia de un servidor se muestra explícitamente. El outline heurístico y la asistencia generativa previa siguen como mecanismos separados.
- Limitaciones: no hay sugerencia automática en cada pulsación, code actions, rename/references, semantic tokens ni gestión de instalación/configuración del servidor desde la UI. La compilación integral de macOS aún debe ejecutarse en macOS.

## 0.18.0 — Editor legible y outline de símbolos

- Añade coloreado sintáctico nativo para palabras clave, cadenas, comentarios, números, anotaciones e identificadores frecuentes en el editor AppKit. Los documentos grandes omiten el análisis de colores por encima de 300.000 caracteres para evitar bloquear la escritura.
- Añade un índice heurístico de símbolos para C#, TypeScript/JavaScript, Swift, Python, Markdown y JSON, calculado en una tarea de fondo y navegable desde el inspector derecho.
- Incorpora pruebas ejecutables de outline para C#, TypeScript, Markdown y JSON; mantiene explícita la distinción entre outline heurístico y semántica LSP.
- No sustituye los language servers; la navegación semántica precisa, diagnósticos en tiempo real y autocompletado convencional siguen pendientes para la siguiente fase.
- Validación en este entorno: test Swift del índice, parseo sintáctico de las fuentes, estructura UI, árbol y vertical slices pasan; la compilación nativa macOS y .NET debe ejecutarse en el Mac.

## 0.17.0 — Evidencia de relaciones y recorridos navegables

- Sustituye la lista básica de relaciones por un explorador de evidencia con búsqueda, filtros por tipo/confianza, métrica de cobertura de fuente y navegación al archivo/línea cuando existe una coincidencia inequívoca.
- Distingue conexiones HTTP, eventos, referencias de proyecto, imports y dependencias del workspace, y explica la diferencia entre evidencia confirmada e inferida.
- Añade una secuencia horizontal clicable para flujos internos y distribuidos; cada etapa abre su código fuente, y las vistas pueden filtrarse por tipo de recorrido.
- Conserva la vista de detalle vertical para inspección completa de confianza, símbolo y contexto.
- Validación de esta fase: parseo Swift y tests estructurales en este entorno; falta ejecutar `swift build` y las suites .NET en macOS porque los SDK no están disponibles en este runner.

## 0.16.0 — Integración VS Code mediante puente local autenticado

- Añade un puente HTTP local vinculado exclusivamente a `127.0.0.1`, con token aleatorio en `~/Library/Application Support/CodeAtlas/vscode-bridge.json` y permisos de archivo de propietario.
- Añade una extensión instalable de Visual Studio Code para abrir el archivo activo en CodeAtlas, enviar un workspace y comprobar la conexión; usa APIs Node integradas, no incorpora dependencias npm de ejecución ni expone un listener en la red local.
- Expone estado del puente en la barra inferior de CodeAtlas y añade una regresión de estructura/seguridad para los contratos de extensión y app.
- Incluye script para empaquetar un VSIX local e instrucciones de instalación.
- Limitación de validación: el puente y SwiftUI requieren compilación y pruebas de runtime en macOS. La prueba Node/XML y el análisis sintáctico de Swift son verificables en este entorno, pero no sustituyen una prueba de conexión real entre VS Code y CodeAtlas.

## 0.15.0 — Herramientas de desarrollo, Git y asistencia de código

- Añade un dock inferior de Build/Tests que ejecuta comandos detectados para .NET, npm y Swift con salida en vivo, cancelación y un límite de memoria para el registro.
- Separa Perfiles de ejecución y Diagnóstico en vistas con operaciones explícitas de inicio/parada/enable y métricas de proceso/health checks; incorpora acceso a logs.
- Añade un workbench Git para listar ramas, mostrar estado local, diff staged/unstaged e historial, cambiar de rama con confirmación y abrir archivos en CodeAtlas. La operación checkout delega la protección contra sobrescritura en Git y no descarta cambios por cuenta propia.
- Añade una vista GitHub opcional mediante GitHub CLI (`gh`): valida la autenticación y consulta repositorio/pull requests sin almacenar tokens de GitHub en CodeAtlas.
- Añade formateo por extensión: `dotnet format whitespace` para C#, `swift-format` para Swift, `shfmt` para shell y Prettier local (`npx --no-install`) para JavaScript/TypeScript/JSON/Markdown/YAML/HTML/CSS. No instala paquetes ni descarga formateadores automáticamente.
- Añade una acción de autocompletado/asistencia de código que usa el proveedor IA configurado (Ollama o Anthropic), limita el contexto alrededor del cursor y permite revisar/copiar/insertar la propuesta manualmente.
- Añade regresiones estructurales para el dock, Git, perfiles/diagnóstico, formateo y asistencia IA.
- Validación en este entorno: el análisis sintáctico de fuentes Swift, los tests de árbol, la estructura vertical slice y los tests independientes de modelo/enlaces pueden ejecutarse localmente. No hay SDK .NET ni SDK de macOS aquí, de modo que la compilación integral y la suite de integración .NET deben confirmarse en el Mac.

## 0.14.0 — Navegación al código, editor nativo y descubrimiento FastEndpoints

- Añade un explorador de workspace con árbol recursivo multi-raíz, búsqueda por nombre/ruta, exclusión de directorios generados y límite de seguridad para repositorios grandes.
- Añade un editor AppKit nativo con números de línea, búsqueda integrada, salto a línea, guardado UTF-8, recarga y confirmación explícita antes de descartar cambios sin guardar.
- Añade un inspector derecho para la fuente seleccionada, con ruta, lenguaje, tamaño, fecha de modificación y acciones de Finder/copiar ruta.
- Conecta rutas y evidencias de endpoints, flujos de negocio y análisis con el archivo y la línea reales mediante navegación interna.
- Añade detector de endpoints FastEndpoints (`Endpoint<TRequest,TResponse>` y convenciones `Configure`) y mejora el análisis de rutas agrupadas de Minimal API y múltiples atributos HTTP en controllers.
- Muestra progreso del análisis por repositorio y fase, mientras el proceso del motor se ejecuta fuera del hilo de interfaz.
- Añade regresión ejecutable de `CodeFileNode` y refuerza el gate de inicio con los nuevos controles estructurales.
- Validación disponible aquí: tests del árbol, pruebas de estructura vertical slice, parseo sintáctico de Swift y pruebas independientes de enlaces pasan. La compilación .NET 10 y la compilación SwiftUI deben ejecutarse en macOS, pues este entorno no dispone de esos SDK completos.

## 0.13.1 — Fallback de trazado para llamadas locales con símbolos incompletos

- Evita perder pasos de un flujo distribuido cuando Roslyn no puede enlazar una llamada de método local debido a referencias incompletas en el proyecto analizado.
- Añade una resolución conservadora basada en el tipo declarado del receptor y la firma del método fuente; solo sigue llamadas con una coincidencia única y mantiene la confianza como `inferred`.
- Añade protección contra ciclos en las declaraciones recorridas por el fallback y conserva la resolución semántica de Roslyn como primera opción.
- No cambia la lógica de negocio ni los contratos JSON/CLI. La compilación y la prueba de integración que reproduce `CargoRegisteredConsumer → CargoProjection.ApplyAsync` deben confirmarse en macOS con .NET 10.

## 0.13.0 — Refactor de código por vertical slices

- Reorganiza el motor .NET por funcionalidades: arquitectura, flujos de negocio, pruebas HTTP, StackPilot y workspace; la entrada CLI se mantiene en `Program.cs`.
- Divide el escáner de arquitectura, el analizador de flujos, el catálogo HTTP y el control de StackPilot en archivos parciales enfocados por responsabilidad, conservando un único tipo por funcionalidad.
- Separa modelos, utilidades comunes, comparación/snapshots, parsing de `.http`, carga de entornos, ejecución HTTP y estado de procesos.
- Divide las pantallas SwiftUI principales en extensiones de navegación, presentación, ejecución y acciones, y extrae subviews, componentes de diagrama y layout.
- Extrae tarjetas, filas de pasos, estadísticas e indicadores de confianza del explorador de flujos a `Views/BusinessFlows/Components/`, manteniendo el estado y los filtros en la vista raíz.
- Agrupa el cliente del motor, modelos de funcionalidades, servicio IA, contexto IA y enlace con runtime bajo carpetas de responsabilidad.
- Actualiza los tests de regresión para usar las nuevas rutas, y agrega `scripts/vertical-slice-structure-test.sh` como control de estructura que también bloquea el arranque.
- El refactor no intenta cambiar reglas de negocio ni contratos JSON/CLI. Los cambios de acceso de Swift solo permiten que las extensiones de presentación compartan el estado del tipo raíz.
- Validación en este entorno: pruebas Swift independientes, parseo sintáctico de todos los archivos Swift, tests de estructura y shell pasan. La compilación .NET 10 y la compilación SwiftUI completa deben confirmarse en macOS con el SDK instalado; no se declara una compilación completa desde este entorno.

## 0.12.1 — Build fixes after startup gate

- Fixed `CS9105` by making infrastructure detection an instance method so it can access the repository root primary-constructor parameter.
- Fixed nullable-flow warnings in static route concatenation by explicitly proving the left route string is non-null before use.
- Re-run the startup verification script on macOS to validate the full .NET and Swift build chain.

## 0.12.0 — Fail-closed startup verification

- Added `scripts/verify-before-launch.sh` as a mandatory startup gate.
- The gate requires the toolchains, builds the .NET engine, runs integration and regression tests, and builds the native SwiftUI app before launch.
- Updated `scripts/launch-macos.sh` to launch only after the gate passes, using `swift run --skip-build` to avoid an unverified implicit rebuild.
- Updated macOS setup documentation to direct users to the gated launcher rather than `swift run`.
## 0.11.0 — Adaptive glass UI and focused navigation

- Adds a shared `AtlasBackdrop`, adaptive `AtlasGlassSurface`, rounded glass cards, status pills and reusable page headings for consistent macOS screens.
- Removes the forced dark scheme; the persisted System / Light / Dark selector follows the selected appearance at app level.
- Restructures the sidebar into collapsible navigation groups for Home, Architecture, Development/HTTP, Local Environment and AI, while keeping selected workspace roots and detected projects accessible.
- Splits the former combined dashboard into a concise workspace summary, dedicated architecture map, relations, analysis/evidence, changes, business flows, HTTP endpoints and StackPilot views.
- Adds shortcut cards, a contextual top bar and a persistent bottom workspace status bar.
- Updates prominent architecture, business flow, endpoint, AI and local-operations cards to use the shared material, mode-aware borders and restrained shadows. Text editors remain on solid adaptive text backgrounds for contrast.
- Adds `docs/VISUAL_SYSTEM_AND_NAVIGATION.md` and `scripts/ui-structure-test.sh`.
- Validation runs source parsing and pure Swift regression tests; a full macOS SwiftUI build and visual contrast review remain to be run on macOS.

# Changelog

## 0.10.0 — Grouped architecture, multi-root control and AI assistant

- Extracts Clean Architecture grouping into a pure layer grouper; same-root `.NET` projects with recognized layer suffixes appear as one service block, with a layer picker leading to the original project inspector. API/presentation is the representative block and grouped service edges are remapped to the top-level block.
- Ensures grouped services remain in the API/business lane instead of being misclassified into the shared-layer lane due to their internal Domain/Application/Infrastructure members.
- Adds a pure regression test covering a grouped four-layer service, preferred API representative, repository boundary isolation, and independent infrastructure nodes.
- Makes infrastructure detection visible as diagram blocks for RabbitMQ/MassTransit, MinIO, Redis, SQL Server, PostgreSQL, MongoDB, Meilisearch, Kafka and Elasticsearch where source or Compose evidence is found. Relationships retain inferred confidence and evidence paths.
- Allows an infrastructure block to link to an explicitly configured StackPilot Docker Compose service (for example `RabbitMQ` → `rabbitmq`, `MinIO` → `minio`) when the service name is an exact recognized alias. Matching searches selected workspace profiles, supports the Compose definition living in a different selected repository, and refuses generic names or multiple matches.
- Shows an aggregate runtime summary when several repository profiles are selected, rather than implying that the first profile represents the whole workspace.
- Adds a Foundation-only multi-root snapshot-merger test for unique IDs, infrastructure de-duplication, cross-repository contract linking and distributed-flow repository labels. Fixes missing root labels for generated cross-repository paths without internal business flows.
- Fixes Flujos repository selection so the chosen repo filter applies even when the search query is empty; repository filtering and text search now combine rather than bypassing each other.
- Updates the HTTP catalog to preserve an owner workspace root for endpoint, request and outgoing-call entries, so opening source files and saving generated `.http` files in a multi-root workspace use the right repository instead of the first selected root.
- Updates StackPilot dashboard to operate over the selected list of roots, initialize multiple workspace profiles in one action and refresh missing profiles after a root-list change. Existing profiles are not duplicated, and generated process templates stay disabled.
- Adds AI provider settings for local Ollama and Anthropic API, Ollama/model discovery, Anthropic model discovery, Keychain-only API key storage, connection checks, architecture-context privacy toggle and one-click prompts for architecture explanation, change impact, risk discovery and test suggestions.
- Expands the bounded AI context with repository roots, project relationships, code-evidence references, internal/distributed flow summaries and snapshot deltas; the assistant does not automatically attach source contents or private `.http` environment data.
- Adds `docs/AI_INTEGRATION.md`, including current credit-claim setup and privacy boundaries, plus `docs/CLEAN_ARCHITECTURE_AND_INFRA.md`.
- Adds `scripts/clean-architecture-grouper-test.sh` and `scripts/workspace-merger-test.sh` which can run without the .NET SDK.

## 0.9.0 — HTTP Endpoint Explorer and request execution

- Adds an **Endpoints HTTP** desktop section grouping discovered API endpoints, `.http` / `.rest` requests, and outgoing HTTP calls by owning Git repository.
- Detects ASP.NET Core controller routes, Minimal API candidates, Next.js App Router / Pages API routes, path parameters, and source-derived JSON request examples when a request type can be resolved conservatively.
- Parses common Rider / IntelliJ HTTP Client and VS Code REST Client syntax: `###` request separators, method/URL, headers, body, file-scoped `@variables`, `http-client.env.json`, and `http-client.private.env.json`. The parser is intentionally a documented subset, not a full implementation of either IDE's request language.
- Associates `.http` requests with endpoints by method and normalized path, preferring the owning project and repository; unmatched requests remain executable.
- Adds direct endpoint execution, `.http` execution with temporary UI overrides, environment substitution, response status/headers/body capture, a 30-second timeout and a 512 KiB response-body limit. The runner only accepts absolute HTTP/HTTPS URLs and performs no dependency installation.
- Can generate a `.http` file for a discovered endpoint using the code-derived request example, while explicitly labeling inferred payloads as templates rather than authoritative OpenAPI schemas.
- Detects common outbound .NET `HttpClient` calls and TypeScript `fetch` / common Axios-style calls when method and route are statically discoverable, then links same-method/path matches to candidate endpoints in other projects. Cross-service links are explicitly inferred and do not prove runtime traffic.
- Keeps request catalog data out of persistent architecture snapshots because `.http` files may contain test data or credentials.
- Adds an HTTP catalog fixture covering endpoint discovery, source-derived payload samples, Rider-style environment files, `.http` association, C# and TypeScript outbound call traces, plus local HTTP execution tests for response capture and environment precedence.


## 0.8.0 — Cross-service message-flow tracing

- Adds a versioned `DistributedFlow` snapshot entity joining a producer path, a RabbitMQ/MassTransit contract match, and a consumer path across discovered projects.
- Recognizes source classes implementing `IConsumer<T>` as consumer entry points and follows their `Consume` method into local methods where Roslyn can resolve them.
- Preserves project, phase, file/line, confidence and explanatory evidence on every distributed step; the UI presents producer → transport → consumer and can open either service from a flow card.
- Extends architecture snapshot deltas to report distributed paths added, modified or removed after source/contract changes.
- Adds a consumer fixture that traces `CargoRegisteredConsumer.Consume` into `CargoProjection.ApplyAsync`, plus smoke assertions for the full producer-to-consumer path.
- Keeps distributed-flow fields optional for compatibility with existing snapshots.
- Runtime broker topology, routing keys, queue bindings, retries and actual delivery remain unverified unless configuration or telemetry is inspected; contract equality is treated as inferred evidence only.
- Supports a selected workspace containing multiple independent nested Git repositories and records each repository's branch, HEAD and local changes.
- Prefixes changed-file paths with workspace-relative repository paths and suppresses duplicate parent-worktree entries that only represent a nested repository.
- Auto-refresh fingerprints include changed-file content, so a second edit to an already-dirty path still triggers rescan; files up to 8 MiB are hashed fully, and larger files use bounded samples.
- Adds a multi-repository Git state panel to the desktop UI and `scripts/git-workspace-test.sh` for nested repos and content-sensitive fingerprints.

## 0.7.0 — Business Flow Explorer

- Adds a new **Flujos de negocio** desktop view with searchable endpoint-to-code traces, source file/line evidence, step categories, and confidence labels.
- Traces ASP.NET Core Minimal API lambdas and controller actions into local methods using Roslyn method symbols, with recursion and step limits to keep large codebases responsive.
- Recognizes common `ISender.Send(new Request(...))` / `IRequestHandler<TRequest>` patterns, with a conservative `{RequestName}Handler` convention fallback.
- Follows generic DI registrations (`AddScoped`, `AddTransient`, `AddSingleton` and keyed variants) from injected interfaces to unique implementation methods.
- Adds message-publication and persistence/client boundary steps when source evidence supports them; clearly states that inferred paths do not prove runtime execution.
- Builds one bounded repository-wide C# source type index so cross-project message contracts can retain their namespace when project reference assemblies are unavailable; this is source evidence, not a substitute for a successful build.
- Creates route-only flow entries for discovered Next.js App Router / Pages Router endpoints while labeling TypeScript body tracing as not implemented yet.
- Adds business-flow deltas between snapshots and reports added, removed, or modified paths while ignoring line-number-only movement.
- Expands the smoke fixture to cover `POST /api/cargo` → request handler → DI implementation → `CargoRegistered` publication.
- Improves TypeScript compiler discovery for local and global installations and provides a real cross-package import fixture; the smoke test now verifies the expected relation if a compiler is available.
- Keeps `businessFlows` and `flowChanges` optional so pre-0.7 snapshots remain readable.

## 0.6.1 — Fix C# smoke fixtures and primary-constructor evidence

- Fixed a false-negative smoke test by adding an explicit `app.MapGet("/api/health", ...)` endpoint to the .NET fixture inspected by the scanner.
- Added Roslyn syntax handling for C# 12 primary constructors on classes and structs, so typed parameters can appear as constructor-injection evidence. Positional records are deliberately excluded to reduce data-model false positives.
- Clarified the optional TypeScript smoke output: if the fixture does not expose a `tsconfig.json` and installed TypeScript Compiler API, the analyzer is reported as skipped rather than implying a cross-package analysis ran.
- No project dependencies are installed, restored, or modified by these tests.

## 0.6.0 — Semantic contract and DI evidence

- Builds Roslyn semantic models with platform and NuGet compile references when project assets are available.
- Uses fully-qualified contract symbol names where resolvable; conservative short-name fallback avoids linking competing namespaced contracts. Message-edge identities include the full contract key so namespace changes are visible in deltas.
- Adds line-level evidence for ASP.NET Core endpoint candidates, MassTransit consumers, publish/send candidates, generic DI service registrations and constructor injection.
- Includes semantic evidence changes in project snapshot deltas and displays a compact evidence inspector in the desktop UI.
- Extends fixtures to verify a shared namespaced message contract and dependency-injection patterns.
- Adds a compatibility test ensuring snapshots from 0.5.0 still decode when the optional evidence field is missing.

## 0.5.0 — Diagram and local runtime integration

- Connected architecture blocks to matching StackPilot services from the selected workspace profile.
- Added service runtime indicators to project blocks, including managed running/stopped/disabled/unavailable status and PID when available.
- Added block context-menu actions for starting/stopping enabled services and opening StackPilot for review.
- Added a selected-project operations card with command preview, working directory, health status, log viewer, and start/stop controls.
- Added workspace-profile matching and conservative source-project-to-service resolution, with ambiguity handling that refuses to guess.
- Added pure Swift runtime-linker tests for profile path normalization, .NET command matching, Node working-directory matching, and ambiguous bindings.
- Added architecture/operations integration documentation and validation steps.


## 0.4.0 — StackPilot local operations

- Added a StackPilot dashboard to the desktop app, accessible without selecting an architecture repository.
- Added additive workspace profile initialization, disabled generated service templates and commands to enable/disable them.
- Added managed process start/stop/status with PID/start-time ownership records, log file redirection, bounded tail reads and process-tree stop.
- Added profile-level start/stop in topological dependency order and cycle/missing-dependency validation.
- Added Docker Compose start/stop/status/log operations and optional HTTP health probes.
- Added process lifecycle smoke-test script using an isolated sleep process.
- Updated setup, architecture and safety documentation.

## 0.3.0 — Architecture diagram and compiler-assisted discovery

- Added directed connections between architecture blocks.
- Added visual before/after states for architecture delta.
- Added Roslyn syntax-tree parsing for C# endpoint and MassTransit candidates.
- Added optional TypeScript Compiler API analysis of cross-package module edges.

## 0.2.0 — Snapshot comparison and repository refresh

- Added architecture snapshots, deltas and Git-state fingerprinting.
- Added refresh loop for branch, HEAD and working-tree changes.

