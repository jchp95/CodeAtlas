import SwiftUI

struct RelationDeltaRow: View {
    let relation: ArchitectureRelation
    let icon: String
    let color: Color
    let sourceName: String
    let targetName: String

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: icon).foregroundStyle(color)
            VStack(alignment: .leading, spacing: 3) {
                Text("\(sourceName) → \(targetName)").font(.callout.weight(.semibold))
                Text(relation.label).font(.caption).foregroundStyle(.secondary)
                Text(relation.evidence).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(2)
            }
            Spacer()
        }.padding(.vertical, 5)
    }
}
