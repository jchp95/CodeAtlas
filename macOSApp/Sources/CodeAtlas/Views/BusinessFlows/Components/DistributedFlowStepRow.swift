import SwiftUI

struct DistributedFlowStepRow: View {
    let step: DistributedFlowStep

    private var phaseLabel: String {
        switch step.phase {
        case "producer": return "PRODUCTOR · \(step.projectName)"
        case "transport": return "MENSAJERÍA"
        case "consumer": return "CONSUMIDOR · \(step.projectName)"
        default: return step.phase.uppercased()
        }
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(String(format: "%02d", step.sequence + 1))
                .font(.caption.monospaced().bold()).foregroundStyle(.secondary)
                .frame(width: 34, height: 30)
                .atlasGlassCard(cornerRadius: 8, shadow: false)
            VStack(alignment: .leading, spacing: 5) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(step.symbol).font(.system(.callout, design: .monospaced).weight(.semibold)).textSelection(.enabled)
                    Text(phaseLabel).font(.system(size: 9, weight: .bold)).foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                    ConfidencePill(value: step.confidence)
                }
                Text("\(step.file):\(step.line)").font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                Text(step.detail).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 11)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
