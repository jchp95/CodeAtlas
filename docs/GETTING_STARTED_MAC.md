# Ejecutar CodeAtlas + StackPilot en macOS

## 1. Requisitos

Comprueba que tienes las herramientas necesarias:

```bash
xcode-select -p
swift --version
dotnet --list-sdks
git --version
```

Debe aparecer un SDK `10.x` de .NET. También necesitas macOS 14 o posterior y Xcode 16 o posterior.

## 2. Descomprimir y ejecutar con verificación obligatoria

Desde Terminal, entra en la raíz de la carpeta descomprimida y ejecuta el launcher:

```bash
./scripts/launch-macos.sh
```

El launcher no inicia la aplicación hasta que se cumplan todas estas condiciones:

1. Existe el SDK de .NET 10, Swift/Xcode, Node.js y Git.
2. Compila el motor .NET.
3. Pasa `scripts/smoke-test.sh` (análisis, snapshots, workspace, HTTP y ciclo de vida de StackPilot).
4. Pasa los tests de flujo distribuido, workspace Git, agrupación Clean Architecture, compatibilidad de modelos, enlaces runtime y combinación multi-repositorio.
5. Pasa las verificaciones de estructura UI y vertical slice.
6. Compila el target nativo SwiftUI completo con `swift build`.

Si falla un test o una compilación, el proceso termina con código distinto de cero y **no lanza CodeAtlas**. No uses `swift run` directamente si quieres conservar esta garantía.

Para ejecutar la verificación sin abrir la aplicación:

```bash
./scripts/verify-before-launch.sh
```

Mantén `macOSApp/` y `engine/` en la misma estructura. El motor sigue ejecutándose como proyecto .NET durante el desarrollo; todavía no está empaquetado dentro de una aplicación `.app` independiente.

## 3. Primer análisis

1. Pulsa **Abrir repositorio** y selecciona la raíz de una solución .NET o un monorepo que contenga proyectos .NET / Next.js.
2. CodeAtlas muestra los bloques de proyecto, rutas candidatas, referencias declaradas y relaciones detectadas.
3. Selecciona un bloque para revisar endpoints, paquetes, referencias de proyecto, contratos de mensajería, destinos HTTP y la sección desplegable **Evidencia de código y DI** con símbolos y líneas de origen.
4. La primera captura se guarda en el directorio de datos de usuario de CodeAtlas. En análisis posteriores se comparan ambos snapshots.
5. Mantén **Auto** habilitado para refrescar el mapa al detectar cambios en la rama, el commit o el estado/contenido del diff de Git. La comprobación ocurre cada 15 segundos. También puedes pulsar **Actualizar**.

## 4. Qué significa una relación

- **Confirmada:** el código del proyecto contiene una referencia `ProjectReference` que coincide con otro proyecto encontrado.
- **Inferida:** el motor identifica un posible flujo por nombre de contrato productor/consumidor, una URL HTTP que coincide con el nombre de otro servicio o un puerto loopback configurado.

Una relación inferida no demuestra que la llamada ocurra en runtime. Revisa la evidencia (archivo, URL o contrato) que aparece en el panel.

## 5. Diagnóstico desde Terminal

Analiza una carpeta sin guardar snapshot:

```bash
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- scan tests/fixtures/architecture --no-persist
```

Prueba los candidatos de HTTP y mensajería con:

```bash
./scripts/smoke-test.sh
```

## 6. Endpoints HTTP y pruebas `.http`

Con un workspace seleccionado, abre **Endpoints HTTP**. La barra lateral separa por repositorio los endpoints detectados en el código, las peticiones `.http` / `.rest` existentes y las llamadas HTTP salientes. Al seleccionar un endpoint, la aplicación prepara una petición con su ruta y, cuando se puede inferir con seguridad, un ejemplo de cuerpo JSON. Pulsa **Crear .http** para guardar una prueba reutilizable.

El subconjunto compatible de archivos incluye separadores `###`, método/URL, cabeceras, cuerpo, `@variables`, `http-client.env.json` y `http-client.private.env.json`. No implementa todas las funciones de Rider / REST Client, como scripts arbitrarios, todas las variables dinámicas integradas ni el encadenamiento de respuestas entre peticiones. Las peticiones que no se asocien automáticamente a un endpoint siguen siendo ejecutables.

