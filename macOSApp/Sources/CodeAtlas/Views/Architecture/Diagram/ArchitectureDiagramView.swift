import SwiftUI
import AppKit

/// Layered block diagram. Connections are rendered from repository evidence, not generated examples.
struct ArchitectureDiagramView: View {
    let snapshot: ArchitectureSnapshot
    let workspaceURL: URL?
    let onOpenOperations: () -> Void
    @Binding var selectedProjectID: String?

    @State var runtimeResponse: StackPilotResponse?
    @State var runtimeLoadError: String?
    @State var runtimeMessage: String?
    @State var isRefreshingRuntime = false
    @State var busyServiceKey: String?
    @State var selectedRuntimeLog: StackPilotLogDocument?
    @State var selectedLayerProjectID: String?
    let engine = EngineClient()

    var layout: ArchitectureDiagramLayout { ArchitectureDiagramLayout(snapshot: snapshot) }

    var body: some View {
        let diagram = layout
        VStack(alignment: .leading, spacing: 10) {
            runtimeSummaryBar

            ScrollView([.horizontal, .vertical]) {
                ZStack(alignment: .topLeading) {
                    Canvas { context, _ in
                        drawLaneBackgrounds(context: &context, layout: diagram)
                        drawEdges(context: &context, layout: diagram)
                    }
                    .frame(width: diagram.width, height: diagram.height)

                    ForEach(diagram.nodes) { node in
                        diagramBlock(for: node)
                    }
                }
                .frame(width: diagram.width, height: diagram.height)
            }
            .frame(minHeight: 430, maxHeight: 650)
            .background {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(Color(nsColor: .textBackgroundColor).opacity(0.30))
                    .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.ultraThinMaterial).opacity(0.36))
            }
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(Color.primary.opacity(0.09), lineWidth: 0.8))
            .shadow(color: Color.black.opacity(0.08), radius: 16, x: 0, y: 6)

            HStack(spacing: 14) {
                legend(color: .blue, label: "HTTP")
                legend(color: .purple, label: "Evento / mensajería")
                legend(color: .cyan, label: "Import TypeScript")
                legend(color: .secondary, label: "Referencia")
                legend(color: .orange, label: "Infraestructura")
                Spacer(minLength: 4)
                legend(color: .green, label: "Añadido")
                legend(color: .orange, label: "Modificado")
                legend(color: .red, label: "Eliminado")
            }
            .font(.caption2)
            .foregroundStyle(.secondary)

            selectedGroupInspector(layout: diagram)

            Text("Las columnas representan responsabilidades detectadas. Las capas Clean Architecture se agrupan en un único bloque desplegable; la infraestructura como RabbitMQ, MinIO y Redis aparece como dependencia. Las conexiones estáticas no demuestran tráfico en ejecución.")
                .font(.caption)
                .foregroundStyle(.secondary)

            selectedRuntimeOperations
        }
        .task(id: "\(snapshot.repositoryPath)|\(snapshot.scannedAt)") {
            await refreshRuntimeStatus()
        }
        .sheet(item: $selectedRuntimeLog) { document in
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text(document.title).font(.headline)
                    Spacer()
                    Button("Copiar") { NSPasteboard.general.setString(document.content, forType: .string) }
                    Button("Cerrar") { selectedRuntimeLog = nil }
                }
                Divider()
                ScrollView {
                    Text(document.content.isEmpty ? "No hay salida de logs." : document.content)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(12)
                }
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            }
            .padding(18)
            .frame(minWidth: 680, minHeight: 440)
        }
        .alert("Operaciones locales", isPresented: Binding(
            get: { runtimeLoadError != nil },
            set: { if !$0 { runtimeLoadError = nil } }
        )) {
            Button("Aceptar", role: .cancel) { runtimeLoadError = nil }
        } message: {
            Text(runtimeLoadError ?? "Error desconocido")
        }
    }
}
