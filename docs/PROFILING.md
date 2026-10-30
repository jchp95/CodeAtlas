# Profiling y diagnóstico de runtime (Fase 5)

Reutiliza las herramientas oficiales de .NET y analiza su salida; no reimplementa el muestreo. Prueba: `./scripts/profiling-test.sh` (procesos reales con forma conocida, `tests/fixtures/profiling/Busy`).

## Herramientas (instaladas con autorización del usuario, a nivel de usuario)

`dotnet-trace`, `dotnet-counters` y `dotnet-gcdump` 10.0.750501, desde nuget.org (con `--configfile` que excluye el feed privado de la máquina), con `dotnet tool install <nombre> --tool-path ~/.codeatlas/tools/dotnet-tools`. Orden de búsqueda: variables `CODEATLAS_DOTNET_TRACE|COUNTERS|GCDUMP`, esa carpeta, `PATH`. Se retiran borrando la carpeta. `runtime doctor` informa del estado; sin herramienta, el perfil falla con `toolMissing` (exit 5) e instrucciones. **No se usa `dotnet-dump`**: un volcado contiene la memoria del proceso y merece una decisión de privacidad aparte (y `dotnet-monitor`/OpenTelemetry son de la Fase 6).

## Comandos

```
runtime doctor
profile cpu      <repo> --program RUTA/App.dll [--args "a b"] --allow-execution [--duration N] [--repeat N] [--hot-threshold 0.25] [--timeout-seconds N] [--out FILE]
profile counters <repo> --program RUTA/App.dll --allow-execution [--duration N] [--refresh-seconds N] [--out FILE]
profile heap     <repo> --program RUTA/App.dll --allow-execution [--snapshots N] [--interval-seconds N] [--warmup-seconds N] [--out FILE]
profile <cpu|counters|heap> <repo> --attach PID --allow-attach ...
profile compare  <informe-base.json> <informe-actual.json>
```
Lanzar exige `--allow-execution`; adjuntar exige `--allow-attach`; sin la bandera se devuelve el motivo y exit 7 y no se escribe nada. Cancelar (SIGTERM) mata el trazador **y** el programa lanzado (exit 130); un proceso adjuntado nunca se mata. Los trazas y volcados se guardan con permisos de usuario en `~/Library/Application Support/CodeAtlas/profiles/<repo>/…` (`CODEATLAS_DATA_DIR`) y se borran a los 14 días. Un `.gcdump` contiene nombres de tipos y tamaños, no el contenido de los objetos; una traza incluye nombres de métodos y la línea de comandos del proceso: no se comparten sin revisar.

## CPU (`profile cpu`)

`dotnet-trace --profile dotnet-sampled-thread-time`. El marcador final de cada pila (`CPU_TIME` / `BLOCKED_TIME`) separa **tiempo en CPU** de **tiempo bloqueado**, que se informan aparte. Por método: CPU *propia* (la suya más la de código de framework que llamó; es la medida que apunta a código de la solución y suma como mucho el 100 %), CPU exclusiva, inclusiva, bloqueo, rango mínimo–máximo entre ejecuciones y hallazgos estáticos del mismo método. Los métodos se resuelven al grafo de llamadas (`nodeId`, ubicación). Árbol de llamadas podado (≥ 1 %, profundidad 14). Con `--repeat N` los valores son medianas y se conserva el rango.
`PRF0001` (origen `profile`, validación `measured`) señala métodos con CPU propia ≥ umbral (25 % por defecto), nunca el punto de entrada, y dice que **un punto caliente no es un defecto**: recomienda un benchmark (BenchmarkDotNet) antes de cambiar nada.
Límites declarados: muestreo (~1 muestra/ms/hilo; no hay recuentos de llamadas), sesgo de *safe point* (el muestreador detiene el runtime en puntos seguros y puede cargar tiempo a un método vecino pequeño), inlining, calentamiento del JIT, medición de una sola máquina.

## Memoria, GC y concurrencia (`profile counters`)

`dotnet-counters` sobre el meter `System.Runtime`. Distingue **asignación** (tasa) de **retención** (tamaño del heap *tras* cada GC). Dos contadores son *up/down*: cada muestra es el cambio del intervalo, así que el nivel de cola y de hilos del pool se reconstruye sumando (el nivel inicial de la cola es desconocido (la suma puede salir negativa), así que se expresa relativo al momento más vacío observado).

| Hallazgo | Criterio (constantes, ver `CounterProfiler`) |
|---|---|
| `PRF0101` crecimiento sostenido del heap post-GC | ≥ 8 muestras, ≥ 80 % de intervalos sin descenso, final ≥ 1,2× mínimo y ≥ 8 MiB. **Compatible con una fuga, no la demuestra** (cachés y calentamiento crecen igual) |
| `PRF0102` tasa de asignación alta | media ≥ 50 MiB/s; si el heap post-GC es plano lo dice: *churn*, no retención |
| `PRF0103` tiempo en pausas de GC | ≥ 5 % de la ejecución |
| `PRF0201` presión del thread pool | cola (relativa al momento más vacío observado, cota inferior) ≥ 5 y número de hilos creciendo hasta ≥ 2× núcleos; enlaza hallazgos estáticos `ATL1001` como **candidatos**, no como causa |
| `PRF0202` contención de locks | ≥ 10/s de media y ≥ 100 en total; no dice *qué* lock |

Con menos de 8 muestras no se informa ninguna tendencia y se avisa.

## Heap (`profile heap`)

`dotnet-gcdump collect` (objetos alcanzables; fuerza un GC) y `report`. Compara el primer y último snapshot por tipo (bytes, objetos) y emite `PRF0301` si el heap alcanzable crece ≥ 20 % y ≥ 10 MiB y un tipo explica ≥ 50 % del crecimiento. **gcdump no trae rutas de retención**: nombra el tipo, no quién lo retiene (hace falta PerfView/Visual Studio sobre un volcado para eso). En el informe de gcdump "Object Bytes" es el tamaño por objeto; el total de una fila es bytes × recuento, y se avisa si los totales no cuadran con el heap declarado.

## Comparación (`profile compare`)

Mismo tipo de informe en ambos lados. Un cambio se llama `regression`/`improvement` solo si es ≥ 25 % (y, para cuotas de CPU por método, de al menos 5 puntos porcentuales; los métodos por debajo del 5 % en ambos lados no se juzgan) **y** los rangos de las ejecuciones repetidas de cada lado no se solapan (≥ 3 ejecuciones por lado); un cambio grande con una sola ejecución o con rangos solapados es `inconclusive`; por debajo del 25 %, `noSignificantChange`. Contadores y heap comparan una medición por lado, por lo que un cambio grande es `inconclusive` por diseño. Una optimización no se declara exitosa porque el código parezca más sencillo.

## Pendiente / no cubierto

- Tareas, bloqueos y *deadlocks* por volcado de pilas (`dotnet-stack`/`dotnet-dump`), asignaciones por método y OTel: no implementados. La inanición del pool y la contención se detectan por síntomas medibles, no por la causa.
- BenchmarkDotNet no se ejecuta: se recomienda y no se integra todavía (cambios pequeños requieren benchmarks repetibles).
- Contenedores/procesos remotos: solo procesos locales.
- Sin vistas en la app (Swift sigue sin poder compilarse en este equipo).
