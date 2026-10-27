import SwiftUI

struct RelationFlowRow: View {
    let relation: ArchitectureRelation
    let sourceName: String
    let targetName: String

    var body: some View {
        HStack(spacing: 10) {
            endpointBlock(sourceName, systemImage: "square.stack.3d.up")
                .frame(maxWidth: 190, alignment: .leading)
            VStack(spacing: 4) {
                Image(systemName: "arrow.right").foregroundStyle(relation.type == "message-flow" ? .purple : .blue)
                Text(relation.type == "message-flow" ? "EVENTO" : relation.type == "http-call" ? "HTTP" : relation.type == "project-reference" ? "REFERENCIA" : relation.type == "module-import" ? "IMPORT TS" : relation.type == "workspace-dependency" ? "WORKSPACE" : "DEPENDENCIA")
                    .font(.system(size: 8, weight: .bold, design: .rounded)).tracking(0.6).foregroundStyle(.secondary)
            }
            .frame(width: 92)
            endpointBlock(targetName, systemImage: "square.stack.3d.up")
                .frame(maxWidth: 190, alignment: .leading)
            VStack(alignment: .leading, spacing: 3) {
                Text(relation.label).font(.caption.bold()).lineLimit(2)
                Text(relation.evidence).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(2)
            }
            Spacer(minLength: 4)
            Text(relation.confidence == "confirmed" ? "Confirmada" : "Inferida")
                .font(.system(size: 9, weight: .semibold)).foregroundStyle(relation.confidence == "confirmed" ? .green : .orange)
                .padding(.horizontal, 7).padding(.vertical, 4)
                .background((relation.confidence == "confirmed" ? Color.green : Color.orange).opacity(0.12), in: Capsule())
        }
        .padding(.horizontal, 12).padding(.vertical, 10)
    }

    private func endpointBlock(_ name: String, systemImage: String) -> some View {
        HStack(spacing: 7) {
            Image(systemName: systemImage).foregroundStyle(.blue)
            Text(name).font(.system(.callout, design: .rounded).weight(.semibold)).lineLimit(2)
        }
        .padding(9).frame(maxWidth: .infinity, alignment: .leading)
        .atlasGlassCard(cornerRadius: 8, shadow: false)
    }
}
