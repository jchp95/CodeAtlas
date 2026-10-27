import SwiftUI
import AppKit

extension ArchitectureDiagramView {
var runtimeSummaryBar: some View {
        HStack(spacing: 12) {
            Image(systemName: "server.rack")
                .font(.title3)
                .foregroundStyle(.secondary)
                .frame(width: 34, height: 34)
                .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 3) {
                Text("Operaciones locales").font(.callout.weight(.semibold))
                if (snapshot.workspaceRoots?.count ?? 0) > 1 {
                    let profiles = matchingProfiles
                    let running = profiles.reduce(0) { $0 + $1.services.filter { $0.status == "running" }.count }
                    let total = profiles.reduce(0) { $0 + $1.services.count }
                    Text("\(profiles.count) perfiles · \(running)/\(total) servicios en ejecución")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                } else if let profile = matchingProfile {
                    let running = profile.services.filter { $0.status == "running" }.count
                    Text("\(profile.name) · \(running)/\(profile.services.count) servicios en ejecución")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                } else if isRefreshingRuntime {
                    Text("Consultando estado de StackPilot…").font(.caption).foregroundStyle(.secondary)
                } else if let runtimeLoadError {
                    Text(runtimeLoadError).font(.caption).foregroundStyle(.orange).lineLimit(1)
                } else if runtimeResponse?.configured == false {
                    Text("StackPilot aún no tiene un perfil para este workspace.")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                } else {
                    Text("No hay un perfil asociado a este repositorio.")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            if isRefreshingRuntime { ProgressView().controlSize(.small) }
            Button {
                Task { await refreshRuntimeStatus() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.bordered)
            .help("Actualizar estado de los servicios")
            .disabled(isRefreshingRuntime || busyServiceKey != nil)
            Button("Abrir StackPilot…", action: onOpenOperations)
                .buttonStyle(.borderedProminent)
        }
        .padding(12)
        .atlasGlassCard(cornerRadius: 12)
    }

    @ViewBuilder
    var selectedRuntimeOperations: some View {
        if let selectedProjectID,
           let project = snapshot.projects.first(where: { $0.id == selectedProjectID }),
           let binding = runtimeBinding(for: project) {
            runtimeOperationsCard(project: project, binding: binding)
        }
    }

    func runtimeOperationsCard(project: ProjectNode, binding: ArchitectureRuntimeBinding) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 9) {
                Image(systemName: "terminal.fill").foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 3) {
                    Text("Control local · \(binding.service.name)").font(.headline)
                    Text("Proyecto: \(project.name) · Perfil: \(binding.profileName)").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                runtimeStatusPill(binding.service.status)
            }
            Text(binding.service.command)
                .font(.caption.monospaced())
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            Text(binding.service.workingDirectory)
                .font(.caption2.monospaced())
                .foregroundStyle(.tertiary)
                .textSelection(.enabled)
            if let healthURL = binding.service.healthUrl, !healthURL.isEmpty {
                Label("Health: \(healthURL) · \(binding.service.healthStatus)", systemImage: binding.service.healthStatus == "healthy" ? "checkmark.shield.fill" : "waveform.path.ecg")
                    .font(.caption).foregroundStyle(binding.service.healthStatus == "healthy" ? Color.green : Color.secondary)
            }
            if let runtimeMessage, !runtimeMessage.isEmpty {
                Text(runtimeMessage).font(.caption).foregroundStyle(.secondary)
            }
            HStack(spacing: 8) {
                if binding.service.enabled {
                    Button {
                        Task { await performRuntimeAction(["start", binding.profileID, binding.service.id], key: "\(binding.profileID)/\(binding.service.id)") }
                    } label: {
                        Label("Iniciar", systemImage: "play.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(isRefreshingRuntime || busyServiceKey != nil || binding.service.status == "running")

                    Button {
                        Task { await performRuntimeAction(["stop", binding.profileID, binding.service.id], key: "\(binding.profileID)/\(binding.service.id)") }
                    } label: {
                        Label("Detener", systemImage: "stop.fill")
                    }
                    .buttonStyle(.bordered)
                    .disabled(isRefreshingRuntime || busyServiceKey != nil || binding.service.status != "running")
                } else {
                    Label("Plantilla desactivada: revisa el comando antes de habilitarla.", systemImage: "lock.fill")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Revisar en StackPilot", action: onOpenOperations).buttonStyle(.borderedProminent)
                }
                Button {
                    Task { await loadRuntimeLogs(binding) }
                } label: { Label("Ver logs", systemImage: "text.alignleft") }
                    .buttonStyle(.bordered)
                    .disabled(busyServiceKey != nil)
                Spacer()
                if let pid = binding.service.pid {
                    Text("PID \(pid)").font(.caption.monospaced()).foregroundStyle(.secondary)
                }
            }
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .atlasGlassCard(cornerRadius: 12)
    }

    func runtimeStatusPill(_ status: String) -> some View {
        let color: Color = {
            switch status {
            case "running": return .green
            case "unavailable": return .red
            case "disabled": return .secondary
            default: return .orange
            }
        }()
        let title: String = {
            switch status {
            case "running": return "EN EJECUCIÓN"
            case "unavailable": return "NO DISPONIBLE"
            case "disabled": return "DESACTIVADO"
            default: return "DETENIDO"
            }
        }()
        return Text(title)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 8).padding(.vertical, 4)
            .background(color.opacity(0.13), in: Capsule())
            .foregroundStyle(color)
    }

    func refreshRuntimeStatus() async {
        guard !isRefreshingRuntime else { return }
        isRefreshingRuntime = true
        defer { isRefreshingRuntime = false }
        do {
            runtimeResponse = try await engine.stackPilot(arguments: ["status"])
            runtimeLoadError = runtimeResponse?.error
        } catch {
            runtimeLoadError = error.localizedDescription
        }
    }

    func performRuntimeAction(_ arguments: [String], key: String) async {
        guard busyServiceKey == nil else { return }
        busyServiceKey = key
        defer { busyServiceKey = nil }
        runtimeMessage = nil
        do {
            let next = try await engine.stackPilot(arguments: arguments)
            runtimeResponse = next
            if let error = next.error {
                runtimeMessage = error
                runtimeLoadError = error
            } else {
                runtimeMessage = next.message
            }
        } catch {
            runtimeLoadError = error.localizedDescription
        }
    }

    func loadRuntimeLogs(_ binding: ArchitectureRuntimeBinding) async {
        guard busyServiceKey == nil else { return }
        busyServiceKey = "logs/\(binding.profileID)/\(binding.service.id)"
        defer { busyServiceKey = nil }
        do {
            let output = try await engine.stackPilotLogs(profileID: binding.profileID, serviceID: binding.service.id)
            selectedRuntimeLog = StackPilotLogDocument(title: "Logs · \(binding.service.name)", content: output)
        } catch {
            runtimeLoadError = error.localizedDescription
        }
    }

    var matchingProfile: StackPilotProfileStatus? {
        ArchitectureRuntimeLinker.matchingProfile(snapshot: snapshot, workspaceURL: workspaceURL, response: runtimeResponse)
    }

    var matchingProfiles: [StackPilotProfileStatus] {
        guard let runtimeResponse else { return [] }
        let roots = Set((snapshot.workspaceRoots ?? [workspaceURL?.path ?? snapshot.repositoryPath]).map { normalizePath($0) })
        return runtimeResponse.profiles.filter { roots.contains(normalizePath($0.workspaceRoot)) }
    }

    func normalizePath(_ path: String) -> String {
        let value = URL(fileURLWithPath: path).standardizedFileURL.path
        return value == "/" ? value : value.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }

    func runtimeBinding(for project: ProjectNode) -> ArchitectureRuntimeBinding? {
        ArchitectureRuntimeLinker.binding(for: project, snapshot: snapshot, workspaceURL: workspaceURL, response: runtimeResponse)
    }
}
