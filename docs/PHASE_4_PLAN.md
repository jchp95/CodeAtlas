# Preparación de la Fase 4 — Depuración, excepciones y Test Explorer

Estado: **ejecutado** (ver `TESTING_AND_EXCEPTIONS.md` y `DEBUGGING.md`). El plan original se conserva debajo. Con autorización explícita del usuario se instaló `netcoredbg` a nivel de usuario (`~/.codeatlas/tools`).

## Qué aporta la Fase 3 a la Fase 4

- Un **mapa de símbolos** estable: una traza de pila (`Namespace.Tipo.Método(…)` + archivo:línea) se puede resolver a un nodo del grafo con `impact`/`graph calls` y navegar a sus llamadores y a los flujos que lo atraviesan.
- Un **grafo con caminos** para el análisis estático de excepciones: se puede calcular qué puntos de entrada alcanzan un `throw` por aristas definidas.
- Nodos con etiqueta `test` para vincular fallos de prueba con el código bajo prueba.

## Lo que se puede construir sin instalar nada (Fase 4a)

| Capacidad | Dependencia | Verificación prevista |
|---|---|---|
| Descubrir pruebas (`dotnet test --list-tests`) y ejecutarlas con `--logger "trx"` | SDK .NET ya presente | Fixture con xUnit/NUnit/MSTest mínimos; resultados leídos del TRX, nunca de la salida de consola |
| Parsear TRX (resultados, duración, mensaje, traza de pila) y relacionarlo con nodos `test` | ninguna | Comparar con TRX reales del fixture |
| Resolver trazas de pila a nodos del grafo y a `Finding` (origen `test`/`dynamic`) | grafo | Traza sintética + traza real de un fallo del fixture |
| Explorador de excepciones **estático**: `throw`, `catch` vacíos ya detectados (`ATL1004`) y caminos desde puntos de entrada | grafo | Fixtures con y sin `catch` en el camino |
| Repetir fallidas y comparar ejecuciones | TRX | Dos ejecuciones con un fallo intermitente simulado |

**Cobertura** exige un proveedor (coverlet es un paquete NuGet del proyecto de pruebas, no una herramienta global); se tratará como dependencia opcional detectada, nunca asumida.

## Lo que requiere una decisión del usuario (Fase 4b)

1. **Depurador.** Hay que evaluar integrar un adaptador DAP antes de escribir uno propio. Candidatos: `netcoredbg` (Samsung, código abierto) — no está instalado; `vsdbg` — no instalado y su licencia lo restringe a productos de Microsoft (verificar los términos vigentes antes de plantearlo). Instalar cualquiera modifica el entorno global: **pedir autorización explícita**.
2. **Diagnóstico de runtime (Fase 5).** `Microsoft.Diagnostics.NETCore.Client` es un paquete NuGet del propio motor (cambia `CodeAtlas.Engine.csproj` y requiere restaurar paquetes, es decir, red); `dotnet-trace`, `dotnet-counters`, `dotnet-gcdump` y `dotnet-dump` son herramientas globales no instaladas.
3. **Modelo de sesión.** El motor es "un proceso por comando"; un depurador necesita una sesión larga con eventos. Propuesta: un proceso de sesión aparte que hable DAP con el adaptador y emita JSON por líneas (un evento por línea) que la app consuma, manteniendo el motor para análisis.

## Honestidad de la fase

- "Test ejecutado" solo si hay TRX con resultado; una prueba descubierta pero no ejecutada se muestra como tal.
- Una traza de pila no reconstruye variables ni el pasado: sin captura real (depurador, volcado) el estado no se inventa.
- Sin depurador instalado, las funciones de depuración muestran un error explícito de dependencia ausente, no datos ficticios.

## Orden propuesto

1. Fase 4a completa (Test Explorer real + excepciones estáticas + resolución de trazas), con su script de pruebas.
2. Con autorización: instalar/obtener el adaptador DAP, sesión mínima (breakpoint, pila, variables) contra un proceso de prueba.
3. Fase 5 sobre la misma convención de sesiones.
