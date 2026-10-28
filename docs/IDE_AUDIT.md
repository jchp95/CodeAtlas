# Auditoría del IDE y plan de evolución (Fase 1)

Fecha de la auditoría: 2026-10-28 · Versión base: 0.23.0 · Alcance: repositorio completo (`engine/`, `macOSApp/`, `scripts/`, `tests/`, `vscode-extension/`, `docs/`).

Esta auditoría describe lo que el código **hace hoy**, comprobado leyendo el código y ejecutando las suites; no lo que los documentos prometen. Donde algo no se pudo comprobar se indica.

## 1. Realidad técnica

| Aspecto | Hallazgo |
|---|---|
| Arquitectura | Dos procesos. **App macOS** SwiftUI (`macOSApp`, Swift tools 6.0, modo de lenguaje Swift 5, macOS 14+, 78 archivos, ~9,7 k líneas) y **motor .NET 10** (`engine/CodeAtlas.Engine`, consola, Roslyn 5.9.0). La app invoca el motor como subproceso por comando y lee JSON por stdout (`EngineClient*`, `ProcessExecutionService`, `EngineInvocationResolver`). |
| Motor | ~6,6 k líneas C# (incluida la Fase 2) sin namespaces (tipos internos globales), organizado por *features* (`Architecture`, `BusinessFlows`, `HttpTesting`, `StackPilot`, `Workspace`, ahora `Findings` y `CodeAnalysis`). Un único `Program.cs` enruta los subcomandos. |
| Roslyn | `RepositoryScanner` crea **una `CSharpCompilation` por `.csproj`** con referencias de plataforma + `compile` assets de `obj/project.assets.json` si existe. No ejecuta MSBuild, no restaura paquetes, no ejecuta generadores. |
| Editor | `NativeCodeEditor` (NSTextView), resaltado y *outline* heurísticos, cliente LSP genérico (`LanguageServerClient`) que lanza servidores externos del `PATH` (`csharp-ls`, `sourcekit-lsp`, `typescript-language-server`, …). No se incluye ningún servidor. |
| Pruebas | 26 scripts en `scripts/` (con el nuevo) (Node + `swiftc` + `dotnet run`), encadenados por `verify-before-launch.sh`. No hay proyecto de pruebas xUnit/NUnit para el motor; las aserciones están en JavaScript sobre el JSON del motor. |
| Estado del build nativo | **`swift build` falla en este equipo antes de compilar código**: el manifiesto `Package.swift` no compila contra el `PackageDescription` instalado (Command Line Tools con *swiftinterface* y `libPackageDescription.dylib` desalineados). No es un defecto del código de la app; se resuelve reinstalando CLT o apuntando a Xcode. Todo lo que depende de `swift build` queda **sin verificar** en esta auditoría. |

## 2. Matriz de capacidades

Clasificación: **V** implementada y verificada · **P** parcial · **UI** solo interfaz · **S** simulada · **A** ausente · **B** bloqueada por dependencia · **R** requiere rediseño.

