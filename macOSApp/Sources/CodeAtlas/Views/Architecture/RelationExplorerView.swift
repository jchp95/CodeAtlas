import SwiftUI

/// Evidence-oriented relationship explorer. It is intentionally separate from the
/// node diagram: the graph answers "what connects?", this view answers "why?".
struct RelationExplorerView: View {
    let snapshot: ArchitectureSnapshot
    let onSelectProject: (String) -> Void

    @State private var searchText = ""
    @State private var selectedKind = "Todas"
    @State private var selectedConfidence = "Todas"
    @State private var showOnlyWithSource = false

    private let kinds = ["Todas", "HTTP", "Eventos", "Referencias", "Imports", "Workspace", "Otras"]
    private let confidenceOptions = ["Todas", "Confirmadas", "Inferidas"]

    private var filteredRelations: [ArchitectureRelation] {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        return snapshot.relations.filter { relation in
            guard kindLabel(relation.type) == selectedKind || selectedKind == "Todas" else { return false }
            if selectedConfidence == "Confirmadas" && !isConfirmed(relation.confidence) { return false }
            if selectedConfidence == "Inferidas" && isConfirmed(relation.confidence) { return false }
            if showOnlyWithSource && sourceEvidence(for: relation) == nil { return false }
            guard !query.isEmpty else { return true }
            let source = project(for: relation.sourceId)?.name ?? relation.sourceId
            let target = project(for: relation.targetId)?.name ?? relation.targetId
            return [source, target, relation.type, relation.label, relation.evidence, relation.contract ?? ""]
                .contains { $0.localizedLowercase.contains(query) }
        }
        .sorted {
            if relationOrder($0) != relationOrder($1) { return relationOrder($0) < relationOrder($1) }
            if $0.confidence != $1.confidence { return isConfirmed($0.confidence) && !isConfirmed($1.confidence) }
            return $0.label.localizedCaseInsensitiveCompare($1.label) == .orderedAscending
        }
    }

