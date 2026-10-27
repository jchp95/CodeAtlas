# Language Services y LSP

## Objetivo

CodeAtlas utiliza Language Server Protocol (LSP) para añadir semántica real al editor donde un servidor externo está instalado. El outline basado en reglas sigue siendo una ayuda rápida, pero no se presenta como sustituto de compiladores ni de símbolos semánticos.

## Servidores detectados

CodeAtlas busca estos ejecutables en `PATH`. No instala servidores, no ejecuta gestores de paquetes automáticamente y no evalúa comandos de shell procedentes del repositorio.

| Lenguaje / archivos | Ejecutable esperado | Inicio |
|---|---|---|
| C# (`.cs`) | `csharp-ls` | sin argumentos |
| TypeScript (`.ts`, `.tsx`) | `typescript-language-server` | `--stdio` |
| JavaScript (`.js`, `.jsx`, `.mjs`, `.cjs`) | `typescript-language-server` | `--stdio` |
| Swift (`.swift`) | `sourcekit-lsp` | sin argumentos |
| Python (`.py`) | `pylsp` | sin argumentos |
| JSON (`.json`, `.jsonc`) | `vscode-json-language-server` | `--stdio` |

Los identificadores LSP para TSX y JSX se resuelven como `typescriptreact` y `javascriptreact`.

## Funcionalidades implementadas

- Inicialización y finalización JSON-RPC por `stdin`/`stdout`.
- Framing por bytes con `Content-Length`, soporte para lecturas fragmentadas y múltiples mensajes por lectura.
- Límite de 16 MiB por payload y timeout por petición.
- `textDocument/didOpen`, `textDocument/didChange` con sincronización completa y `textDocument/didClose` al cambiar de documento.
- Diagnósticos `textDocument/publishDiagnostics` navegables por línea en el inspector derecho.
- Completion bajo demanda desde el botón **IntelliSense**. Las propuestas se muestran para revisión y solo se insertan al seleccionar una.
- `textDocument/definition` desde **Definición**.
- Respuestas seguras a solicitudes frecuentes del servidor (`workspace/configuration`, `workspace/workspaceFolders`, registro de capacidades y creación de progreso).

## Seguridad y privacidad

- El proceso se ejecuta directamente mediante `Process`, con una ruta de ejecutable resuelta y argumentos separados; no se invoca `/bin/sh`.
- CodeAtlas no instala dependencias ni ejecuta las herramientas de build del proyecto para activar LSP.
- `workspace/applyEdit` se rechaza: un servidor no puede modificar archivos silenciosamente.
- `workspace/executeCommand` no se ejecuta: el cliente responde sin lanzar el comando y escribe una advertencia en el registro.
- Los servidores locales reciben el texto del documento abierto a través de stdio. Las solicitudes al modelo IA son independientes y siguen utilizando el proveedor seleccionado por el usuario.
- El registro está acotado para no crecer indefinidamente.

## Estados y diagnóstico

El botón de estado distingue `starting`, `ready`, `failed` y `unavailable`. Si un ejecutable no está en `PATH`, el editor informa del nombre esperado y deja disponibles el resaltado sintáctico y el outline heurístico.

## Limitaciones actuales

Esta fase habilita la base para semantic completion y navegación, pero aún no implementa:

- completado automático al escribir o al pulsar `.`;
- `textDocument/references`, rename o code actions;
- semantic tokens del language server y diagnósticos multiarchivo completos en la UI;
- selección/configuración de ruta de ejecutable desde Preferencias;
- administración de versiones o instalación de servidores;
- pruebas de runtime con cada servidor real (se cubre el protocolo con un servidor LSP falso y se debe verificar cada proveedor en macOS).

## Validación

Ejecutar `./scripts/language-server-test.sh`. La prueba compila el cliente Foundation con `swiftc`, lanza un servidor LSP falso sobre stdio y verifica handshake, peticiones del servidor, diagnósticos, completion, definition y sincronización del documento. La suite obligatoria queda incluida en `verify-before-launch.sh`.
