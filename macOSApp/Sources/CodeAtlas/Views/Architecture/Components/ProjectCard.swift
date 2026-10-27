import SwiftUI

struct ProjectCard: View {
    let project: ProjectNode; let selected: Bool; let action: () -> Void
    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .top) {
                    Image(systemName: project.kind == "Dotnet" ? "curlybraces" : "globe").font(.title2).foregroundStyle(project.kind == "Dotnet" ? .blue : .purple)
                    Spacer()
                    Image(systemName: selected ? "checkmark.circle.fill" : "arrow.up.right.square").foregroundStyle(.secondary)
                }
                Text(project.name).font(.headline).lineLimit(1).foregroundStyle(.primary)
                Text(project.path).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(2)
                HStack(spacing: 6) {
                    Label("\(project.endpoints.count) endpoints", systemImage: "point.3.connected.trianglepath.dotted")
                    if let framework = project.framework { Text("· \(framework)") }
                }.font(.caption2).foregroundStyle(.secondary)
            }.padding(14).frame(maxWidth: .infinity, alignment: .leading).atlasGlassCard(cornerRadius: 13, highlighted: selected)
        }.buttonStyle(.plain)
    }
}
