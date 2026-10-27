import SwiftUI

struct ProjectInspector: View {
    let project: ProjectNode
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack { Text(project.name).font(.title3.bold()); Spacer(); Text(project.kind).font(.caption).foregroundStyle(.secondary) }
            Text(project.path).font(.caption.monospaced()).foregroundStyle(.secondary)
            if !project.endpoints.isEmpty {
                Text("Endpoints detectados").font(.headline)
                ForEach(project.endpoints, id: \.self) { endpoint in Label(endpoint, systemImage: "arrow.turn.down.right").font(.system(.caption, design: .monospaced)) }
            } else { Text("No se detectaron endpoints con los patrones iniciales.").font(.callout).foregroundStyle(.secondary) }
            if !project.dependencies.isEmpty {
                Text("Dependencias declaradas").font(.headline)
                Text(project.dependencies.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
            }
            if !project.projectReferences.isEmpty {
                Text("Referencias entre proyectos").font(.headline)
                ForEach(project.projectReferences, id: \.self) { reference in
                    Label(reference, systemImage: "arrow.turn.down.right").font(.system(.caption, design: .monospaced))
                }
            }
            if !project.contractsPublished.isEmpty {
                Text("Contratos publicados (candidatos)").font(.headline)
                Text(project.contractsPublished.joined(separator: " · ")).font(.caption.monospaced()).foregroundStyle(.secondary)
            }
            if !project.contractsConsumed.isEmpty {
                Text("Contratos consumidos (candidatos)").font(.headline)
                Text(project.contractsConsumed.joined(separator: " · ")).font(.caption.monospaced()).foregroundStyle(.secondary)
            }
            if !project.httpTargets.isEmpty {
                Text("Destinos HTTP candidatos").font(.headline)
                ForEach(project.httpTargets) { target in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(target.url).font(.system(.caption, design: .monospaced))
                        Text(target.sourcePath).font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }
            if !project.listenAddresses.isEmpty {
                Text("URLs locales declaradas").font(.headline)
                Text(project.listenAddresses.joined(separator: " · ")).font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
            }
            let evidence = (project.codeEvidence ?? []).sorted { lhs, rhs in
                let lhsPriority = evidencePriority(lhs.category)
                let rhsPriority = evidencePriority(rhs.category)
                if lhsPriority != rhsPriority { return lhsPriority < rhsPriority }
                if lhs.file != rhs.file { return lhs.file.localizedStandardCompare(rhs.file) == .orderedAscending }
                return lhs.line < rhs.line
            }
            if !evidence.isEmpty {
                DisclosureGroup("Evidencia de código y DI · \(evidence.count)") {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Evidencia estática. No demuestra por sí sola el comportamiento en ejecución.")
                            .font(.caption).foregroundStyle(.secondary)
                        ForEach(evidence.prefix(80)) { item in
                            VStack(alignment: .leading, spacing: 4) {
                                HStack(alignment: .firstTextBaseline, spacing: 8) {
                                    Text(evidenceCategory(item.category))
                                        .font(.caption.weight(.semibold))
                                    Text(item.symbol)
                                        .font(.system(.caption, design: .monospaced))
                                        .foregroundStyle(.secondary)
                                        .textSelection(.enabled)
                                    Spacer(minLength: 4)
                                    Text(item.confidence == "resolved" ? "Resuelto" : "Candidato")
                                        .font(.system(size: 9, weight: .semibold))
                                        .foregroundStyle(item.confidence == "resolved" ? .green : .orange)
                                }
                                Text(item.detail).font(.caption).foregroundStyle(.secondary)
                                Text("\(item.file):\(item.line)")
                                    .font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                                    .textSelection(.enabled)
                            }
                            .padding(9)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .atlasGlassCard(cornerRadius: 8, shadow: false)
                        }
                        if evidence.count > 80 {
                            Text("Se muestran 80 de \(evidence.count) evidencias. La salida JSON conserva el conjunto limitado por el analizador.")
                                .font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .padding(.top, 8)
                }
            }
        }.padding(16).frame(maxWidth: .infinity, alignment: .leading).atlasGlassCard(cornerRadius: 12, shadow: false)
    }

    private func evidencePriority(_ category: String) -> Int {
        switch category {
        case "api-endpoint": return 0
        case "message-published": return 1
        case "message-consumed": return 2
        case "service-registration": return 3
        case "constructor-injection": return 4
        default: return 4
        }
    }

    private func evidenceCategory(_ category: String) -> String {
        switch category {
        case "api-endpoint": return "Endpoint HTTP"
        case "message-published": return "Publicación de evento"
        case "message-consumed": return "Consumo de evento"
        case "service-registration": return "Registro DI"
        case "constructor-injection": return "Inyección por constructor"
        default: return category
        }
    }
}
