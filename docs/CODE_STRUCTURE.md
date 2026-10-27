# CodeAtlas + StackPilot — estructura de código

La versión 0.13.1 reorganiza el código por funcionalidades y responsabilidades. El cambio es estructural: no busca redefinir reglas de análisis, datos de snapshot, criterios de detección ni operaciones de StackPilot.

## Principios aplicados

- **Vertical slice por funcionalidad:** el código que implementa una capacidad se encuentra junto a sus modelos, análisis y pruebas relacionados.
- **Responsabilidad única:** la entrada CLI enruta comandos; el escáner detecta arquitectura; el comparador calcula deltas; la capa HTTP parsea, construye catálogos y ejecuta peticiones; StackPilot gestiona perfiles y ciclo de vida de procesos.
- **Separación de UI, servicios y modelos:** las vistas SwiftUI no contienen el cliente de proceso .NET ni el modelo de dominio de snapshots.
- **Reutilización sin duplicación:** clases grandes que necesitaban compartir estado fueron divididas en `partial` con archivos por tarea, conservando un único tipo y sus miembros privados donde corresponde.
- **Sin cambios de contrato:** los nombres JSON, los argumentos CLI, los identificadores de snapshots, las reglas del analizador y la presentación existente permanecen como estaban, salvo los modificadores de acceso necesarios para mover extensiones SwiftUI a otros archivos.

## Motor .NET

```text
engine/CodeAtlas.Engine/
├── Program.cs                         # Entrada CLI y despacho de comandos
├── Shared/
│   ├── Paths/PathTools.cs             # Primitivas comunes de rutas
│   └── Serialization/JsonDefaults.cs  # Configuración JSON común
└── Features/
    ├── Architecture/
    │   ├── Scanning/                  # Orquestación de escaneo y detección por responsabilidad
    │   ├── Comparison/                 # Diferencias entre snapshots
    │   └── Models/                    # Proyectos, relaciones y deltas
    ├── BusinessFlows/
    │   ├── Analysis/                  # Descubrimiento de entradas y trazado de llamadas
    │   └── Models/                    # Flujos y cambios de flujo
    ├── HttpTesting/
    │   ├── Catalog/                   # Endpoints y llamadas salientes detectadas
    │   ├── Parsing/                   # Lectura de archivos .http / .rest
    │   ├── Environments/              # Variables y perfiles de entorno
    │   ├── Execution/                 # Ejecución y captura de respuestas HTTP
    │   └── Models/                    # Contratos del catálogo y resultados
    ├── StackPilot/
    │   ├── Models/                    # Configuración, perfiles y servicios
    │   ├── Runtime/                   # Persistencia del estado de procesos
    │   └── StackPilotCli.*.cs          # Descubrimiento, ciclo de vida, Compose y configuración
    └── Workspace/
        ├── GitWorkspaceInspector.cs
        └── Models/GitModels.cs
```

`RepositoryScanner`, `HttpTestingCatalogBuilder`, `StackPilotCli` y `BusinessFlowAnalyzer` son tipos parciales con una única identidad en tiempo de compilación. Sus archivos agrupan responsabilidades; no son servicios paralelos ni duplicados.

## Aplicación macOS

```text
macOSApp/Sources/CodeAtlas/
├── CodeAtlasApp.swift                 # Ciclo de vida y tema global
├── ContentView.swift                  # Shell, navegación y estado de pantalla
├── ContentView+*.swift                # Navegación, resumen, secciones y workspace
├── Infrastructure/EngineClient.swift  # Proceso hijo y protocolo con el motor
├── Features/
│   ├── AI/                             # Proveedor, servicio y contexto acotado
│   ├── Architecture/                  # Modelos, agrupación y enlace con runtime
│   ├── StackPilot/Models/             # Modelos de operaciones locales
│   └── Workspace/                     # Unión de snapshots multi-repositorio
└── Views/
    ├── AI/
    ├── Architecture/                  # Componentes, diagrama, layout y rendering
    ├── BusinessFlows/                   # Explorador y componentes de flujos separados
    │   └── Components/                  # Tarjetas, filas, métricas y confianza
    ├── DesignSystem/
    ├── HTTPTesting/                   # Navegación, detalles y ejecución
    └── StackPilot/                    # Presentación, perfiles y acciones
```

## Dónde agregar código nuevo

- Nueva detección de arquitectura: `Features/Architecture/Scanning/`; modelos del snapshot: `Features/Architecture/Models/`.
- Nuevas reglas de trazado de flujos: `Features/BusinessFlows/Analysis/`; evitar incorporarlas a la entrada CLI.
- Parseo o ejecución de `.http`: usar la parte correspondiente de `Features/HttpTesting/`; no mezclar parseo, entorno y transporte HTTP.
- Nuevo comando o acción de StackPilot: ubicarlo en el slice correspondiente; configuración y estado de runtime permanecen separados.
- Nuevo elemento visual: crear un subview pequeño bajo `Views/<funcionalidad>/`; si la vista necesita varias secciones, dividirlas en extensiones de presentación/acciones y mantener las propiedades de estado en el tipo raíz.
- Código reutilizable entre funcionalidades debe ser pequeño, sin dependencias del UI, y permanecer en `Shared/` o `Infrastructure/` solo si cruza límites de dominio de verdad.

## Verificación

Antes de lanzar la aplicación en macOS, `scripts/verify-before-launch.sh` ejecuta la compilación .NET, los tests de integración/regresión, las comprobaciones de estructura y la compilación SwiftUI. `scripts/vertical-slice-structure-test.sh` valida que no reaparezcan los archivos monolíticos iniciales y que el tamaño de cada fuente se mantenga por debajo del umbral de 30 KB. El límite sirve como alarma de revisión, no como objetivo para fragmentar cada función mecánicamente.


### Trazado de llamadas locales sin enlace semántico

El analizador de flujos utiliza primero los símbolos de Roslyn. Si una referencia incompleta impide resolver una llamada entre tipos locales, puede usar un fallback conservador basado en el tipo declarado del receptor y una firma fuente única. Estos pasos se marcan como `inferred`; no se crea un enlace cuando la búsqueda es ambigua.