| # | Capacidad (según el prompt maestro) | Estado | Evidencia / brecha |
|---|---|---|---|
| 1 | Escaneo de arquitectura (proyectos, referencias, endpoints, mensajería, DI) | **V** | `RepositoryScanner*`, `smoke-test.sh`, `distributed-flow-test.sh`. Límites documentados en `SEMANTIC_ANALYSIS.md`. |
| 2 | Grafo de dependencias entre proyectos y diagrama por carriles | **V** | `ArchitectureDiagramView*`, `runtime-linker-test.sh`. Es grafo **de proyectos/contratos**, no de métodos. |
| 3 | Trazado de flujos de negocio (endpoint → métodos → mensajes) | **P** | `BusinessFlowAnalyzer*` (profundidad 7, 24 pasos, DI únicas). Sin delegados, reflexión ni despacho virtual no resuelto; sin TypeScript. Es el embrión del grafo de llamadas. |
| 4 | Grafo de llamadas completo con clasificación virtual/interfaz/delegado/reflexión | **A** | Solo existe dentro del trazador de flujos; sin modelo propio. → Fase 3. |
| 5 | Análisis de impacto de cambios (referencias directas/indirectas, pruebas) | **A** | Hay *diff* de snapshots de arquitectura (`ArchitectureComparer`), no impacto de un símbolo. → Fase 3. |
| 6 | Reglas arquitectónicas configurables (Clean/DDD/VSA) y ciclos | **P** | Agrupación heurística Clean Architecture por sufijos (`CleanArchitectureLayerGrouper`). Sin reglas de acceso configurables ni detección de ciclos. → Fase 3. |
| 7 | Diagnósticos Roslyn de calidad/rendimiento/asincronía/recursos | **V (parcial, nuevo)** | Fase 2: 11 reglas en `Features/CodeAnalysis`, verificadas por `code-analysis-test.sh`. Ver `CODE_ANALYSIS.md` para las reglas pendientes. |
| 8 | Modelo común de hallazgos con evidencia y niveles de validación | **V (núcleo, nuevo)** | `Features/Findings`. Falta almacén persistente con historial entre ejecuciones y adaptadores para arquitectura. |
| 9 | Correcciones automáticas con diff previo y respeto a cambios del usuario | **V (2 reglas)** | `ATL1003`, `ATL1005`; hash base obligatorio. Sin `CodeFixProvider` de Roslyn (se emiten ediciones de texto propias). |
| 10 | Análisis incremental, cancelación, caché, paralelismo, telemetría del analizador | **V (nuevo)** | Caché por proyecto, `SIGTERM`/`SIGINT`, `Parallel.ForEach` por documento, telemetría por regla. Granularidad de caché = proyecto. |
| 11 | Análisis interprocedimental (propagación de nulos, recursos, excepciones) | **A** | Las reglas actuales son intraprocedimentales. → Fase 3 (sobre el grafo de llamadas). |
| 12 | Lógica de negocio: invariantes, reglas duplicadas, transiciones de estado | **A** | Solo trazado de flujos. → Fase 3. |
| 13 | Depurador (breakpoints, pila, variables, hilos, pasos) | **A** | No hay código. Ni `netcoredbg` ni `vsdbg` están instalados. → Fase 4, **B** por dependencia. |
| 14 | Configuración y exploración de excepciones | **A** | Sin código. → Fase 4. |
| 15 | Diagnóstico histórico tipo IntelliTrace | **A** | Sin código. Viabilidad por evaluar en Fase 4 (no se promete reconstrucción de variables). |
| 16 | Profiling de CPU / memoria / GC / concurrencia | **A** | Sin código. Ni `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump` ni `dotnet-dump` instalados. → Fase 5, **B**. |
| 17 | Comparación de rendimiento entre ejecuciones | **A** | Sin código. → Fase 5. |
| 18 | Observabilidad (OpenTelemetry, trazas distribuidas) | **A** | Hay "tráfico observado" como hito futuro en `PRODUCT_SCOPE.md`. → Fase 6. |
| 19 | Test Explorer (descubrir, ejecutar, depurar, cobertura) | **P** | `BuildConsoleModel` ejecuta `dotnet test`/`npm test`/`swift test` como comando y muestra salida cruda. No descubre pruebas ni parsea resultados. → Fase 4/7. |
| 20 | Servicios de lenguaje (completion, definición, diagnósticos) | **P** | Cliente LSP con servidor falso en pruebas; ningún servidor real verificado. |
| 21 | Operaciones locales (StackPilot: procesos, Docker Compose, logs, health) | **V** | `StackPilotCli*`, `stackpilot-smoke-test.sh`. |
| 22 | Cliente/catálogo HTTP (endpoints, `.http`, ejecución) | **V** | `HttpTestingCatalogBuilder*`, `http-catalog-test.sh`, `http-request-execution-test.sh`. |
| 23 | Puente VS Code | **V** | `vscode-extension-test.sh`. |
| 24 | Asistente IA (Ollama / Anthropic), opt-in | **V** | `AIAssistantService`; no envía código sin activar contexto. |
| 25 | Vistas de UI para: Code Analysis, Call Graph, Debugger, Exception Explorer, Timeline, CPU/Memory Profiler, Concurrency, Production Readiness, Performance Comparison, Unified Findings | **A** | No existen. Las vistas de este bloque solo se crearán cuando haya servicios reales detrás (regla 12 del prompt). La app no pudo compilarse aquí, así que no se añadió SwiftUI sin poder verificarlo. |
| 26 | Seguridad: sin ejecución automática, secretos fuera de snapshots/logs | **P** | Política documentada y respetada en StackPilot; el análisis estático **no ejecuta código del usuario**. Falta política para volcados/trazas (Fase 5). |

