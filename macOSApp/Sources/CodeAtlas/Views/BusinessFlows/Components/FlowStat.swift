import SwiftUI

struct FlowStat: View {
    let title: String
    let value: String
    let icon: String

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: icon).foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(value).font(.headline.monospacedDigit())
                Text(title).font(.caption2).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 11).padding(.vertical, 9)
        .atlasGlassCard(cornerRadius: 10)
    }
}
