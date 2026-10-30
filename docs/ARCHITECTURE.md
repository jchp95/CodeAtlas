# Arquitectura del producto

## Capas

1. **SwiftUI desktop shell** — selector de repositorios, diagrama de bloques por capas, panel de detalles, cambios y vigilancia Git; ahora muestra estado operativo y controles contextuales de los servicios asociados.
2. **EngineClient** — ejecuta el motor .NET como proceso hijo, lee stdout/stderr en paralelo, consulta las operaciones locales y calcula huellas de estado Git.
3. **CodeAtlas.Engine** — escanea proyectos, analiza sintaxis C# con Roslyn, construye relaciones y compara snapshots.
4. **TypeScript analyzer** — script Node opcional que carga el TypeScript Compiler API ya instalado en el workspace y resuelve imports entre paquetes locales. No instala dependencias.
5. **Snapshot store** — conserva la última captura por ruta de repositorio en el directorio de datos del usuario; no modifica los repositorios analizados.
6. **StackPilot runtime manager** — perfiles por workspace, procesos locales administrados, Docker Compose, health checks y logs locales.
7. **ArchitectureRuntimeLinker** — enlaza un proyecto fuente con una plantilla de ejecución a partir de la raíz de workspace, el argumento `.csproj`, el directorio de un package o un nombre único. No crea ni edita configuraciones automáticamente; rechaza empates ambiguos.
8. **HTTP Endpoint Explorer** — catálogo efímero de endpoints de código, peticiones `.http` / `.rest`, entornos Rider/REST Client y llamadas HTTP salientes; permite ejecutar solicitudes y generar archivos `.http`.

9. **Findings** (`Features/Findings`) — contrato común de hallazgos (id estable, nivel de validación, evidencias, limitaciones, reproducción, corrección opcional) usado por el análisis Roslyn, el grafo y las reglas arquitectónicas. Ver `docs/CODE_ANALYSIS.md`.
10. **Code analysis** (`Features/CodeAnalysis`) — reglas Roslyn por documento con caché por proyecto y correcciones con diff previo.
11. **Call graph** (`Features/CallGraph`, `Features/Architecture/Rules`) — grafo de llamadas semántico por solución, ciclos, reglas de arquitectura configurables, impacto de cambios, reglas interprocedimentales y flujos proyectados. Comparte con el escaneo la carga de proyectos y el descubrimiento de puntos de entrada; ver `docs/CALL_GRAPH.md`.

## Modelo de arquitectura

- `ProjectNode`: proyecto .NET o package Node/Next; rutas/endpoints, paquetes, referencias, contratos candidatos, URLs HTTP, direcciones de escucha y evidencia de código (símbolo, archivo, línea, categoría y confianza).
- `ArchitectureRelation`: origen, destino, tipo, etiqueta, evidencia y confianza.
- `ArchitectureSnapshot`: commit/rama, proyectos, relaciones, warnings, estado de Git, métricas y delta respecto al snapshot previo.
- `ArchitectureDelta`: proyectos añadidos/eliminados/modificados y relaciones añadidas/eliminadas.
- `ArchitectureDiagramLayout`: proyección de interfaz que asigna bloques a cuatro carriles heurísticos y dibuja conexiones dirigidas. No se persiste ni modifica el modelo base.
- `ArchitectureRuntimeBinding`: relación de interfaz entre un bloque de código y un servicio declarado en el perfil StackPilot del mismo workspace.

## Relaciones actualmente detectadas

| Tipo | Evidencia | Confianza |
|---|---|---|
| `project-reference` | `ProjectReference` del `.csproj` | Confirmada |
| `message-flow` | Símbolo de contrato resuelto cuando es posible; candidatos explícitos de `Publish`/`Send` y `IConsumer<T>` con contrato coincidente | Inferida: no demuestra entrega runtime |
| `http-call` | URL absoluta en fuente/configuración cuyo host coincide heurísticamente con otro proyecto, o puerto loopback que coincide con `launchSettings` | Inferida |
| `workspace-dependency` | Dependencia de `package.json` cuyo nombre coincide con otro package detectado | Inferida |
| `module-import` | Import/export/dynamic import/require resuelto por TypeScript Compiler API entre paquetes distintos | Confirmada a nivel de resolución de fuente |

Roslyn construye un `CSharpCompilation` y `SemanticModel` por proyecto con árboles de fuente, ensamblados del runtime y las referencias `compile` de `obj/project.assets.json` cuando están disponibles. Se resuelven símbolos de contratos y tipos de DI cuando el contexto lo permite; cada resultado conserva archivo/línea y marca `resolved` o `candidate`. El analizador no ejecuta MSBuild ni source generators y puede no resolver referencias de proyectos no compiladas, paquetes faltantes o convenciones de enrutamiento MassTransit. En la interfaz las responsabilidades de carril siguen siendo aproximaciones basadas en nombres, rutas, endpoints y candidatos a mensajes.

## Principios

- No ejecutar comandos destructivos de Git ni fusionar ramas.
- No detener procesos no iniciados/gestionados por CodeAtlas.
- Separar relaciones declaradas, inferidas y observadas en runtime.
- Mostrar evidencia para relaciones estáticas o inferidas.
- Mantener los snapshots fuera de los repositorios analizados.
- La pasada TypeScript nunca instala paquetes y tiene timeout para no bloquear indefinidamente el análisis.
- Los perfiles de StackPilot están en JSON fuera del repositorio y los servicios generados se crean desactivados.
- Los bloques muestran el estado de StackPilot para servicios asociados. Al seleccionar un bloque se ofrecen inicio/parada y logs; los servicios desactivados deben revisarse y habilitarse desde el dashboard.
- Para procesos locales, StackPilot registra PID + hora de inicio, redirige stdout/stderr a logs y solo intenta detener procesos que coinciden con su propio registro.
- Las operaciones Compose se limitan al archivo y servicio declarados; no equivalen a una orquestación remota.

## Catálogo HTTP efímero

El catálogo se construye a demanda a partir del workspace seleccionado y no forma parte de `ArchitectureSnapshot`. Incluye endpoint, método, ruta, archivo/línea, tipo de request cuando es inferible, ejemplos JSON editables, peticiones existentes y candidatos de llamadas salientes. Las asociaciones de tests usan método + ruta normalizada y priorizan proyecto/repositorio. El analizador no ejecuta solicitudes durante el análisis.

El runner admite un subconjunto común de archivos Rider / IntelliJ HTTP Client y VS Code REST Client: separadores `###`, métodos, URL, headers, cuerpo, variables declaradas y archivos de entornos JSON. Los valores de un entorno privado en un directorio tienen prioridad sobre el público del mismo directorio; directorios más cercanos al archivo de petición sobrescriben los padres. Las peticiones se ejecutan solo por acción explícita del usuario.

Las llamadas salientes encontradas por C# o TypeScript se enlazan por método y ruta cuando hay coincidencias en otro proyecto. A falta de resolución completa de URL/configuración, se etiquetan como inferidas o ambiguas; no representan telemetría de ejecución ni demuestran que el destino real sea ese endpoint. El runner impone timeout de 30 s y captura como máximo 512 KiB del cuerpo de respuesta.
## Estructura de código y responsabilidades

La distribución física por funcionalidades y los puntos de extensión están documentados en [`CODE_STRUCTURE.md`](CODE_STRUCTURE.md). La organización separa análisis, modelos, parsing, transporte HTTP, runtime y presentación; las clases parciales comparten implementación sin generar duplicados ni cambiar los contratos del motor.