Sin hallazgos de capacidades **simuladas** (S) ni "solo interfaz" (UI) en el motor. La UI nativa no se auditó línea a línea: no se pudo compilar ni ejecutar.

## 3. Brechas y riesgos

1. **Modelo de proceso.** El motor es *un proceso por comando*. Depuración y profiling requieren sesiones largas con eventos (DAP/EventPipe). Decisión: no convertir el motor en servidor; añadir procesos de sesión independientes (adaptador DAP, recolector EventPipe) que emitan JSON de líneas, y mantener el motor para análisis.
2. **Compilación aproximada.** Sin `dotnet restore` los símbolos de paquetes no resuelven. El analizador lo declara en `warnings` y las reglas que dependen de esos símbolos se omiten, no adivinan. Se corrigió un fallo real que se descubrió al probar: los `ImplicitUsings` de MSBuild (`obj/*.GlobalUsings.g.cs`) no estaban en la compilación, de modo que `Task`, `List<T>` o `HttpClient` quedaban sin resolver en cualquier proyecto moderno.
3. **Pruebas en JavaScript.** Funcionan y están integradas, pero no hay pruebas unitarias del motor en .NET. Se mantiene la convención existente para no crear un segundo marco; se reevaluará en Fase 7.
4. **Build nativo roto por toolchain** (ver §1). Hasta resolverlo no se puede verificar ningún cambio en Swift.
5. **Árbol de trabajo sin confirmar.** Hay cambios del usuario sin *commit*. Esta fase solo **añade** archivos y modifica dos del motor (`RepositoryScanner.Projects.cs`, `JsonDefaults.cs`) y `Program.cs`; no toca Swift.

## 4. Arquitectura objetivo

```
App SwiftUI ──(JSON/stdout, procesos hijos cancelables)──► Motor .NET
                                                            ├─ Findings (modelo común, fusión, identidad estable)   ← Fase 2 ✔
                                                            ├─ CodeAnalysis (reglas Roslyn, caché, correcciones)   ← Fase 2 ✔
                                                            ├─ CallGraph / Impact / ArchitectureRules / Business   ← Fase 3
                                                            ├─ Sesiones: Debug (DAP), Runtime (EventPipe)           ← Fases 4–5
                                                            └─ Production (OTel, informes, regresiones)             ← Fase 6
```

Principios: los subsistemas **producen `Finding`** (mismo contrato) y evidencias; la UI solo consume JSON. Ningún subsistema duplica la carga de proyectos: todos usan `RepositoryScanner.LoadDotnetProject`.

## 5. Plan por fases y criterios de aceptación

| Fase | Contenido | Criterio de aceptación verificable | Estado |
|---|---|---|---|
| 1 | Auditoría, contrato `Finding`, pruebas de referencia | Este documento; `Finding.cs`; `code-analysis-test.sh` | **Hecha** |
| 2 | Motor Roslyn: 11 reglas, config, caché, cancelación, telemetría, 2 correcciones | `code-analysis-test.sh` verde (15 defectos sembrados detectados, 0 falsos positivos en el archivo limpio, ids estables, caché, cancelación, correcciones con hash) | **Hecha (núcleo)**; pendiente: reglas de nulabilidad/reflexión/concurrencia, `CodeFixProvider`, vista en la app |
| 3 | Grafo de llamadas, ciclos, reglas arquitectónicas, impacto, reglas de negocio | Grafo con resolución clasificada; impacto de un símbolo con consumidores y pruebas | Pendiente |
| 4 | Depuración (DAP), excepciones, Test Explorer real | Sesión con breakpoint real contra un proceso de prueba; resultados de pruebas parseados de TRX | Pendiente · **B**: requiere instalar `netcoredbg` |
| 5 | Profiling EventPipe, GC, concurrencia, comparación | Traza real de un proceso de prueba; comparación con variabilidad | Pendiente · **B**: requiere `Microsoft.Diagnostics.NETCore.Client` / `dotnet-trace` |
| 6 | Observabilidad, informe de preparación para producción | Informe reproducible que distingue estático/medido | Pendiente |
| 7 | Endurecimiento: soluciones grandes, seguridad, documentación | Benchmarks de escala y revisión de superficie de ataque | Pendiente |

Orden: se mantiene el del prompt. La Fase 3 depende del grafo de llamadas y las Fases 4–5 de herramientas externas que hoy no están instaladas; por eso 3 precede a 4.
