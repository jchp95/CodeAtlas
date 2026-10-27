import SwiftUI
import AppKit

struct DiagramProjectBlock: View {
    let project: ProjectNode
    let displayName: String
    let layersCount: Int
    let repositoryLabel: String?
    let state: DiagramNodeState
    let runtimeService: StackPilotServiceStatus?
    let selected: Bool
    let isBusy: Bool
    let selectAction: () -> Void
    let startAction: () -> Void
    let stopAction: () -> Void
    let openOperationsAction: () -> Void

    var body: some View {
        Button(action: selectAction) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 7) {
                    Image(systemName: project.kind == "Infrastructure" ? "server.rack" : (project.kind == "Dotnet" ? "curlybraces" : "globe.americas.fill"))
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(project.kind == "Infrastructure" ? Color.orange : (project.kind == "Dotnet" ? Color.blue : Color.purple))
                    Text(project.kind == "Dotnet" ? ".NET" : project.kind)
                        .font(.system(size: 10, weight: .bold, design: .rounded))
                        .foregroundStyle(.secondary)
                    Spacer(minLength: 1)
                    if state != .normal {
                        Text(state.label)
                            .font(.system(size: 8, weight: .bold, design: .rounded))
                            .padding(.horizontal, 6).padding(.vertical, 3)
                            .background(state.color.opacity(0.14), in: Capsule())
                            .foregroundStyle(state.color)
                    }
                }
                HStack(spacing: 5) {
                    Text(displayName)
                        .font(.system(size: 13, weight: .semibold, design: .rounded))
                    if layersCount > 1 {
                        Text("\(layersCount) capas")
                            .font(.system(size: 8, weight: .bold, design: .rounded))
                            .padding(.horizontal, 5).padding(.vertical, 3)
                            .background(Color.accentColor.opacity(0.12), in: Capsule())
                    }
                }
                .lineLimit(1).truncationMode(.middle)
                .foregroundStyle(.primary)
                if let repositoryLabel, !repositoryLabel.isEmpty {
                    Text(repositoryLabel).font(.system(size: 8, weight: .medium)).foregroundStyle(.tertiary).lineLimit(1)
                }
                Text(project.path)
                    .font(.system(size: 9, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                HStack(spacing: 10) {
                    Label("\(project.endpoints.count)", systemImage: "point.3.connected.trianglepath.dotted")
                    Label("\(project.contractsPublished.count + project.contractsConsumed.count)", systemImage: "arrow.left.arrow.right")
                    if let framework = project.framework { Text(framework).lineLimit(1) }
                }
                .font(.system(size: 9, weight: .medium))
                .foregroundStyle(.secondary)

                if let runtimeService {
                    HStack(spacing: 5) {
                        Circle().fill(runtimeColor(runtimeService.status)).frame(width: 6, height: 6)
                        Text(runtimeLabel(runtimeService.status)).font(.system(size: 8, weight: .bold, design: .rounded))
                        Spacer(minLength: 0)
                        if let pid = runtimeService.pid { Text("PID \(pid)").font(.system(size: 8, design: .monospaced)) }
                        if isBusy { ProgressView().controlSize(.mini) }
                    }
                    .foregroundStyle(runtimeColor(runtimeService.status))
                }
            }
            .padding(11)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            .background {
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .fill(.regularMaterial)
                    .overlay {
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .fill(Color(nsColor: .controlBackgroundColor).opacity(state == .removed ? 0.30 : 0.18))
                    }
            }
            .overlay {
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .strokeBorder(selected ? Color.accentColor : state.color.opacity(state == .normal ? 0.30 : 0.95), lineWidth: selected ? 2.4 : state == .normal ? 0.9 : 2)
            }
            .shadow(color: Color.black.opacity(selected ? 0.16 : 0.055), radius: selected ? 12 : 5, x: 0, y: selected ? 5 : 2)
            .opacity(state == .removed ? 0.72 : 1)
        }
        .buttonStyle(.plain)
        .help("\(project.path)\n\(state.label)")
        .contextMenu {
            if let runtimeService {
                if runtimeService.enabled && runtimeService.status != "running" {
                    Button("Iniciar servicio", systemImage: "play.fill", action: startAction)
                        .disabled(isBusy || runtimeService.status == "unavailable")
                }
                if runtimeService.status == "running" {
                    Button("Detener servicio", systemImage: "stop.fill", action: stopAction)
                        .disabled(isBusy)
                }
                Divider()
                Button("Abrir controles de StackPilot…", systemImage: "server.rack", action: openOperationsAction)
                if !runtimeService.enabled {
                    Text("Plantilla desactivada: habilítala tras revisar el comando.")
                }
            } else {
                Button("Configurar operaciones locales…", systemImage: "server.rack", action: openOperationsAction)
            }
        }
    }

    func runtimeColor(_ status: String) -> Color {
        switch status {
        case "running": return .green
        case "unavailable": return .red
        case "disabled": return .secondary
        default: return .orange
        }
    }

    func runtimeLabel(_ status: String) -> String {
        switch status {
        case "running": return "EN EJECUCIÓN"
        case "unavailable": return "NO DISPONIBLE"
        case "disabled": return "DESACTIVADO"
        default: return "DETENIDO"
        }
    }
}

enum DiagramNodeState: Equatable {
    case normal, added, changed, removed
    var label: String {
        switch self {
        case .normal: return ""
        case .added: return "AÑADIDO"
        case .changed: return "MODIFICADO"
        case .removed: return "ELIMINADO"
        }
    }
    var color: Color {
        switch self {
        case .normal: return .secondary
        case .added: return .green
        case .changed: return .orange
        case .removed: return .red
        }
    }
}

enum DiagramEdgeState: Equatable { case normal, added, removed }

struct DiagramNodeLayout: Identifiable {
    let project: ProjectNode
    let members: [ProjectNode]
    let displayName: String
    let frame: CGRect
    let lane: Int
    let state: DiagramNodeState
    var id: String { project.id }
}

struct DiagramEdgeLayout: Identifiable {
    let relation: ArchitectureRelation
    let state: DiagramEdgeState
    let sourceBlockID: String
    let targetBlockID: String
    var id: String { "\(relation.id):\(state):\(sourceBlockID):\(targetBlockID)" }
}

struct EdgeGeometry {
    let start: CGPoint
    let end: CGPoint
    let control1: CGPoint
    let control2: CGPoint
    let angle: CGFloat
    let labelPoint: CGPoint
}