El ejecutor requiere URL absolutas HTTP/HTTPS, aplica un timeout de 30 segundos y limita el cuerpo de respuesta capturado a 512 KiB. Revisa primero la URL y el entorno: los métodos que escriben pueden modificar datos. No guardes credenciales reales en archivos versionados.

Valida el catálogo y la ejecución HTTP local con .NET 10:

```bash
./scripts/http-catalog-test.sh
./scripts/http-request-execution-test.sh
```

## 7. StackPilot local

1. Abre **Servicios locales**.
2. Pulsa **Crear perfil desde un repositorio…** o **Añadir workspace**.
3. Abre `profiles.json` desde la tarjeta de configuración. Las entradas generadas están desactivadas por seguridad.
4. Revisa los comandos, directorios, argumentos y variables. Añade un `healthUrl` solo si el servicio dispone realmente de ese endpoint.
5. Habilita las entradas revisadas y usa **Iniciar perfil**, o inicia cada servicio individualmente. Las dependencias configuradas se respetan al iniciar el perfil.
6. Inspecciona logs desde cada fila; para procesos locales, los logs se conservan en Application Support fuera del repositorio.

Documentación del esquema y CLI: [`STACKPILOT.md`](STACKPILOT.md).

## Limitaciones de esta versión

- El análisis semántico de Roslyn es best-effort: todavía no resuelve todos los casos de DI, referencias a proyectos no compilados, reflexión, configuración indirecta o routing personalizado.
- La observación Git usa polling cada 15 segundos. Archivos ignorados pueden requerir actualización manual.
- Las relaciones HTTP y de mensajería no son prueba de tráfico en runtime.
- StackPilot es local; no despliega a producción ni consulta la topología remota real de RabbitMQ.
- La configuración inicial es JSON editada con una aplicación externa; el editor de servicios dentro de CodeAtlas está pendiente.
- El motor y la app nativa aún deben compilarse y probarse en el Mac con sus SDK instalados.


## Servicios de lenguaje y semántica LSP

CodeAtlas detecta servidores existentes en `PATH`; no los instala ni ejecuta gestores de paquetes de forma automática. Para C# se busca `csharp-ls`, para TypeScript/JavaScript `typescript-language-server --stdio`, para Swift `sourcekit-lsp`, para Python `pylsp` y para JSON `vscode-json-language-server --stdio`. Consulta [`LANGUAGE_SERVICES.md`](LANGUAGE_SERVICES.md) para el mapa completo de funciones y limitaciones.

En **Código**, abre un archivo compatible. El indicador `LSP activo` confirma que el proceso inicializó; después puedes usar **IntelliSense**, **Definición** y el inspector **Problemas**. Cuando el servidor no esté instalado, la interfaz muestra el ejecutable que falta y mantiene disponibles el editor, el resaltado y el outline heurístico.

Para validar el protocolo independientemente del proveedor instalado:

```bash
./scripts/language-server-test.sh
```

Esta prueba inicia un servidor simulado sobre `stdin`/`stdout` y verifica el handshake, diagnósticos, completion, definition y sincronización. No sustituye la prueba de un servidor real en el Mac.


## Cancelar y validar los análisis (0.20.0)

La barra de análisis del workspace incluye **Cancelar**. Cancelar termina el proceso activo y no muestra la cancelación como un error. Una actualización nueva espera a que la operación anterior haya terminado para evitar dos procesos del motor compitiendo a la vez.

Para comprobar este comportamiento antes del lanzamiento:

```bash
./scripts/process-execution-test.sh
./scripts/engine-invocation-test.sh
./scripts/workspace-cancellation-test.sh
./scripts/verify-before-launch.sh
```

Después del build de arranque, las llamadas al motor prefieren `bin/Debug/net10.0/CodeAtlas.Engine.dll` (o Release si no existe Debug). No ejecutes la aplicación omitiendo el script de lanzamiento en el flujo normal de desarrollo.
