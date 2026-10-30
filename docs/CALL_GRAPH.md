# Grafo de llamadas, arquitectura e impacto (Fase 3)

Todo se calcula con símbolos y operaciones de Roslyn (`IOperation`, `SemanticModel`), nunca por búsqueda de texto. Nada ejecuta código del usuario, MSBuild ni restaura paquetes.

## Auditoría previa (hallazgos que condicionaron el diseño)

- **`RepositoryScanner` compilaba cada `.csproj` aislado**, sin sus `ProjectReference`: una llamada de un proyecto a otro no podía enlazarse. El grafo usa `LoadLinkedDotnetProjects`, que crea las compilaciones en orden topológico y adjunta las compilaciones referenciadas (transitivas) como referencias de metadatos. El escaneo de arquitectura **no cambia** (sus resultados están cubiertos por 20 suites). Una referencia que cierra un ciclo se descarta y se avisa: una compilación no puede referenciarse a sí misma.
- **`ImplicitUsings` y `obj/project.assets.json`** ya estaban resueltos en la compilación compartida (Fase 2: usings implícitos del SDK y `<Using>`; assets de paquetes si existen).
- **`BusinessFlowAnalyzer`** es un trazador sintáctico (profundidad 7, 24 pasos) con mapa DI de registros únicos, enlace de `Send(request)` a `IRequestHandler` (por interfaz, con convención de nombre como candidato) y un respaldo por sintaxis cuando Roslyn no enlaza. Su descubrimiento de puntos de entrada era privado: ahora `DiscoverSharedEntryPoints` lo expone y el grafo parte de **los mismos** puntos de entrada.
- **Contratos JSON**: `Program.cs` enruta subcomandos y serializa con `JsonDefaults` (camelCase, enums como cadenas). Los comandos nuevos siguen esa convención; `analyze`, `analysis-rules`, `fix` y `scan` no cambian.
- **Cambios pendientes del usuario**: no se tocó ningún archivo que ya estuviera modificado y no perteneciera a las Fases 1–2 (catálogo HTTP, app Swift, etc.).

## Comandos

```
graph build <repo> [--no-cache] [--max-dispatch-targets N] [--max-parallelism N] [--out FILE]
graph calls <repo> <símbolo> [--direction callers|callees] [--depth N]
graph cycles <repo>
graph flows <repo>
graph analyze <repo>                      # reglas interprocedimentales ATL3101–ATL3105
architecture-rules <repo>                 # configuración leída (sin construir el grafo)
architecture-check <repo>                 # ciclos + reglas configuradas, como hallazgos
impact <repo> <símbolo> [--depth N] [--project NOMBRE]
```

Todos imprimen un JSON con `schemaVersion` (actualmente **1**). Códigos de salida: `0` correcto, `2` uso, `3` símbolo no encontrado, `6` símbolo ambiguo (las coincidencias van en la respuesta), `130` cancelado (SIGINT/SIGTERM; se devuelve el informe parcial marcado como cancelado). Límites comunes: `--depth` (8), `--max-results` (500), `--max-dispatch-targets` (25).

`<símbolo>` puede ser un **id estable** (`N-…`), una **posición** `ruta/Archivo.cs:LINEA[:COL]` (el miembro más interno que la abarca) o un **nombre** (`Tipo.Método`, con o sin firma).

## Contrato del grafo (v1)

`nodes[]`: `id`, `symbolId`, `kind` (method, constructor, accessor, operator, localFunction, lambda, initializer), `project`, `name`, `signature`, `containingType`, `namespace`, `accessibility`, `isExternal`, `isAbstract`, `isVirtual`, `tags` (`async`, `token-param`, `test`, `entry:http`, `entry:consumer`, `blocking`, `remote:HTTP|Database|Message`, `disposable-creator`, `implicit`), `location` + `otherLocations` (métodos parciales) + `endLine`.
`edges[]`: `from`, `to` (**nulo si el destino no se conoce**), `resolution`, `certainty`, `siteId`, `site` (archivo y línea de la llamada), `inLoop`, `inTry`, `passesToken`, `targetAcceptsToken`, `openWorld`, `truncated`, `label`, `reason`.
Además: `entryPoints[]`, `dispatch[]` (implementaciones/overrides conocidos de cada miembro declarado), `nullFlows[]`, `projectReferences[]`, `projectFiles`, `limitsApplied[]`, `completeness`, `metrics`, `status` (`completed|cancelled|limitReached`) y `warnings[]`.

### Clases de resolución y certeza

