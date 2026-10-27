import SwiftUI

struct BusinessFlowExplorerView: View {
    let snapshot: ArchitectureSnapshot
    let onSelectProject: (String) -> Void

    @State private var searchText = ""
    @State private var selectedRepository = "Todos los repositorios"
    @State private var selectedFlowKind = "Todos"

    private var repositoryOptions: [String] {
        let roots = snapshot.workspaceRoots ?? []
        return ["Todos los repositorios"] + roots.map { WorkspaceSnapshotMerger.label(for: URL(fileURLWithPath: $0, isDirectory: true)) }.sorted()
    }

    private func belongsToSelectedRepository(_ projectName: String) -> Bool {
        selectedRepository == "Todos los repositorios" || projectName.hasPrefix(selectedRepository + " · ")
    }

    private var allFlows: [BusinessFlow] {
        (snapshot.businessFlows ?? []).sorted {
            if $0.projectName.localizedCaseInsensitiveCompare($1.projectName) != .orderedSame {
                return $0.projectName.localizedCaseInsensitiveCompare($1.projectName) == .orderedAscending
            }
            return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }

    private var allDistributedFlows: [DistributedFlow] {
        (snapshot.distributedFlows ?? []).sorted {
            if $0.producerProjectName.localizedCaseInsensitiveCompare($1.producerProjectName) != .orderedSame {
                return $0.producerProjectName.localizedCaseInsensitiveCompare($1.producerProjectName) == .orderedAscending
            }
            if $0.contract != $1.contract { return $0.contract.localizedCaseInsensitiveCompare($1.contract) == .orderedAscending }
            return $0.consumerProjectName.localizedCaseInsensitiveCompare($1.consumerProjectName) == .orderedAscending
        }
    }

    private var filteredDistributedFlows: [DistributedFlow] {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        return allDistributedFlows.filter { flow in
            let repositoryMatches = selectedRepository == "Todos los repositorios"
                || flow.producerProjectName.hasPrefix(selectedRepository + " · ")
                || flow.consumerProjectName.hasPrefix(selectedRepository + " · ")
            guard repositoryMatches else { return false }
            guard !query.isEmpty else { return true }
            return [flow.contract, flow.producerProjectName, flow.consumerProjectName, flow.producerEntryPoint,
                    flow.consumerEntryPoint, flow.summary].contains { $0.localizedLowercase.contains(query) }
                || flow.steps.contains { step in
                    [step.kind, step.symbol, step.projectName, step.file, step.detail].contains { $0.localizedLowercase.contains(query) }
                }
        }
    }

    private var visibleFlows: [BusinessFlow] {
        selectedFlowKind == "Distribuidos" ? [] : filteredFlows
    }

    private var visibleDistributedFlows: [DistributedFlow] {
        selectedFlowKind == "Internos" ? [] : filteredDistributedFlows
    }

    private var filteredFlows: [BusinessFlow] {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines).localizedLowercase
        return allFlows.filter { flow in
            guard belongsToSelectedRepository(flow.projectName) else { return false }
            guard !query.isEmpty else { return true }
            return [flow.projectName, flow.name, flow.entryPoint, flow.entryFile, flow.summary].contains { $0.localizedLowercase.contains(query) }
                || flow.steps.contains { step in
                    [step.kind, step.symbol, step.file, step.detail].contains { $0.localizedLowercase.contains(query) }
                }
        }
    }

