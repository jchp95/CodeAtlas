# Endpoints HTTP y pruebas de API

## Objetivo

El menú **Endpoints HTTP** reúne en una sola vista los endpoints que se pueden inferir del código, los archivos de pruebas `.http` / `.rest` y las llamadas HTTP salientes que podrían enlazar dos microservicios. Cada elemento se agrupa por repositorio Git del workspace seleccionado.

El catálogo se calcula bajo demanda. El contenido de cuerpos de petición, cabeceras y variables no se incorpora a los snapshots de arquitectura.

## Fuentes de endpoints

Actualmente el analizador busca:

- ASP.NET Core Controllers con atributos `Route` y `HttpGet` / `HttpPost` / equivalentes.
- Minimal APIs con `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete`, `MapMethods` y rutas literales; se reconoce un prefijo sencillo declarado por `MapGroup`.
- Next.js App Router (`app/api/**/route.*`) y Pages API (`pages/api/**`).

No es un resolvedor completo de routing. Rutas construidas por funciones helper, convenciones de librerías, source generators, metadatos de ejecución o configuración externa pueden no detectarse.

Cuando un endpoint tiene un tipo de cuerpo identificable y un tipo de fuente no ambiguo, CodeAtlas genera una plantilla JSON a partir de propiedades/campos del tipo. Los valores de ejemplo son sintéticos. La plantilla no garantiza que cumpla validaciones, reglas de negocio, autorizaciones ni todos los requisitos del servidor. Si el tipo es ambiguo, el esquema se deja vacío en lugar de inventarlo.

## Archivos `.http` / `.rest`

El parser admite el subconjunto usado habitualmente en Rider / IntelliJ HTTP Client y VS Code REST Client:

```http
@baseUrl = http://localhost:5267

### Health
GET {{baseUrl}}/api/health
Accept: application/json

### Register cargo
# @name registerCargo
POST {{baseUrl}}/api/cargo
Content-Type: application/json
Accept: application/json

{
  "cargoId": "CARGO-TEST-001"
}
```

Incluye separadores `###`, método + URL, cabeceras, cuerpo, variables `@name = value` y archivos de entorno JSON. El parser no ejecuta scripts de pre-request, no admite todas las variables dinámicas integradas de Rider/REST Client y no resuelve expresiones encadenadas desde respuestas anteriores.

### Entornos y precedencia

Se leen estos archivos cuando existen:

- `http-client.env.json`
- `http-client.private.env.json`

La configuración de los directorios del workspace proporciona valores por defecto. Al avanzar a la carpeta del archivo `.http`, el valor más cercano reemplaza el del padre. Dentro de cada carpeta se lee primero el archivo público y luego el privado, por lo que el privado puede sobrescribir al público. La sección `$shared` se aplica como valor predeterminado cuando una variable no fue definida en el entorno seleccionado. Las variables declaradas en el propio archivo `.http` tienen prioridad para esa petición.

Revisa `.gitignore` y no commits credenciales reales ni tokens. CodeAtlas no copia las variables al snapshot, pero no cambia el estado de Git de tus archivos de entorno.

## Asociación de pruebas con endpoints

La asociación compara método HTTP y ruta normalizada, incluyendo segmentos de parámetros como `{id}`. Para evitar cruces innecesarios, aplica esta prioridad:

1. Endpoint del proyecto que contiene el archivo de prueba.
2. Endpoint del mismo repositorio Git.
3. Coincidencias en otros proyectos del workspace.

El catálogo puede mostrar varias coincidencias si la arquitectura contiene rutas repetidas. Si no se encuentra endpoint, la petición sigue disponible para ejecutar/editable; no necesita estar asociada para probarla.

## Ejecutar una petición

1. Selecciona el workspace en CodeAtlas.
2. Abre **Endpoints HTTP**.
3. Selecciona un archivo `.http` existente, o un endpoint descubierto.
4. Revisa método, URL, cabeceras, cuerpo y entorno seleccionado.
5. Pulsa el botón de ejecución y revisa código de estado, cabeceras, duración y cuerpo.

Los cambios hechos en el editor de una petición `.http` son overrides temporales; no se escribe sobre el archivo fuente. Para un endpoint que no tenía una prueba, **Crear .http** permite guardar una petición reutilizable en un archivo nuevo.

Las peticiones se ejecutan solo cuando el usuario pulsa ejecutar. El runner exige URL absoluta `http://` o `https://`, tiene un timeout de 30 segundos y limita el cuerpo de respuesta capturado a 512 KiB. `POST`, `PUT`, `PATCH` y `DELETE` pueden modificar datos. Usa un entorno de desarrollo seguro y revisa el host antes de cada ejecución.

## Llamadas HTTP salientes entre servicios

La vista identifica llamadas C# habituales (`GetAsync`, `PostAsync`, `SendAsync`, variantes JSON comunes) y llamadas TypeScript/JavaScript simples (`fetch`, métodos básicos de `axios` y algunos clientes con nombre convencional) cuando método y ruta se pueden extraer del código fuente. Si otro proyecto tiene un endpoint con el mismo método y ruta, se muestra como destino candidato.

La asociación se marca como inferida/ambigua/no resuelta. El análisis estático no prueba que ese código se ejecute, no determina necesariamente el valor final de `HttpClient.BaseAddress`, ni contempla con exactitud API gateways, DNS, service discovery, reescrituras de proxy, interceptores o rutas dinámicas. No equivale a observabilidad de tráfico en ejecución.

## CLI y pruebas

Generar el catálogo JSON sin abrir la aplicación:

```bash
dotnet run --project engine/CodeAtlas.Engine/CodeAtlas.Engine.csproj -- http-catalog /ruta/al/workspace
```

Validar descubrimiento y asociación (requiere .NET 10):

```bash
./scripts/http-catalog-test.sh
```

Probar ejecución de una petición contra un servidor HTTP local efímero (requiere .NET 10 y Node.js):

```bash
./scripts/http-request-execution-test.sh
```

El ejecutor es una herramienta de desarrollo local, no un escáner de seguridad ni un reemplazo de pruebas contractuales/OpenAPI. Los resultados HTTP se registran en el panel de la sesión y no se persisten como snapshots de arquitectura.