| `resolution` | Significado | `certainty` |
|---|---|---|
| `direct`, `extensionMethod`, `objectCreation`, `propertyAccess` | Destino único enlazado por Roslyn | `proven` |
| `interfaceDispatch`, `virtualDispatch` | Una arista al miembro **declarado** (`proven`: la referencia existe) y una arista por cada implementación/override conocido (`potential`). `openWorld` si el miembro es visible fuera de la solución | `proven` / `potential` |
| `lambda`, `delegate` (método de grupo) | Se crea aquí; quien lo recibe decide si se ejecuta | `potential` |
| `delegate` sin destino | Invocación de un delegado: destino desconocido | `unknown` |
| `mediatorDispatch` | `Send(request)` enlazado por símbolo a las implementaciones de `IRequestHandler` de ese tipo de request | `potential` |
| `syntaxInferred` | Roslyn no pudo enlazar; coincide el nombre del tipo del receptor, el método y la aridad, y es único | `potential` |
| `unresolved` | Roslyn no pudo enlazar la llamada (referencia ausente o error) | `unknown` |

Despacho: se aplica análisis de jerarquía de clases sobre todos los proyectos analizados, filtrado por el tipo estático del receptor. Se evita con: llamadas sobre tipos/miembros sellados, métodos de `object`, y llamadas `base.`.

### Completitud

`completeness.closed` es `true` **solo** si no hay llamadas sin resolver, ni invocaciones de delegados, ni despacho sobre miembros visibles desde fuera de la solución, ni abanico truncado, y la construcción no se interrumpió. En cualquier otro caso el grafo se declara **no completo** y se explica por qué. Un grafo con todo `internal` y enlazado estáticamente sí puede ser cerrado (fixture `closed`).

### Identidad estable

El id de nodo es el hash de `proyecto + documentation comment id` del símbolo (espacio de nombres, tipo, miembro, tipos de parámetros, aridad genérica). **Conservan** la identidad: mover código entre líneas o archivos, reformatear, cambiar cuerpos, repartir un tipo `partial` entre archivos, renombrar parámetros, métodos genéricos instanciados (se colapsan a su definición), la parte de implementación/definición de un método parcial (un solo nodo con ambas ubicaciones). **Crean un símbolo nuevo**: renombrar el miembro o su tipo, cambiar el espacio de nombres, cambiar tipos de parámetros o aridad genérica, mover a otro proyecto (o renombrarlo). Las lambdas se identifican por propietario + orden de aparición: insertar una lambda antes de otra del mismo método renumera las siguientes. La ubicación es metadato actualizable. Ids de aristas: hash de origen + destino + sitio + resolución; el sitio se numera por propietario y destino, no por línea.

## Ciclos

- **Proyectos** (`ATL3002`, error, `staticProven`): ciclos de `ProjectReference`, con la línea del `.csproj` como evidencia.
- **Llamadas** (`ATL3001`): componentes fuertemente conexos sobre aristas *definidas* (enlazadas estáticamente) = `staticProven`; los que necesitan despacho/delegado/lambda para cerrarse = "posible", `staticRisk`, `info`. **Recursión legítima** (un miembro que se llama a sí mismo, o miembros del mismo tipo entre sí) se lista en `legitimateRecursion` y **no** genera hallazgo; solo los ciclos que cruzan tipos.
- **Capas** (`ATL3003`): si hay una regla `noCycles` con `scope: "layers"`.

## Reglas arquitectónicas

`codeatlas.architecture.json` en la raíz. Sin el archivo no se aplica **ninguna** regla de capas: no hay convención Clean/DDD/VSA incorporada. Tipos de regla: `forbidden` (capa → capas prohibidas), `allowedOnly` (lista blanca), `forbiddenProjects` (patrones de proyectos), `moduleBoundary` (acceso solo por espacios de nombres públicos), `noCycles`. Las capas se asignan por patrón de proyecto o de espacio de nombres. Las **excepciones** exigen `reason`; una sin motivo se ignora y se avisa. Solo se juzgan dependencias *demostradas* (referencias de proyecto y llamadas enlazadas estáticamente): un destino posible por despacho no prueba una dependencia. `severity` es configurable por regla.

## Análisis de impacto

`impact` devuelve: referencias directas, consumidores indirectos (con camino), implementaciones y contratos afectados, espacios de nombres, proyectos y capas, pruebas relacionadas (métodos con atributos de prueba entre los consumidores), flujos de negocio (puntos de entrada que alcanzan el símbolo) y riesgos de contrato (API pública, implementaciones, contrato implementado, endpoint HTTP, contrato de mensaje). Cada elemento es `proven` (alcanzado por referencias que existen en el código) o `potential` (necesita abanico de despacho, lambda o delegado).
`exhaustive` es `true` solo si el grafo está cerrado, no se alcanzó ningún límite y el miembro no es visible desde fuera; `statement` explica en cada caso qué falta. Nunca modela reflexión, resolución DI por cadena/convención ni código fuera del directorio.

## Reglas interprocedimentales (`graph analyze`)

