import SwiftUI

struct MetricCard: View {
    let title: String; let value: String; let detail: String; let icon: String
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack { Image(systemName: icon).foregroundStyle(.secondary); Spacer(); Text(title).font(.caption).foregroundStyle(.secondary) }
            Text(value).font(.system(size: 30, weight: .semibold, design: .rounded))
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading).atlasGlassCard(cornerRadius: 14)
    }
}
