import SwiftUI
import AppKit

extension ContentView {
func summaryCards(_ snapshot: ArchitectureSnapshot) -> some View {
        LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible())], spacing: 12) {
            MetricCard(title: "Proyectos .NET", value: "\(snapshot.summary.dotnetProjects)", detail: "Soluciones y APIs", icon: "chevron.left.forwardslash.chevron.right")
            MetricCard(title: "Proyectos web", value: "\(snapshot.summary.nodeProjects)", detail: "Node.js / Next.js", icon: "globe")
            MetricCard(title: "Endpoints candidatos", value: "\(snapshot.summary.endpointCandidates)", detail: "Analizados con Roslyn / Next routing", icon: "point.3.connected.trianglepath.dotted")
            MetricCard(title: "Relaciones detectadas", value: "\(snapshot.summary.relations)", detail: "HTTP · eventos · referencias", icon: "arrow.left.arrow.right")
        }
    }

    @ViewBuilder
    func gitRepositoriesSection(_ snapshot: ArchitectureSnapshot) -> some View {
        if let repositories = snapshot.gitRepositories, !repositories.isEmpty {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Repositorios Git del workspace").font(.title3.bold())
                        Text("Estado independiente de cada repositorio incluido en este mapa.")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Text("\(repositories.count) repositorios").font(.caption).foregroundStyle(.secondary)
                }
                LazyVGrid(columns: [GridItem(.flexible(), alignment: .leading), GridItem(.flexible(), alignment: .leading)], spacing: 10) {
                    ForEach(repositories) { repository in
                        let changeCount = repository.changes?.count ?? 0
                        VStack(alignment: .leading, spacing: 8) {
                            HStack(alignment: .top, spacing: 8) {
                                Image(systemName: "arrow.triangle.branch").foregroundStyle(.secondary)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(repository.path == "." ? "Raíz del workspace" : repository.path)
                                        .font(.subheadline.bold()).lineLimit(1)
                                    Text(repository.branch).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                                }
                                Spacer(minLength: 4)
                                Text(changeCount == 0 ? "Limpio" : "\(changeCount) cambios")
                                    .font(.caption2.bold())
                                    .foregroundStyle(changeCount == 0 ? Color.green : Color.orange)
                                    .padding(.horizontal, 7).padding(.vertical, 4)
                                    .background((changeCount == 0 ? Color.green : Color.orange).opacity(0.11), in: Capsule())
                            }
                            Text("HEAD \(repository.headCommit)")
                                .font(.caption2.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                            if let change = repository.changes?.first {
                                Text("\(change.status) · \(change.path)")
                                    .font(.caption2.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                        .padding(12)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .atlasGlassCard(cornerRadius: 10)
                    }
                }
            }
        }
    }

    func architectureSection(_ snapshot: ArchitectureSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 9) {
                Label("Diagrama interactivo", systemImage: "square.3.layers.3d")
                    .font(.system(.headline, design: .rounded).weight(.semibold))
                    .foregroundStyle(.primary)
                Spacer()
                AtlasStatusPill(title: "\(snapshot.projects.count) bloques", color: .blue)
                AtlasStatusPill(title: "\(snapshot.relations.count) conexiones", color: .purple)
            }
            if snapshot.projects.isEmpty {
                ContentUnavailableView("No se detectaron proyectos", systemImage: "doc.questionmark", description: Text("Comprueba que seleccionaste la raíz de la solución o monorepo.")).frame(height: 180)
                    .atlasGlassCard(cornerRadius: 16)
            } else {
                ArchitectureDiagramView(
                    snapshot: snapshot,
                    workspaceURL: repositoryURL,
                    onOpenOperations: { selectedSection = "Servicios"; operationsMenuExpanded = true },
                    selectedProjectID: $selectedProjectID
                )
            }
            if !snapshot.warnings.isEmpty {
                VStack(alignment: .leading, spacing: 9) {
                    Label("Notas del análisis", systemImage: "info.circle.fill")
                        .font(.headline).foregroundStyle(.primary)
                    ForEach(snapshot.warnings, id: \.self) { warning in
                        Label(warning, systemImage: "exclamationmark.circle")
                            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .atlasGlassCard(cornerRadius: 14, shadow: false)
            }
        }
    }

    func relationsSection(_ snapshot: ArchitectureSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Conectividad detectada").font(.title3.bold())
                    Text("Relaciones respaldadas por referencias de proyecto o contratos identificados en el código.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Text("\(snapshot.relations.count) relaciones").font(.caption).foregroundStyle(.secondary)
            }
            if snapshot.relations.isEmpty {
                Label("Todavía no hay conexiones confirmadas", systemImage: "point.3.connected.trianglepath.dotted")
                    .foregroundStyle(.secondary).padding(14).frame(maxWidth: .infinity, alignment: .leading)
                    .atlasGlassCard(cornerRadius: 12, shadow: false)
            } else {
                VStack(spacing: 0) {
                    ForEach(snapshot.relations) { relation in
                        RelationFlowRow(
                            relation: relation,
                            sourceName: snapshot.projects.first(where: { $0.id == relation.sourceId })?.name ?? relation.sourceId,
                            targetName: snapshot.projects.first(where: { $0.id == relation.targetId })?.name ?? relation.targetId
                        )
                        if relation.id != snapshot.relations.last?.id { Divider().padding(.horizontal, 12) }
                    }
                }
                .atlasGlassCard(cornerRadius: 12)
            }
        }
    }

    func architectureDeltaSection(_ snapshot: ArchitectureSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Impacto desde el último análisis").font(.title3.bold())
                    Text("Comparación de snapshots locales, no solo del diff de Git.").font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                if let delta = snapshot.delta {
                    Text("\(delta.totalChanges) cambios").font(.caption.bold())
                        .padding(.horizontal, 9).padding(.vertical, 5)
                        .background(delta.totalChanges == 0 ? Color.green.opacity(0.12) : Color.orange.opacity(0.14), in: Capsule())
                }
            }
            guardDelta(snapshot.delta, projects: snapshot.projects)
        }
    }

    @ViewBuilder
    func guardDelta(_ delta: ArchitectureDelta?, projects: [ProjectNode]) -> some View {
        if let delta {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 8) {
                    Label("\(delta.previousBranch) · \(delta.previousCommit)", systemImage: "clock.arrow.circlepath")
                    Image(systemName: "arrow.right").foregroundStyle(.secondary)
                    Label("\(delta.currentBranch) · \(delta.currentCommit)", systemImage: "arrow.triangle.branch")
                }.font(.caption.monospaced()).foregroundStyle(.secondary)
                ForEach(delta.summary, id: \.self) { line in
                    Label(line, systemImage: delta.totalChanges == 0 ? "checkmark.circle" : "arrow.triangle.branch")
                        .font(.callout)
                }

                if !delta.addedProjects.isEmpty || !delta.removedProjects.isEmpty || !delta.changedProjects.isEmpty {
                    Divider()
                    Text("Proyectos afectados").font(.headline)
                    ForEach(delta.addedProjects) { project in
                        DeltaRow(icon: "plus.circle.fill", color: .green, title: project.name, detail: "Añadido · \(project.path)")
                    }
                    ForEach(delta.removedProjects) { project in
                        DeltaRow(icon: "minus.circle.fill", color: .red, title: project.name, detail: "Eliminado · \(project.path)")
                    }
                    ForEach(delta.changedProjects) { project in
                        DeltaRow(icon: "pencil.circle.fill", color: .orange, title: project.name, detail: project.changedFields.joined(separator: " · "))
                    }
                }

                if !delta.addedRelations.isEmpty || !delta.removedRelations.isEmpty {
                    Divider()
                    Text("Conexiones modificadas").font(.headline)
                    ForEach(delta.addedRelations) { relation in
                        RelationDeltaRow(relation: relation, icon: "plus.circle.fill", color: .green,
                            sourceName: projects.first(where: { $0.id == relation.sourceId })?.name ?? relation.sourceId,
                            targetName: projects.first(where: { $0.id == relation.targetId })?.name ?? relation.targetId)
                    }
                    ForEach(delta.removedRelations) { relation in
                        RelationDeltaRow(relation: relation, icon: "minus.circle.fill", color: .red,
                            sourceName: projects.first(where: { $0.id == relation.sourceId })?.name
                                ?? delta.removedProjects.first(where: { $0.id == relation.sourceId })?.name
                                ?? relation.sourceId,
                            targetName: projects.first(where: { $0.id == relation.targetId })?.name
                                ?? delta.removedProjects.first(where: { $0.id == relation.targetId })?.name
                                ?? relation.targetId)
                    }
                }
                if let flowChanges = delta.flowChanges, !flowChanges.isEmpty {
                    Divider()
                    Text("Flujos de negocio modificados").font(.headline)
                    ForEach(flowChanges) { change in
                        let isAdded = change.status == "added"
                        let isRemoved = change.status == "removed"
                        DeltaRow(
                            icon: isAdded ? "plus.circle.fill" : (isRemoved ? "minus.circle.fill" : "arrow.triangle.2.circlepath"),
                            color: isAdded ? .green : (isRemoved ? .red : .orange),
                            title: "\(change.projectName) · \(change.name)",
                            detail: change.changes.joined(separator: " · ")
                        )
                    }
                }
                if let distributedChanges = delta.distributedFlowChanges, !distributedChanges.isEmpty {
                    Divider()
                    Text("Recorridos entre microservicios modificados").font(.headline)
                    ForEach(distributedChanges) { change in
                        let isAdded = change.status == "added"
                        let isRemoved = change.status == "removed"
                        DeltaRow(
                            icon: isAdded ? "plus.circle.fill" : (isRemoved ? "minus.circle.fill" : "arrow.triangle.2.circlepath"),
                            color: isAdded ? .green : (isRemoved ? .red : .orange),
                            title: "\(change.producerProjectName) · \(change.producerEntryPoint) → \(change.consumerProjectName)",
                            detail: "\(change.contract) · " + change.changes.joined(separator: " · ")
                        )
                    }
                }
            }
            .padding(14).frame(maxWidth: .infinity, alignment: .leading)
            .atlasGlassCard(cornerRadius: 12)
        } else {
            Label("Primera captura guardada. CodeAtlas comparará la próxima actualización con este estado.", systemImage: "bookmark")
                .font(.callout).foregroundStyle(.secondary).padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 10))
        }
    }

    func changesSection(_ snapshot: ArchitectureSnapshot) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Cambios locales").font(.title3.bold())
                Spacer()
                Text("\(snapshot.changes.count) archivos").font(.caption).foregroundStyle(.secondary)
            }
            if snapshot.changes.isEmpty {
                Label("El árbol de trabajo está limpio", systemImage: "checkmark.circle").foregroundStyle(.secondary).padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading).background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 10))
            } else {
                VStack(spacing: 0) {
                    ForEach(snapshot.changes.prefix(30)) { change in
                        HStack(spacing: 10) {
                            Text(change.status).font(.caption.monospaced().bold()).foregroundStyle(statusColor(change.status)).frame(width: 30)
                            Image(systemName: change.path.hasSuffix(".cs") ? "curlybraces" : change.path.hasSuffix(".ts") || change.path.hasSuffix(".tsx") ? "chevron.left.forwardslash.chevron.right" : "doc.text")
                                .foregroundStyle(.secondary)
                            Text(change.path).font(.system(.callout, design: .monospaced)).lineLimit(1)
                            Spacer()
                        }.padding(.vertical, 8).padding(.horizontal, 10)
                        if change.id != snapshot.changes.prefix(30).last?.id { Divider() }
                    }
                }.atlasGlassCard(cornerRadius: 10)
            }
        }
    }
}