Cada regla es una búsqueda inversa acotada (profundidad `--depth`) desde un conjunto pequeño de nodos semilla, calculado una vez; no se recorre el grafo por diagnóstico. Siguen solo aristas definidas, de modo que un camino informado existe en el código.

| Id | Qué detecta | Validación |
|---|---|---|
| ATL3101 | Método asíncrono → ayudante síncrono → espera bloqueante (`Wait`, `.Result`, `GetResult`) | staticProven (el camino); el efecto no se mide |
| ATL3102 | Bucle que llama a un método que acaba en HTTP/BD/mensajería | staticRisk |
| ATL3103 | `CancellationToken` recibido, perdido en un método sin parámetro, y luego una API que lo admitía | staticProven |
| ATL3104 | Resultado de una fábrica que devuelve un `IDisposable` nuevo, descartado o sin liberar por el llamador | staticRisk |
| ATL3105 | Posible nulo según el análisis de flujo del compilador (CS8602/CS8604) cuyo origen es una llamada a un método de la solución con retorno anulable | staticRisk |

**No implementadas por no poder dar resultados fiables en esta fase:** propagación de excepciones hasta los puntos de entrada (no se modela el middleware global de errores, que convertiría casi todo en falso positivo) y propagación de nulos que el compilador no analice (sin un análisis de flujo propio no se afirma nada).

## Flujos de negocio compartidos

`graph flows` proyecta el grafo al contrato existente `BusinessFlow`/`BusinessFlowStep`, de modo que la UI y los *diffs* de snapshots pueden consumir ambos motores. Los pasos salen de símbolos resueltos: llamadas (`resolved`), implementaciones/overrides por despacho y manejadores de request (`candidate`), inferencias por sintaxis (`inferred`), llamadas a APIs reconocidas de BD/HTTP/mensajería, y llamadas sin resolver como pasos explícitos. No se deduce significado de negocio del nombre de un método. El trazador heredado **se mantiene** para `scan`; una prueba (`call-graph-test.sh`, sección 9) exige que todo paso resoluble del trazador heredado esté en la proyección del grafo. Los pasos `message-publish` heredados se basan en el nombre del método y el grafo no los reproduce a propósito.
Los flujos describen caminos estáticos, nunca ejecuciones observadas.

## Rendimiento y límites (medido en este equipo, motor en Debug)

- Fixture `shop` (5 proyectos, 94 nodos, 75 aristas): ~1,5 s en frío.
- 4 proyectos con 12 006 métodos y 48 000 aristas: ~9,7 s en frío con 4 proyectos en paralelo (el coste dominante es el enlace semántico de Roslyn; los diagnósticos de nulabilidad añaden ~30 %); ensamblado global ~0,3 s.
- 400 proyectos mínimos: la carga de compilaciones supera los 5 s antes de empezar a extraer.
- **Caché por proyecto** (fragmentos) con clave = versión + límites + `.csproj` + `project.assets.json` + hash de todos los fuentes + claves de **todos los proyectos referenciados transitivamente**. Editar una hoja reextrae solo esa hoja; editar un proyecto del que dependen todos invalida todos (verificado). El ensamblado del despacho global se rehace siempre (barato).
- Límites: abanico de despacho (`--max-dispatch-targets`, se trunca de forma determinista y **cada arista del sitio queda marcada `truncated`**, además de `limitsApplied` y `completeness`), `maxEdges`, `maxNodes`, profundidad y resultados de consulta. Ningún límite se aplica en silencio.
- Cancelación cooperativa en carga, extracción y consultas.

## Limitaciones conocidas

- `dotnet restore` es necesario para los **paquetes NuGet** (se leen de `obj/project.assets.json`). Los *framework references* (ASP.NET Core) no están en ese archivo: se toman del targeting pack instalado en `packs/Microsoft.AspNetCore.App.Ref` (o del runtime compartido) cuando el proyecto usa el SDK Web. Otros frameworks (Windows Desktop, etc.) no se cargan.
- Sin `dotnet restore`, los símbolos de paquetes no resuelven: aparecen como `unresolved`/`syntaxInferred` y el grafo se declara incompleto.
- Los inicializadores de campo se atribuyen a un nodo `initializer` enlazado desde cada constructor que no delega con `this(...)`.
- No se modelan reflexión, `dynamic`, DI por convención, ni destinos de delegados recibidos como parámetro (solo se listan los sitios donde se crea la lambda/método de grupo).
- Las llamadas a miembros de `object` y a tipos sellados se tratan como directas.
- Las dependencias circulares de proyecto impiden compilar con referencias cruzadas; las llamadas a través de la arista descartada salen como no resueltas.
- La app SwiftUI aún no consume estos comandos (la compilación nativa sigue bloqueada por el toolchain de este equipo).
