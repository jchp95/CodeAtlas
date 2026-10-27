import SwiftUI

struct DistributedFlowCard: View {
    let flow: DistributedFlow
    let onSelectProject: (String) -> Void
    var producerRootPath: String? = nil
    var consumerRootPath: String? = nil
    @State private var expanded = true

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 11) {
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: "point.3.connected.trianglepath.dotted")
                        .font(.title3).foregroundStyle(.secondary)
                        .frame(width: 38, height: 38)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                    VStack(alignment: .leading, spacing: 4) {
                        Text(flow.contract).font(.headline.monospaced()).textSelection(.enabled)
                        Text(flow.summary).font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 6)
                    ConfidencePill(value: flow.confidence)
                }

                HStack(spacing: 8) {
                    Button { onSelectProject(flow.producerProjectId) } label: {
                        Label(flow.producerProjectName, systemImage: "arrow.up.right.square")
                            .lineLimit(1).font(.callout.weight(.medium))
                    }.buttonStyle(.bordered)
                    Image(systemName: "arrow.right").foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("RabbitMQ / MassTransit").font(.caption.weight(.semibold))
                        Text("Contrato coincidente").font(.caption2).foregroundStyle(.secondary)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
                    Image(systemName: "arrow.right").foregroundStyle(.secondary)
                    Button { onSelectProject(flow.consumerProjectId) } label: {
                        Label(flow.consumerProjectName, systemImage: "arrow.down.left.square")
                            .lineLimit(1).font(.callout.weight(.medium))
                    }.buttonStyle(.bordered)
                    Spacer(minLength: 0)
                    Button { expanded.toggle() } label: {
                        Image(systemName: expanded ? "chevron.up" : "chevron.down")
                    }.buttonStyle(.borderless).help(expanded ? "Ocultar pasos" : "Mostrar pasos")
                }

                HStack {
                    Text(flow.producerEntryPoint).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                    Spacer()
                    Text(flow.consumerEntryPoint).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .padding(14)

            if !flow.steps.isEmpty {
                Divider()
                FlowPipelineView(steps: flow.steps.map { step in
                    let root = step.phase == "consumer" ? consumerRootPath : producerRootPath
                    return FlowPipelineNode(id: step.id, sequence: step.sequence, title: step.symbol,
                                            category: step.kind, detail: step.detail, file: step.file, line: step.line,
                                            rootPath: root, confidence: step.confidence, phase: step.phase)
                })
                .padding(.horizontal, 12)
                .padding(.vertical, 5)
            }

            if expanded {
                Divider()
                VStack(spacing: 0) {
                    ForEach(flow.steps.sorted { $0.sequence < $1.sequence }) { step in
                        DistributedFlowStepRow(step: step,
                                               rootPath: step.phase == "producer" ? producerRootPath : step.phase == "consumer" ? consumerRootPath : producerRootPath)
                        if step.id != flow.steps.last?.id { Divider().padding(.leading, 58) }
                    }
                }
                .padding(.vertical, 3)
            }
        }
        .atlasGlassCard(cornerRadius: 14)
    }
}
