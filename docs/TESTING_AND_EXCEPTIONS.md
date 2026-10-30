# Test Explorer real y exploración de excepciones (Fase 4a)

Prueba: `./scripts/testing-test.sh` (xUnit, NUnit y MSTest reales; necesita los paquetes NuGet en la caché o red al primer uso). Fixtures: `tests/fixtures/testing` (su `nuget.config` usa solo nuget.org para no tocar feeds privados).

## Comandos

```
tests discover <repo>
tests related <repo> <símbolo>
tests run <repo> --allow-execution [--project NOMBRE] [--filter EXPR] [--related SÍMBOLO] [--failed-from REPORT.json] [--no-build] [--timeout-seconds N] [--out FILE]
tests compare <informe-base.json> <informe-actual.json>
exceptions resolve <repo> --trace-file ARCHIVO|-
```
Códigos de salida: `0` informe producido, `2` uso, `3`/`6` símbolo no encontrado/ambiguo, `4` nada que ejecutar, `7` ejecución no autorizada, `130` cancelado.

## Qué es verdad y de dónde sale

- **Descubrimiento (`tests discover`)** es *estático*: métodos con atributos de prueba resueltos por Roslyn (`Fact/Theory`, `Test/TestCase`, `TestMethod/DataTestMethod`), con motivo de omisión (`Skip=`, `Ignore`) y filas de datos declaradas por atributo. No prueba que compile, que el ejecutor lo descubra ni que pase. Las filas de `MemberData`/`TestCaseSource`/`DynamicData` no se cuentan.
- **Ejecución (`tests run`)** invoca `dotnet test` por proyecto con `--logger trx` y **solo los TRX deciden los resultados**: una prueba es `passed` únicamente si un TRX lo dice. `notExecuted` es cómo el TRX informa de pruebas omitidas/ignoradas. Si un proyecto no compila o no produce TRX, su estado es `buildFailed`/`noResults`/`timedOut`/`cancelled`, no hay resultados, sus pruebas descubiertas aparecen en `notRun` y se emite `TST0002`. Los informes se guardan en `~/.config/CodeAtlas/testruns/<repo>/<fecha>/report.json` (`CODEATLAS_DATA_DIR` lo cambia).
- **Autorización**: ejecutar pruebas ejecuta código del usuario; sin `--allow-execution` solo se muestra el plan (exit 7) y no se ejecuta nada.
- **Fallos** → `TST0001` (origen `test`, validación `runtimeObserved`): mensaje, marcos de pila resueltos contra el grafo (`resolved`, `ambiguous`, `external`), ruta de llamadas, ubicación del primer marco de código propio y el comando exacto para repetirlo. Limitación declarada: el marco superior es donde afloró la excepción, no necesariamente donde está el defecto.
- **Seleccionar**: `--related SÍMBOLO` ejecuta solo las pruebas que alcanzan el símbolo por el grafo (y se niega si no hay ninguna, en lugar de ejecutar todo); `--failed-from` repite las fallidas de un informe previo; `--filter` pasa una expresión VSTest.
- **Comparar**: `tests compare` clasifica regresiones (passed→failed), corregidas, añadidas, eliminadas y cambios grandes de duración. Las duraciones son mediciones únicas: no son una conclusión de rendimiento.
- **Cobertura**: si un proyecto de pruebas referencia `coverlet.collector`, se recoge Cobertura y se informa tasa de líneas y el estado (`covered`, `uncovered`, `notInstrumented`) de cada hallazgo estático en archivos medidos. Sin coverlet no se inventa cobertura.

## Excepciones

`exceptions resolve` analiza una traza de pila pegada o capturada: separa la excepción externa de las internas, resuelve los marcos (incluye máquinas de estado `async` `<M>d__N.MoveNext`, lambdas `<M>b__N` y funciones locales), elige como origen el primer marco de la excepción más interna que pertenece a la solución, y devuelve quién llama a ese código, qué puntos de entrada lo alcanzan, qué pruebas lo cubren y qué `catch` de los archivos de la traza tragan la excepción o la relanzan por valor (`ATL1004`/`ATL1005`). Un marco con sobrecargas de igual aridad se declara `ambiguous`; marcos de framework, `external`.
Una traza no contiene valores de variables ni la operación de negocio en curso: no se inventan. Lo que sí puede capturarse en vivo está en `DEBUGGING.md`.

## Pendiente

Exploración *estática* de excepciones (qué `throw` alcanza cada punto de entrada) no está implementada: sin modelar el middleware global de errores sería casi todo falso positivo. Descubrimiento en tiempo de ejecución (`--list-tests`) y depuración de una prueba concreta desde el Test Explorer tampoco; el depurador existe y se puede lanzar sobre el proceso de pruebas.
