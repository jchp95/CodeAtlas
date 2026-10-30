# Depuración (Fase 4b)

Integra un adaptador de depuración real —**netcoredbg 3.2.0** (Samsung, licencia MIT)— por Debug Adapter Protocol. No se implementó un motor propio. Prueba: `./scripts/debugging-test.sh`.

## Instalación (hecha con autorización del usuario)

Instalado a nivel de usuario, sin `sudo` ni cambios globales: `~/.codeatlas/tools/netcoredbg/` desde `https://github.com/Samsung/netcoredbg/releases/download/3.2.0-1092/netcoredbg-osx-arm64.zip` (3 389 504 bytes, SHA-256 `f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea`; la release no publica una suma contra la que compararla). Orden de búsqueda: `CODEATLAS_NETCOREDBG`, `~/.codeatlas/tools/netcoredbg/netcoredbg`, `PATH`. Se retira borrando esa carpeta. `debug doctor` informa del estado; sin adaptador, todo comando de depuración falla con `debuggerNotInstalled` (exit 5) e instrucciones, nunca con datos simulados. `vsdbg` no se usa (su licencia lo limita a productos de Microsoft).

## Comandos

```
debug doctor
debug run <repo> --program RUTA/App.dll [--args "a b"] [--cwd DIR] --allow-execution [--stop-timeout-seconds N] [--record FILE]
debug run <repo> --attach PID --allow-attach
debug timeline <grabación.jsonl>
```
Lanzar exige `--allow-execution`; **adjuntar** a un proceso exige `--allow-attach` (pausa e inspecciona su memoria). Sin la bandera se devuelve el plan y exit 7. Al terminar el adjuntado el proceso se **desvincula, no se mata**; una sesión lanzada que se cancela (SIGTERM) mata también al depurado (exit 130).

## Protocolo de sesión (JSON por líneas)

Al arrancar emite `{"event":"ready","state":{…supports…},"graphResolution":true}`. Después, un comando por línea de stdin (`{"id":1,"cmd":"…"}`) y una respuesta por línea de stdout (`{"id":1,"ok":true,"result":…}` o `"ok":false,"error"`):

| `cmd` | Notas |
|---|---|
| `breakpoints` `{file, items:[{line, condition?, logMessage?}]}` | Ruta relativa al repositorio. Devuelve `verified` y el mensaje del adaptador (p. ej. *pendiente*); el adaptador puede **reubicar** una línea sin código y el parón lo muestra |
| `functionBreakpoints` `{names:["Ns.Tipo.Metodo"]}` | |
| `exceptions` `{filters:["all"\|"user-unhandled"], types?}` | `all` = al lanzarse, antes de capturarse; `user-unhandled` = no controladas |
| `run`, `continue`, `next`, `stepIn`, `stepOut`, `pause` | Esperan al siguiente parón o salida y lo describen |
| `stack`, `variables`, `evaluate`, `threads`, `terminate` | |

Un parón (`state: "stopped"`) incluye: motivo, marcos de pila con archivo/línea y `nodeId` del **grafo de llamadas** (`resolved`/`ambiguous`/`external`), variables locales (40 por ámbito) y, para excepciones, tipo, mensaje, traza y el análisis de `ATL4001` (origen, llamadores, pruebas relacionadas, flujos). Salida del depurado acotada (200 líneas).

## Seguridad y datos

- Las variables cuyo **nombre** sugiere secretos (`password`, `token`, `secret`, `apikey`, `connectionstring`, `credential`, `bearer`…) se devuelven como `<redacted>`; también al evaluar expresiones con esos nombres. Es por nombre: un secreto en una variable con otro nombre no se detecta.
- `--record` guarda solo lo que la sesión capturó (parones, excepciones, salida) ya redactado; `debug timeline` lo resume. No se reconstruyen valores que no se inspeccionaron ni historia anterior: **no hay IntelliTrace**, solo registro de lo observado.
- Rutas con enlaces simbólicos (`/var` → `/private/var`) se resuelven: el adaptador enlaza breakpoints con la ruta guardada en el PDB.

## Limitaciones (de netcoredbg o de esta fase)

- Inspección de tareas asíncronas: no la ofrece el adaptador; solo se ven marcos de máquinas de estado `async`.
- Breakpoints con `hitCondition` y logpoints: el adaptador no declara soporte.
- Depuración remota y de contenedores: no implementada (sí lanzamiento y adjuntado local por pid).
- Los PDB deben ser portables y el código compilado en Debug para que los valores no estén optimizados.
- Fuera de la sesión (UI de la app): pendiente; el build SwiftUI sigue sin poder verificarse en este equipo.