    private func sourceRoot(for projectID: String) -> String? {
        snapshot.projects.first(where: { $0.id == projectID })?.repositoryRoot ?? snapshot.repositoryPath
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                header
                Picker("Tipo de recorrido", selection: $selectedFlowKind) {
                    Text("Todos").tag("Todos")
                    Text("Dentro del servicio").tag("Internos")
                    Text("Entre microservicios").tag("Distribuidos")
                }
                .pickerStyle(.segmented)
                .frame(maxWidth: 540)

                HStack(spacing: 10) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Buscar ruta, handler, contrato o archivo…", text: $searchText)
                        .textFieldStyle(.plain)
                    if !searchText.isEmpty {
                        Button { searchText = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                            .buttonStyle(.plain)
                    }
                }
                .padding(11)
                .atlasGlassCard(cornerRadius: 10)

                if (snapshot.workspaceRoots?.count ?? 1) > 1 {
                    HStack(spacing: 10) {
                        Label("Repositorio", systemImage: "arrow.triangle.branch").font(.callout.weight(.medium)).foregroundStyle(.secondary)
                        Picker("Repositorio", selection: $selectedRepository) {
                            ForEach(repositoryOptions, id: \.self) { option in Text(option).tag(option) }
                        }.frame(maxWidth: 340)
                        Spacer()
                        Text("\(filteredFlows.count + filteredDistributedFlows.count) flujos visibles")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }

                if visibleFlows.isEmpty && visibleDistributedFlows.isEmpty {
                    ContentUnavailableView {
                        Label(allFlows.isEmpty && allDistributedFlows.isEmpty ? "Aún no hay flujos trazables" : "No hay coincidencias", systemImage: "point.topleft.down.curvedto.point.bottomright.up")
                    } description: {
                        Text(allFlows.isEmpty && allDistributedFlows.isEmpty
                             ? "Actualiza el análisis de una solución .NET o un monorepo. Las rutas y relaciones de mensajería aparecerán aquí cuando haya evidencia suficiente."
                             : "Prueba con un endpoint, servicio, contrato, handler o archivo.")
                    }
                    .frame(maxWidth: .infinity, minHeight: 250)
                } else {
                    if !visibleDistributedFlows.isEmpty {
                        VStack(alignment: .leading, spacing: 12) {
                            HStack {
                                Text("Flujos entre microservicios").font(.title2.bold())
                                Spacer()
                                Text("\(visibleDistributedFlows.count) recorrido(s)").font(.caption).foregroundStyle(.secondary)
                            }
                            Text("Productor → contrato de mensajería → consumidor, con referencias a los archivos fuente.")
                                .font(.callout).foregroundStyle(.secondary)
                            ForEach(visibleDistributedFlows) { flow in
                                DistributedFlowCard(flow: flow, onSelectProject: onSelectProject,
                                                    producerRootPath: sourceRoot(for: flow.producerProjectId),
                                                    consumerRootPath: sourceRoot(for: flow.consumerProjectId))
                            }
                        }
                    }
                    if !visibleFlows.isEmpty {
                        VStack(alignment: .leading, spacing: 12) {
                            Text("Flujos dentro de cada servicio").font(.title2.bold())
                            ForEach(visibleFlows) { flow in
                                BusinessFlowCard(flow: flow, onSelectProject: onSelectProject,
                                                 rootPath: sourceRoot(for: flow.projectId))
                            }
                        }
                    }
                }

                Text("Los flujos se derivan del código estático. Las ramas condicionales, reflexión, configuración de despliegue y comportamiento real pueden cambiar el recorrido; consulta siempre las evidencias de cada paso.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(12)
                    .atlasGlassCard(cornerRadius: 10, shadow: false)
            }
            .padding(26)
            .frame(maxWidth: 1500, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Recorridos detectados").font(.system(.title2, design: .rounded).weight(.bold))
                    Text("Sigue cada etapa, revisa su confianza y abre el archivo y la línea exactos desde la secuencia visual.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Image(systemName: "point.topleft.down.curvedto.point.bottomright.up")
                    .font(.system(size: 30, weight: .medium))
                    .foregroundStyle(.secondary)
                    .frame(width: 64, height: 64)
                    .atlasGlassCard(cornerRadius: 16, shadow: false)
            }
            HStack(spacing: 8) {
                FlowStat(title: "Flujos internos", value: "\(allFlows.count)", icon: "arrow.triangle.branch")
                FlowStat(title: "Distribuidos", value: "\(allDistributedFlows.count)", icon: "point.3.connected.trianglepath.dotted")
                FlowStat(title: "Pasos", value: "\(allFlows.reduce(0) { $0 + $1.steps.count } + allDistributedFlows.reduce(0) { $0 + $1.steps.count })", icon: "list.number")
                FlowStat(title: "Proyectos", value: "\(Set(allFlows.map(\.projectId) + allDistributedFlows.flatMap { [$0.producerProjectId, $0.consumerProjectId] }).count)", icon: "square.grid.2x2")
            }
        }
    }
}
