# Visual system and navigation — 0.11.0

CodeAtlas uses a shared adaptive visual system rather than styling each screen independently.

## Visual principles

- **Adaptive appearance:** `appearanceMode` supports `system`, `light` and `dark`. The app follows macOS by default; it no longer forces dark appearance.
- **Blurred workspace backdrop:** `AtlasBackdrop` combines a system window background, low-opacity blue/purple light fields, a gradient falloff and an ultra-thin material layer. Accent opacity is lowered in light mode to avoid washed-out surfaces.
- **Glass surfaces:** `atlasGlassCard` uses regular material, a mode-aware tint, a restrained border and subtle shadow. Selected states use an accent border instead of stronger fills.
- **Readable editors:** request bodies, headers and source-like text entry areas use a solid adaptive text background, not translucent material. The blur is applied to their surrounding surfaces, not over their text.
- **Contrast and hierarchy:** primary text uses semantic primary styles; supporting metadata uses secondary styles; status colors are reserved for meaningful states. Cards have consistent continuous corners, spacing and line borders.
- **Persistent frame:** the top navigation bar identifies the current section and workspace; the bottom status bar shows scanning state, root count, project and relationship totals, and auto-refresh state.

## Navigation map

- **Inicio:** `Resumen`, `Cambios recientes`.
- **Explorar / Arquitectura:** `Mapa de arquitectura`, `Relaciones y dependencias`, `Flujos de negocio`, `Análisis y evidencias`.
- **Desarrollo y pruebas:** `Endpoints HTTP`, including `.http` / `.rest` requests and outgoing call traces.
- **Entorno local:** `Servicios locales`, `Perfiles de ejecución`, `Diagnóstico`.
- **Inteligencia:** `Asistente IA`, `Configuración de IA`.
- **Workspace:** selected roots and detected projects remain visible in the sidebar.

The summary intentionally contains only workspace metrics, repository state, recent local changes and shortcut cards. The architecture map, relations, business flows, evidence/analysis and operations live in focused views.

## Validation

Run `./scripts/ui-structure-test.sh` to check the navigation groups, appearance settings, shared design primitives, status bar and Swift source syntax. This is a source/syntax check, not a replacement for building and visually reviewing the app on macOS. Verify both light and dark appearance on the target Mac before shipping a signed build.
