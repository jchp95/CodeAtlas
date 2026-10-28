# Ejecución de procesos y análisis del workspace

## Responsabilidades

`ProcessExecutionService` es el único propietario del ciclo de vida para las llamadas del motor y Git que no requieren salida interactiva en streaming. Drena ambos canales simultáneamente, conserva el código de salida, limita stdout/stderr y usa un manejador de cancelación para terminar el proceso asociado a la tarea cancelada.

`EngineInvocationResolver` selecciona el ensamblado .NET compilado por la barrera de arranque. Si todavía no existe, usa `dotnet run` como fallback de checkout sin compilar. La barrera obligatoria construye el ensamblado Debug antes del arranque normal.

`ContentView+Workspace.swift` controla la identidad de cada operación. Los callbacks de progreso ignoran resultados de operaciones obsoletas; una nueva operación cancela y espera a que termine la anterior; el usuario puede cancelar desde la barra superior sin recibir un error falso.

## Concurrencia y memoria

- Leer stdout y stderr en paralelo evita bloqueos cuando uno de los pipes se llena.
- Las salidas de procesos están limitadas a 128 MiB para stdout y 4 MiB para stderr; Git puede capturar hasta 64 MiB de stdout.
- El JSON se decodifica fuera del hilo principal.
- La fusión de snapshots multi-root se ejecuta fuera del hilo principal.
- La huella de Git usa `-z` para nombres de archivos que contienen espacios o saltos de línea. Para evitar guardar un diff binario completo en memoria, calcula fingerprints de los paths modificados.
- Los análisis de varios repositorios siguen siendo secuenciales para evitar competencia de build/restore en NuGet, pero se puede cancelar el proceso actual y los repositorios posteriores no se procesan después de la cancelación.

## Pruebas

`process-execution-test.sh` verifica stdout/stderr, códigos distintos de cero, límites de salida y cancelación rápida. `engine-invocation-test.sh` comprueba la selección de Debug/Release y el fallback. `workspace-cancellation-test.sh` comprueba que todos los caminos de interfaz pasan por el coordinador cancelable. Estas pruebas no sustituyen la ejecución de `verify-before-launch.sh` en macOS con .NET 10 y Xcode.
