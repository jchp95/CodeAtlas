import SwiftUI
import AppKit

extension ContentView {
func sectionTitle(_ section: String) -> String {
        switch section {
        case "Resumen": return "Resumen del workspace"
        case "Arquitectura": return "Mapa de arquitectura"
        case "Relaciones": return "Relaciones y dependencias"
        case "Flujos": return "Flujos de negocio"
        case "Análisis": return "Análisis y evidencias"
        case "Cambios": return "Cambios recientes"
        case "Git": return "Control Git y GitHub"
        case "Código": return "Explorador de código"
        case "Endpoints HTTP": return "Endpoints HTTP"
        case "Servicios": return "Servicios locales"
        case "Perfiles": return "Perfiles de ejecución"
        case "Diagnóstico": return "Diagnóstico del entorno"
        case "Asistente IA": return "Asistente IA"
        case "Configuración IA": return "Configuración de IA"
        default: return "CodeAtlas + StackPilot"
        }
    }

    func sectionIcon(_ section: String) -> String {
        switch section {
        case "Resumen": return "rectangle.grid.2x2.fill"
        case "Arquitectura": return "square.3.layers.3d"
        case "Relaciones": return "arrow.left.arrow.right"
        case "Flujos": return "point.topleft.down.curvedto.point.bottomright.up"
        case "Análisis": return "viewfinder"
        case "Cambios": return "arrow.triangle.2.circlepath"
        case "Git": return "arrow.triangle.branch"
        case "Código": return "chevron.left.forwardslash.chevron.right"
        case "Endpoints HTTP": return "network"
        case "Servicios": return "server.rack"
        case "Perfiles": return "square.stack.3d.up"
        case "Diagnóstico": return "stethoscope"
        case "Asistente IA": return "brain.head.profile"
        case "Configuración IA": return "slider.horizontal.3"
        default: return "square.grid.2x2"
        }
    }

    @ViewBuilder
    func selectedContent(_ snapshot: ArchitectureSnapshot) -> some View {
        switch selectedSection {
        case "Resumen":
            summaryPage(snapshot)
        case "Git":
            GitWorkbenchView(workspaceURLs: repositoryURLs)
        case "Cambios":
            page {
                AtlasPageHeading(eyebrow: "Git · Snapshot · Impacto", title: "Cambios recientes", detail: "Compara el estado actual con el último snapshot e identifica repositorios y flujos afectados.", systemImage: "arrow.triangle.2.circlepath")
                gitRepositoriesSection(snapshot)
                architectureDeltaSection(snapshot)
                changesSection(snapshot)
            }
        case "Arquitectura":
            page {
                AtlasPageHeading(eyebrow: "Mapa del sistema", title: "Arquitectura", detail: "Explora servicios como bloques. Selecciona un bloque para inspeccionar sus capas, dependencias y operaciones locales.", systemImage: "square.3.layers.3d")
                architectureSection(snapshot)
            }
        case "Relaciones":
            page {
                AtlasPageHeading(eyebrow: "Conectividad", title: "Relaciones y dependencias", detail: "Inspecciona conexiones entre APIs, proyectos, contratos de eventos e infraestructura con su evidencia y nivel de confianza.", systemImage: "arrow.left.arrow.right")
                relationsSection(snapshot)
                quickNavigationCard(title: "Seguir un flujo de negocio", detail: "Abrir recorridos desde endpoints y handlers hasta contratos y consumidores.", icon: "point.topleft.down.curvedto.point.bottomright.up", destination: "Flujos", tint: .purple)
            }
        case "Análisis":
            analysisPage(snapshot)
        case "Servicios":
            StackPilotDashboard(section: selectedSection, workspaceURLs: repositoryURLs)
        case "Perfiles":
            StackPilotProfilesView(workspaceURLs: repositoryURLs)
        case "Diagnóstico":
            StackPilotDiagnosticsView(workspaceURLs: repositoryURLs)
        case "Asistente IA":
            AIAssistantView(service: aiService, snapshot: snapshot, onOpenSettings: { selectedSection = "Configuración IA"; intelligenceMenuExpanded = true })
        case "Configuración IA":
            AISettingsView(service: aiService)
        case "Flujos":
            BusinessFlowExplorerView(snapshot: snapshot, onSelectProject: { projectID in
                selectedProjectID = projectID
                selectedSection = "Arquitectura"
                architectureMenuExpanded = true
            })
            .id("\(snapshot.repositoryPath)|\(snapshot.scannedAt)")
            .background(.clear)
        case "Código":
            CodeExplorerView(workspaceURLs: repositoryURLs, aiService: aiService,
                             requestedFilePath: $codeFilePath,
                             requestedRootPath: $codeRootPath,
                             requestedLine: $codeLine)
        case "Endpoints HTTP":
            EndpointHTTPExplorerView(
                repositoryURL: repositoryURL,
                catalog: httpCatalog,
                isLoading: isLoadingHTTPCatalog,
                onReload: { Task { await refreshHTTPCatalog() } },
                onDirectRun: { method, url, headersText, body in
                    try await engine.executeHTTPRequest(method: method, url: url, headersText: headersText, body: body)
                },
                onFileRun: { request, environment, method, url, headersText, body in
                    guard let defaultRoot = repositoryURL else { throw EngineError.processFailed("Selecciona al menos una carpeta antes de ejecutar una petición .http.") }
                    let requestRoot = request.workspaceRoot.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? defaultRoot
                    return try await engine.executeHTTPFile(repositoryURL: requestRoot, request: request, environment: environment,
                        method: method, url: url, headersText: headersText, body: body)
                }
            )
            .task(id: repositoryURLs.map(\.path).joined(separator: "|")) { await refreshHTTPCatalog() }
        default:
            summaryPage(snapshot)
        }
    }

    func page<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                content()
            }
            .frame(maxWidth: 1500, alignment: .leading)
            .padding(.horizontal, 28)
            .padding(.vertical, 26)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .scrollIndicators(.visible)
        .background(.clear)
    }
}
