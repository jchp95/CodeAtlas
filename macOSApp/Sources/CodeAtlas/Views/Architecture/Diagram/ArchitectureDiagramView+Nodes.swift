import SwiftUI
import AppKit

extension ArchitectureDiagramView {
func diagramBlock(for node: DiagramNodeLayout) -> some View {
        let binding = runtimeBinding(for: node.project)
        return DiagramProjectBlock(
            project: node.project,
            displayName: node.displayName,
            layersCount: node.members.count,
            repositoryLabel: node.project.repositoryRoot.map { WorkspaceSnapshotMerger.label(for: URL(fileURLWithPath: $0, isDirectory: true)) },
            state: node.state,
            runtimeService: binding?.service,
            selected: node.members.contains(where: { $0.id == selectedProjectID }),
            isBusy: binding.map { busyServiceKey == "\($0.profileID)/\($0.service.id)" } ?? false,
            selectAction: {
                if node.members.contains(where: { $0.id == selectedProjectID }) {
                    selectedProjectID = nil
                    selectedLayerProjectID = nil
                    runtimeMessage = nil
                } else {
                    selectedProjectID = node.project.id
                    selectedLayerProjectID = node.project.id
                    runtimeMessage = nil
                }
            },
            startAction: {
                guard let binding else { return }
                Task { await performRuntimeAction(["start", binding.profileID, binding.service.id], key: "\(binding.profileID)/\(binding.service.id)") }
            },
            stopAction: {
                guard let binding else { return }
                Task { await performRuntimeAction(["stop", binding.profileID, binding.service.id], key: "\(binding.profileID)/\(binding.service.id)") }
            },
            openOperationsAction: onOpenOperations
        )
        .frame(width: ArchitectureDiagramLayout.nodeWidth, height: ArchitectureDiagramLayout.nodeHeight)
        .position(x: node.frame.midX, y: node.frame.midY)
    }

    @ViewBuilder
    func selectedGroupInspector(layout: ArchitectureDiagramLayout) -> some View {
        if let selectedID = selectedProjectID,
           let node = layout.nodes.first(where: { $0.members.contains(where: { $0.id == selectedID }) }) {
            let layerID = selectedLayerProjectID.flatMap { id in node.members.first(where: { $0.id == id })?.id }
                ?? (node.members.first(where: { $0.id == selectedID })?.id ?? node.project.id)
            let activeLayer = node.members.first(where: { $0.id == layerID }) ?? node.project
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(node.displayName).font(.title3.bold())
                        Text(node.members.count > 1 ? "Servicio compuesto · selecciona una capa para inspeccionar su código y dependencias." : "Servicio seleccionado · evidencia de código y dependencias.")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if node.members.count > 1 {
                        Label("\(node.members.count) capas", systemImage: "square.stack.3d.up.fill")
                            .font(.caption.weight(.semibold)).padding(.horizontal, 9).padding(.vertical, 5)
                            .background(Color.accentColor.opacity(0.12), in: Capsule())
                    }
                }
                if node.members.count > 1 {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 8) {
                            ForEach(node.members) { layer in
                                Button {
                                    selectedLayerProjectID = layer.id
                                    selectedProjectID = node.project.id
                                } label: {
                                    VStack(alignment: .leading, spacing: 4) {
                                        Text(layerLayerTitle(layer)).font(.caption.weight(.semibold))
                                        Text(layer.name).font(.system(size: 10, design: .monospaced)).lineLimit(1)
                                    }
                                    .padding(.horizontal, 12).padding(.vertical, 9)
                                    .background(layer.id == activeLayer.id ? Color.accentColor.opacity(0.16) : Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 9))
                                    .overlay(RoundedRectangle(cornerRadius: 9).stroke(layer.id == activeLayer.id ? Color.accentColor.opacity(0.85) : Color.secondary.opacity(0.15)))
                                }.buttonStyle(.plain)
                            }
                        }
                    }
                }
                ProjectInspector(project: activeLayer)
            }
            .padding(14)
            .atlasGlassCard(cornerRadius: 14, shadow: false)
        }
    }

    func layerLayerTitle(_ project: ProjectNode) -> String {
        let lower = project.name.lowercased()
        for name in ["api", "presentation", "application", "domain", "infrastructure", "persistence", "contracts", "abstractions", "worker", "host", "web"] where lower.hasSuffix(name) {
            return name.capitalized
        }
        return project.kind
    }
}
