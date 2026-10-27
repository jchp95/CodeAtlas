import SwiftUI

struct FlowPipelineNode: Identifiable {
    let id: String
    let sequence: Int
    let title: String
    let category: String
    let detail: String
    let file: String
    let line: Int
    let rootPath: String?
    let confidence: String
    let phase: String

    var tint: Color {
        switch phase.lowercased() {
        case "producer", "entry", "endpoint", "controller": return .blue
        case "transport", "message", "event", "publisher", "consumer": return .purple
        case "database", "repository", "persistence": return .orange
        default:
            switch confidence.lowercased() {
            case "resolved", "source-traced", "declared", "confirmed": return .green
            case "inferred": return .orange
            default: return .secondary
            }
        }
    }
}

/// Compact, horizontally navigable pipeline; each stage opens its concrete source location.
struct FlowPipelineView: View {
    let steps: [FlowPipelineNode]

    private var orderedSteps: [FlowPipelineNode] { steps.sorted { $0.sequence < $1.sequence } }

    var body: some View {
        ScrollView(.horizontal) {
            HStack(alignment: .center, spacing: 8) {
                ForEach(orderedSteps) { step in
                    if step.id != orderedSteps.first?.id {
                        Image(systemName: "arrow.right")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(.tertiary)
                    }
                    Button {
                        SourceNavigator.open(step.file, root: step.rootPath, line: step.line)
                    } label: {
                        VStack(alignment: .leading, spacing: 5) {
                            HStack(spacing: 5) {
                                Text(String(format: "%02d", step.sequence + 1))
                                    .font(.system(size: 9, weight: .bold, design: .monospaced))
                                    .foregroundStyle(step.tint)
                                Image(systemName: "doc.text")
                                    .font(.system(size: 9, weight: .semibold))
                                    .foregroundStyle(step.tint)
                                Text(step.category.replacingOccurrences(of: "-", with: " ").uppercased())
                                    .font(.system(size: 8, weight: .bold)).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Text(step.title).font(.system(size: 11, weight: .semibold, design: .rounded))
                                .lineLimit(1).frame(maxWidth: 150, alignment: .leading)
                            Text(URL(fileURLWithPath: step.file).lastPathComponent + ":" + String(step.line))
                                .font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                        }
                        .padding(.horizontal, 10).padding(.vertical, 8)
                        .frame(minWidth: 118, maxWidth: 172, minHeight: 65, alignment: .leading)
                        .background(step.tint.opacity(0.075), in: RoundedRectangle(cornerRadius: 9))
                        .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(step.tint.opacity(0.22), lineWidth: 1))
                    }
                    .buttonStyle(.plain)
                    .help("Abrir \(step.file):\(step.line)")
                }
            }
            .padding(.horizontal, 2)
            .padding(.vertical, 5)
        }
        .scrollIndicators(.visible)
    }
}
