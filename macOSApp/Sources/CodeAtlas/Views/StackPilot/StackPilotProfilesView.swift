import SwiftUI
import AppKit

struct StackPilotProfilesView: View {
    let workspaceURLs: [URL]
    @State private var response: StackPilotResponse?
    @State private var isLoading = false
    @State private var busyAction: String?
    @State private var errorMessage: String?
    @State private var selectedLog: StackPilotLogDocument?
    private let engine = EngineClient()

    private var profiles: [StackPilotProfileStatus] { response?.profiles ?? [] }
    private var totalServices: Int { profiles.reduce(0) { $0 + $1.services.count } }
    private var runningServices: Int { profiles.reduce(0) { total, profile in total + profile.services.filter { $0.status == "running" }.count } }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                header
                if isLoading && response == nil {
                    ProgressView("Cargando perfiles…").frame(maxWidth: .infinity, minHeight: 220)
                } else if let response, !response.configured {
                    emptyState(response.message ?? "Crea un perfil para administrar los servicios de este workspace.")
                } else if profiles.isEmpty {
                    emptyState("No hay perfiles configurados. Añade una carpeta raíz para crear una plantilla inicial, que se mantendrá desactivada hasta que la revises.")
                } else {
                    HStack(spacing: 12) {
                        summaryCard(title: "Perfiles", value: "\(profiles.count)", detail: "Workspaces registrados", icon: "square.stack.3d.up")
                        summaryCard(title: "Servicios activos", value: "\(runningServices)/\(totalServices)", detail: "Procesos en ejecución", icon: "server.rack")
                        summaryCard(title: "Deshabilitados", value: "\(profiles.reduce(0) { $0 + $1.services.filter { !$0.enabled }.count })", detail: "No se iniciarán automáticamente", icon: "shield.lefthalf.filled")
                    }
                    ForEach(profiles) { profile in profileCard(profile) }
                    if let response, let message = response.message, !message.isEmpty {
                        Label(message, systemImage: "info.circle").font(.callout).foregroundStyle(.secondary)
                    }
                }
            }
            .padding(24)
            .frame(maxWidth: 1450, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
        .task { await refresh() }
        .onChange(of: workspaceURLs.map(\.path).joined(separator: "|")) { _, _ in Task { await refresh() } }
        .alert("StackPilot · Perfiles", isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })) {
            Button("Aceptar", role: .cancel) { errorMessage = nil }
        } message: { Text(errorMessage ?? "Error desconocido") }
        .sheet(item: $selectedLog) { document in
            VStack(alignment: .leading, spacing: 12) {
                HStack { Text(document.title).font(.headline); Spacer(); Button("Copiar") { NSPasteboard.general.setString(document.content, forType: .string) }; Button("Cerrar") { selectedLog = nil } }
                Divider()
                ScrollView { Text(document.content.isEmpty ? "No hay salida de logs." : document.content).font(.system(.caption, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12) }
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            }.padding(18).frame(minWidth: 680, minHeight: 440)
        }
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Perfiles de ejecución").font(.system(.largeTitle, design: .rounded).bold())
                Text("Agrupa APIs, workers y dependencias por workspace. Los servicios se generan desactivados y requieren revisión antes de ejecutarse.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button { chooseWorkspace() } label: { Label("Añadir perfil", systemImage: "plus") }
                .buttonStyle(.borderedProminent).disabled(isLoading || busyAction != nil)
            Button { Task { await refresh() } } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.bordered).disabled(isLoading || busyAction != nil).help("Actualizar perfiles")
        }
    }

    private func summaryCard(title: String, value: String, detail: String, icon: String) -> some View {
        HStack(spacing: 12) {
            Image(systemName: icon).font(.title2).foregroundStyle(.secondary).frame(width: 40, height: 40).background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 3) {
                Text(value).font(.system(.title2, design: .rounded).bold())
                Text(title).font(.callout.weight(.semibold))
                Text(detail).font(.caption2).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }.padding(14).frame(maxWidth: .infinity, alignment: .leading).atlasGlassCard(cornerRadius: 12)
    }

    private func profileCard(_ profile: StackPilotProfileStatus) -> some View {
        VStack(alignment: .leading, spacing: 13) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "square.stack.3d.up.fill").font(.title2).foregroundStyle(.secondary).frame(width: 42, height: 42).background(.quaternary, in: RoundedRectangle(cornerRadius: 11))
                VStack(alignment: .leading, spacing: 4) {
                    Text(profile.name).font(.headline)
                    Text(profile.workspaceRoot).font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(2)
                    Text("\(profile.services.count) servicio(s) · \(profile.services.filter { $0.enabled }.count) habilitado(s)")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button { openConfiguration() } label: { Label("JSON", systemImage: "doc.text") }.buttonStyle(.bordered)
                Button { Task { await perform(["start-profile", profile.id]) } } label: { Label("Iniciar", systemImage: "play.fill") }
                    .buttonStyle(.borderedProminent).disabled(isLoading || busyAction != nil || profile.services.allSatisfy { !$0.enabled })
                Button { Task { await perform(["stop-profile", profile.id]) } } label: { Label("Detener", systemImage: "stop.fill") }
                    .buttonStyle(.bordered).disabled(isLoading || busyAction != nil || profile.services.allSatisfy { $0.status != "running" })
            }
            Divider()
            if profile.services.isEmpty {
                ContentUnavailableView("Sin servicios detectados", systemImage: "shippingbox", description: Text("Inspecciona la configuración o añade servicios manualmente a la plantilla.")).frame(minHeight: 80)
            } else {
                ForEach(profile.services) { service in
                    HStack(spacing: 10) {
                        Circle().fill(service.status == "running" ? .green : service.status == "unavailable" ? .red : .secondary).frame(width: 8, height: 8)
                        VStack(alignment: .leading, spacing: 3) {
                            HStack(spacing: 8) { Text(service.name).font(.callout.weight(.semibold)); Text(service.status.uppercased()).font(.caption2.weight(.bold)).foregroundStyle(service.status == "running" ? .green : .secondary); if !service.enabled { Text("DESHABILITADO").font(.caption2).foregroundStyle(.secondary) } }
                            Text(service.command).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                            Text(service.workingDirectory).font(.caption2.monospaced()).foregroundStyle(.tertiary).lineLimit(1)
                        }
                        Spacer(minLength: 8)
                        Button { Task { await perform([service.enabled ? "disable" : "enable", profile.id, service.id]) } } label: { Label(service.enabled ? "Deshabilitar" : "Habilitar", systemImage: service.enabled ? "pause.circle" : "checkmark.circle") }
                            .buttonStyle(.borderless).disabled(isLoading || busyAction != nil || (service.enabled && service.status == "running"))
                        Button { Task { await perform(["start", profile.id, service.id]) } } label: { Image(systemName: "play.fill") }.buttonStyle(.bordered).disabled(isLoading || busyAction != nil || !service.enabled || service.status == "running")
                        Button { Task { await perform(["stop", profile.id, service.id]) } } label: { Image(systemName: "stop.fill") }.buttonStyle(.bordered).disabled(isLoading || busyAction != nil || service.status != "running")
                        Button { Task { await loadLogs(profile: profile, service: service) } } label: { Image(systemName: "text.alignleft") }.buttonStyle(.bordered).help("Últimos logs").disabled(isLoading || busyAction != nil)
                    }.padding(.vertical, 7)
                    if service.id != profile.services.last?.id { Divider() }
                }
            }
        }.padding(16).atlasGlassCard(cornerRadius: 14)
    }

    private func emptyState(_ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Perfiles todavía no configurados", systemImage: "square.stack.3d.up").font(.title3.bold())
            Text(detail).foregroundStyle(.secondary)
            Button { chooseWorkspace() } label: { Label("Seleccionar workspace…", systemImage: "folder.badge.plus") }.buttonStyle(.borderedProminent)
        }.frame(maxWidth: .infinity, alignment: .leading).padding(24).atlasGlassCard(cornerRadius: 14)
    }

    private func refresh() async {
        isLoading = true; defer { isLoading = false }
        do { response = try await engine.stackPilot(arguments: ["profiles"]); if let error = response?.error { errorMessage = error } }
        catch { errorMessage = error.localizedDescription }
    }

    private func perform(_ arguments: [String]) async {
        guard busyAction == nil else { return }
        busyAction = arguments.joined(separator: " "); isLoading = true
        defer { busyAction = nil; isLoading = false }
        do { response = try await engine.stackPilot(arguments: arguments); if let error = response?.error { errorMessage = error } }
        catch { errorMessage = error.localizedDescription }
        do { response = try await engine.stackPilot(arguments: ["profiles"]) } catch { errorMessage = error.localizedDescription }
    }

    private func loadLogs(profile: StackPilotProfileStatus, service: StackPilotServiceStatus) async {
        do { selectedLog = StackPilotLogDocument(title: "Logs · \(service.name)", content: try await engine.stackPilotLogs(profileID: profile.id, serviceID: service.id)) }
        catch { errorMessage = error.localizedDescription }
    }

    private func chooseWorkspace() {
        let panel = NSOpenPanel(); panel.title = "Añadir repositorios al entorno local"; panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = true
        guard panel.runModal() == .OK, !panel.urls.isEmpty else { return }
        Task {
            isLoading = true; defer { isLoading = false }
            for url in panel.urls {
                do { response = try await engine.stackPilot(arguments: ["init", "--workspace", url.standardizedFileURL.path]) }
                catch { errorMessage = error.localizedDescription }
            }
            await refresh()
        }
    }

    private func openConfiguration() {
        guard let path = response?.configurationPath, FileManager.default.fileExists(atPath: path) else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }
}