    private var sourceCount: Int { snapshot.relations.filter { sourceEvidence(for: $0) != nil }.count }
    private var confirmedCount: Int { snapshot.relations.filter { isConfirmed($0.confidence) }.count }
    private var eventCount: Int { snapshot.relations.filter { kindLabel($0.type) == "Eventos" }.count }
    private var httpCount: Int { snapshot.relations.filter { kindLabel($0.type) == "HTTP" }.count }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                header
                LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible())], spacing: 10) {
                    MetricCard(title: "Relaciones", value: "\(snapshot.relations.count)", detail: "Conexiones detectadas", icon: "point.3.connected.trianglepath.dotted")
                    MetricCard(title: "HTTP", value: "\(httpCount)", detail: "Llamadas entre proyectos", icon: "network")
                    MetricCard(title: "Eventos", value: "\(eventCount)", detail: "Contratos / mensajería", icon: "arrow.left.arrow.right")
                    MetricCard(title: "Con fuente", value: "\(sourceCount)", detail: "De \(confirmedCount) confirmadas", icon: "doc.text.magnifyingglass")
                }

                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 10) {
                        Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                        TextField("Buscar proyecto, contrato, ruta o evidencia…", text: $searchText).textFieldStyle(.plain)
                        if !searchText.isEmpty {
                            Button { searchText = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }.buttonStyle(.plain)
                        }
                    }
                    .padding(11).atlasGlassCard(cornerRadius: 10, shadow: false)

                    HStack(spacing: 8) {
                        Picker("Tipo", selection: $selectedKind) {
                            ForEach(kinds, id: \.self) { Text($0).tag($0) }
                        }.frame(width: 150)
                        Picker("Confianza", selection: $selectedConfidence) {
                            ForEach(confidenceOptions, id: \.self) { Text($0).tag($0) }
                        }.frame(width: 160)
                        Toggle("Solo con fuente", isOn: $showOnlyWithSource).toggleStyle(.checkbox)
                        Spacer()
                        Text("\(filteredRelations.count) / \(snapshot.relations.count)").font(.caption.monospaced()).foregroundStyle(.secondary)
                    }
                }

                if filteredRelations.isEmpty {
                    ContentUnavailableView("No hay relaciones para estos filtros", systemImage: "point.3.connected.trianglepath.dotted", description: Text("Prueba otro tipo de relación o desactiva “Solo con fuente”. Los enlaces de fuente se muestran cuando el analizador conserva un archivo y una línea concretos."))
                        .frame(maxWidth: .infinity, minHeight: 220)
                } else {
                    LazyVStack(spacing: 10) {
                        ForEach(filteredRelations) { relation in
                            relationCard(relation)
                        }
                    }
                }

                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "info.circle").foregroundStyle(.secondary)
                    Text("Confirmada significa que la relación se apoyó en evidencia directa del analizador; inferida significa que se dedujo de nombres, contratos o referencias. No equivale a una llamada verificada en ejecución. Cuando no se conserva una ubicación exacta, se muestra la evidencia textual sin inventar una línea.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .padding(12).atlasGlassCard(cornerRadius: 10, shadow: false)
            }
            .padding(26)
            .frame(maxWidth: 1500, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Mapa de relaciones verificables").font(.system(.title2, design: .rounded).weight(.bold))
                    Text("Inspecciona el origen, destino, tipo de conexión, evidencia y nivel de confianza sin perder el acceso al código fuente.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Image(systemName: "point.3.connected.trianglepath.dotted").font(.system(size: 30, weight: .medium)).foregroundStyle(.secondary)
                    .frame(width: 62, height: 62).atlasGlassCard(cornerRadius: 16, shadow: false)
            }
        }
    }

    private func relationCard(_ relation: ArchitectureRelation) -> some View {
        let sourceProject = project(for: relation.sourceId)
        let targetProject = project(for: relation.targetId)
        let evidence = sourceEvidence(for: relation)

        return VStack(alignment: .leading, spacing: 11) {
            HStack(alignment: .top, spacing: 10) {
                projectButton(sourceProject, fallback: relation.sourceId, icon: "arrow.up.right.square")
                    .frame(maxWidth: .infinity, alignment: .leading)
                VStack(spacing: 5) {
                    Image(systemName: kindIcon(relation.type)).font(.title3).foregroundStyle(kindTint(relation.type))
                    Text(kindLabel(relation.type).uppercased()).font(.system(size: 8, weight: .bold)).tracking(0.5).foregroundStyle(.secondary)
                    Image(systemName: "arrow.right").font(.caption.bold()).foregroundStyle(.secondary)
                }
                .frame(width: 80)
                projectButton(targetProject, fallback: relation.targetId, icon: "arrow.down.left.square")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Divider()
            HStack(alignment: .top, spacing: 10) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(relation.label.isEmpty ? relation.type : relation.label).font(.callout.weight(.semibold)).textSelection(.enabled)
                    if let contract = relation.contract, !contract.isEmpty {
                        Label(contract, systemImage: "shippingbox").font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    Text(relation.evidence.isEmpty ? "El analizador no conservó un texto de evidencia adicional." : relation.evidence)
                        .font(.caption).foregroundStyle(.secondary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                VStack(alignment: .trailing, spacing: 8) {
                    ConfidencePill(value: relation.confidence)
                    if let evidence {
                        Button {
                            SourceNavigator.open(evidence.file, root: evidence.root, line: evidence.line)
                        } label: {
                            Label("Abrir evidencia", systemImage: "arrow.up.forward.square").font(.caption.weight(.medium))
                        }.buttonStyle(.bordered).controlSize(.small)
                    }
                }
            }
        }
        .padding(14)
        .atlasGlassCard(cornerRadius: 13, shadow: false)
    }

    private func projectButton(_ project: ProjectNode?, fallback: String, icon: String) -> some View {
        Button {
            if let project { onSelectProject(project.id) }
        } label: {
            HStack(spacing: 8) {
                Image(systemName: icon).foregroundStyle(Color.accentColor)
                VStack(alignment: .leading, spacing: 3) {
                    Text(project?.name ?? fallback).font(.callout.weight(.semibold)).lineLimit(2).foregroundStyle(.primary)
                    if let project {
                        Text("\(project.kind) · \(project.path)").font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(10).frame(maxWidth: .infinity, alignment: .leading).contentShape(RoundedRectangle(cornerRadius: 8))
            .background(.quaternary.opacity(0.32), in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .disabled(project == nil)
    }

    private struct SourceEvidence {
        let file: String
        let root: String
        let line: Int
    }

    private func sourceEvidence(for relation: ArchitectureRelation) -> SourceEvidence? {
        let source = project(for: relation.sourceId)
        let target = project(for: relation.targetId)
        let rawEvidence = relation.evidence.lowercased()
        let labels = [relation.contract, relation.label, relation.evidence].compactMap { $0?.lowercased() }.filter { !$0.isEmpty }
        let projects = [source, target].compactMap { $0 }
        let candidates = projects.flatMap { project -> [(ProjectNode, ArchitectureEvidence)] in
            (project.codeEvidence ?? []).map { (project, $0) }
        }
        if !rawEvidence.isEmpty, let exact = candidates.first(where: { pair in
            rawEvidence.contains(pair.1.file.lowercased()) && !pair.1.file.isEmpty && pair.1.line > 0
        }) {
            return SourceEvidence(file: exact.1.file, root: exact.0.repositoryRoot ?? snapshot.repositoryPath, line: exact.1.line)
        }

        let scored = candidates.compactMap { pair -> (Int, ProjectNode, ArchitectureEvidence)? in
            let project = pair.0
            let evidence = pair.1
            guard evidence.line > 0, !evidence.file.isEmpty else { return nil }
            let symbol = evidence.symbol.lowercased()
            let detail = evidence.detail.lowercased()
            let category = evidence.category.lowercased()
            var score = 0
            for label in labels where label.count >= 4 {
                if symbol == label { score += 12 }
                else if symbol.contains(label) || label.contains(symbol) { score += 6 }
                if detail.contains(label) { score += 3 }
            }
            if relation.type == "message-flow" && (category.contains("message") || category.contains("contract")) { score += 2 }
            if relation.type == "http-call" && (category.contains("http") || category.contains("endpoint")) { score += 2 }
            if relation.type == "project-reference" && (category.contains("reference") || category.contains("dependency")) { score += 2 }
            return score > 0 ? (score, project, evidence) : nil
        }.sorted { $0.0 > $1.0 }
        guard let best = scored.first else { return nil }
        if scored.count > 1 && scored[0].0 == scored[1].0 { return nil }
        return SourceEvidence(file: best.2.file, root: best.1.repositoryRoot ?? snapshot.repositoryPath, line: best.2.line)
    }

    private func project(for id: String) -> ProjectNode? {
        snapshot.projects.first { $0.id == id }
    }

    private func isConfirmed(_ confidence: String) -> Bool {
        ["confirmed", "resolved", "source-traced", "declared"].contains(confidence.lowercased())
    }

    private func kindLabel(_ type: String) -> String {
        switch type {
        case "http-call": return "HTTP"
        case "message-flow": return "Eventos"
        case "project-reference": return "Referencias"
        case "module-import": return "Imports"
        case "workspace-dependency": return "Workspace"
        default: return "Otras"
        }
    }

    private func kindIcon(_ type: String) -> String {
        switch type {
        case "http-call": return "network"
        case "message-flow": return "point.3.connected.trianglepath.dotted"
        case "project-reference": return "shippingbox"
        case "module-import": return "curlybraces"
        default: return "arrow.left.arrow.right"
        }
    }

    private func kindTint(_ type: String) -> Color {
        switch type {
        case "http-call": return .blue
        case "message-flow": return .purple
        case "project-reference": return .teal
        case "module-import": return .orange
        default: return .secondary
        }
    }

    private func relationOrder(_ relation: ArchitectureRelation) -> Int {
        switch kindLabel(relation.type) {
        case "HTTP": return 0
        case "Eventos": return 1
        case "Referencias": return 2
        case "Imports": return 3
        case "Workspace": return 4
        default: return 5
        }
    }
}
