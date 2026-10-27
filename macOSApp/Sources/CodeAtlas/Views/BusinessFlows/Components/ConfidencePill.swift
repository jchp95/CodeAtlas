import SwiftUI

struct ConfidencePill: View {
    let value: String
    private var color: Color {
        switch value {
        case "resolved", "source-traced", "declared": return .green
        case "inferred": return .orange
        default: return .secondary
        }
    }

    var body: some View {
        Text(value.uppercased())
            .font(.system(size: 9, weight: .bold))
            .padding(.horizontal, 7).padding(.vertical, 4)
            .background(color.opacity(0.12), in: Capsule())
            .foregroundStyle(color)
    }
}
