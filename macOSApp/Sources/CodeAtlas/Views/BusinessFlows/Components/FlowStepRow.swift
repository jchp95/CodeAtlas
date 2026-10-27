import SwiftUI

struct FlowStepRow: View {
    let step: BusinessFlowStep
    var rootPath: String? = nil

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(String(format: "%02d", step.sequence + 1))
                .font(.caption.monospaced().bold())
                .foregroundStyle(.secondary)
                .frame(width: 34, height: 30)
                .atlasGlassCard(cornerRadius: 8, shadow: false)
            VStack(alignment: .leading, spacing: 5) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(step.symbol).font(.system(.callout, design: .monospaced).weight(.semibold)).textSelection(.enabled)
                    Text(step.kind.replacingOccurrences(of: "-", with: " ").uppercased())
                        .font(.system(size: 9, weight: .bold)).foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                    ConfidencePill(value: step.confidence)
                }
                Button { SourceNavigator.open(step.file, root: rootPath, line: step.line) } label: {
                    Label("\(step.file):\(step.line)", systemImage: "arrow.up.forward.square")
                        .font(.caption.monospaced()).foregroundStyle(.secondary)
                }.buttonStyle(.plain).help("Abrir esta ubicación en el explorador de código")
                Text(step.detail).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 11)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
