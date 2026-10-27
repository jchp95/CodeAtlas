import SwiftUI

struct BusinessFlowCard: View {
    let flow: BusinessFlow
    let onSelectProject: (String) -> Void
    var rootPath: String? = nil
    @State private var expanded = true

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "arrow.turn.down.right")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .frame(width: 38, height: 38)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                VStack(alignment: .leading, spacing: 5) {
                    Text(flow.name).font(.headline)
                    Button { SourceNavigator.open(flow.entryFile, root: rootPath, line: flow.entryLine) } label: {
                        Label("\(flow.projectName) · \(flow.entryFile):\(flow.entryLine)", systemImage: "arrow.up.forward.square")
                            .font(.caption.monospaced()).foregroundStyle(.secondary)
                    }.buttonStyle(.plain).help("Abrir el punto de entrada en el explorador de código")
                    Text(flow.summary).font(.callout).foregroundStyle(.secondary)
                }
                Spacer(minLength: 8)
                ConfidencePill(value: flow.confidence)
            }
            .padding(14)

            HStack(spacing: 8) {
                Button { onSelectProject(flow.projectId) } label: { Label("Ver servicio", systemImage: "square.grid.2x2") }
                    .buttonStyle(.bordered)
                Button { expanded.toggle() } label: {
                    Label(expanded ? "Ocultar pasos" : "Ver \(flow.steps.count) pasos", systemImage: expanded ? "chevron.up" : "chevron.down")
                }
                .buttonStyle(.bordered)
                Spacer()
                Text(flow.entryPoint).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(.horizontal, 14).padding(.bottom, 12)

            if expanded {
                Divider()
                VStack(spacing: 0) {
                    ForEach(flow.steps.sorted { $0.sequence < $1.sequence }) { step in
                        FlowStepRow(step: step, rootPath: rootPath)
                        if step.id != flow.steps.last?.id { Divider().padding(.leading, 58) }
                    }
                }
                .padding(.vertical, 3)
            }
        }
        .atlasGlassCard(cornerRadius: 14)
    }
}
