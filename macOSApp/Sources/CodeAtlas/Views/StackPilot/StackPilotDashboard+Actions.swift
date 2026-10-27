import SwiftUI
import AppKit

extension StackPilotDashboard {
func refresh() async {
        guard !isLoading else { return }
        isLoading = true
        defer { isLoading = false }
        do {
            response = try await engine.stackPilot(arguments: ["status"])
            if let error = response?.error { errorMessage = error }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func perform(_ arguments: [String]) async {
        guard busyAction == nil else { return }
        busyAction = arguments.joined(separator: " ")
        isLoading = true
        defer { busyAction = nil; isLoading = false }
        do {
            let next = try await engine.stackPilot(arguments: arguments)
            response = next
            if let error = next.error { errorMessage = error }
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func showLogs(profile: StackPilotProfileStatus, service: StackPilotServiceStatus) async {
        busyAction = "logs-\(service.id)"
        defer { busyAction = nil }
        do {
            let output = try await engine.stackPilotLogs(profileID: profile.id, serviceID: service.id)
            selectedLog = StackPilotLogDocument(title: "Logs · \(service.name)", content: output)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func chooseWorkspaceAndInitialize() {
        let panel = NSOpenPanel()
        panel.title = "Selecciona las carpetas del workspace"
        panel.message = "Puedes seleccionar varios repositorios. Las plantillas de ejecución se crearán desactivadas y deberás revisar los comandos."
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        if panel.runModal() == .OK, !panel.urls.isEmpty {
            Task { await initializeWorkspaces(panel.urls) }
        }
    }

    func addCurrentOrSelectedWorkspace() {
        if !workspaceURLs.isEmpty {
            Task { await initializeWorkspaces(workspaceURLs) }
        } else {
            chooseWorkspaceAndInitialize()
        }
    }

    func initializeWorkspaces(_ urls: [URL]) async {
        let roots = urls.map { $0.standardizedFileURL }
        for root in roots {
            await perform(["init", "--workspace", root.path])
        }
        await refresh()
    }


    func initializeWorkspacesIfMissing() async {
        await refresh()
        let existingRoots = Set((response?.profiles ?? []).compactMap { profile in
            URL(fileURLWithPath: profile.workspaceRoot, isDirectory: true).standardizedFileURL.path
        })
        for root in workspaceURLs.map({ $0.standardizedFileURL }) where !existingRoots.contains(root.path) {
            await perform(["init", "--workspace", root.path])
        }
        await refresh()
    }

    func openConfiguration(_ path: String) {
        let url = URL(fileURLWithPath: path)
        guard FileManager.default.fileExists(atPath: path) else {
            errorMessage = "No existe el archivo de configuración en \(path)."
            return
        }
        NSWorkspace.shared.open(url)
    }
}
