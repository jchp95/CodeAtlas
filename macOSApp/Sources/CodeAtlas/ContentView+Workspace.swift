import SwiftUI
import AppKit

extension ContentView {
func chooseRepository(addToWorkspace: Bool) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = true
        panel.prompt = addToWorkspace ? "Añadir carpetas" : "Analizar carpetas"
        panel.message = "Selecciona una o varias carpetas raíz de repositorios o microservicios."
        guard panel.runModal() == .OK else { return }
        let picked = panel.urls.map { $0.standardizedFileURL }
        let combined = addToWorkspace ? repositoryURLs + picked : picked
        repositoryURLs = normalizedRepositoryRoots(combined)
        repositoryURL = repositoryURLs.first
        selectedSection = "Resumen"
        selectedProjectID = nil
        snapshot = nil
        httpCatalog = nil
        lastFingerprint = nil
        Task { await rescan() }
    }

    func normalizedRepositoryRoots(_ urls: [URL]) -> [URL] {
        var unique: [URL] = []
        for url in urls.map({ $0.standardizedFileURL }) where !unique.contains(where: { $0.path == url.path }) {
            // A parent already selected contains its child projects; don't analyze the same files twice.
            if unique.contains(where: { url.path.hasPrefix($0.path.hasSuffix("/") ? $0.path : $0.path + "/") }) { continue }
            unique.removeAll { existing in existing.path.hasPrefix(url.path.hasSuffix("/") ? url.path : url.path + "/") }
            unique.append(url)
        }
        return unique
    }

    func removeRepository(_ root: URL) {
        repositoryURLs.removeAll { $0.standardizedFileURL.path == root.standardizedFileURL.path }
        repositoryURL = repositoryURLs.first
        snapshot = nil
        httpCatalog = nil
        lastFingerprint = nil
        selectedProjectID = nil
        if repositoryURLs.isEmpty { selectedSection = "Resumen"; return }
        Task { await rescan() }
    }

    @MainActor func rescan() async {
        guard !repositoryURLs.isEmpty else { return }
        repositoryURL = repositoryURLs.first
        isScanning = true
        defer { isScanning = false }
        do {
            let result = try await engine.scanWorkspace(repositoryURLs: repositoryURLs)
            snapshot = result
            if let selectedProjectID, !result.projects.contains(where: { $0.id == selectedProjectID }) { self.selectedProjectID = nil }
            lastFingerprint = try? await engine.workspaceFingerprint(repositoryURLs: repositoryURLs)
            if selectedSection == "Endpoints HTTP" { await refreshHTTPCatalog() }
        } catch { errorMessage = error.localizedDescription }
    }

    @MainActor func refreshHTTPCatalog() async {
        guard !repositoryURLs.isEmpty else { httpCatalog = nil; return }
        isLoadingHTTPCatalog = true
        defer { isLoadingHTTPCatalog = false }
        do {
            httpCatalog = try await engine.httpCatalog(repositoryURLs: repositoryURLs)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func watchRepositoryLoop() async {
        while !Task.isCancelled {
            try? await Task.sleep(nanoseconds: 15_000_000_000)
            guard watchingRepository, !repositoryURLs.isEmpty, !isScanning else { continue }
            guard let fingerprint = try? await engine.workspaceFingerprint(repositoryURLs: repositoryURLs) else { continue }
            if let lastFingerprint, fingerprint != lastFingerprint {
                await rescan()
            }
        }
    }

    func icon(for kind: String) -> String {
        switch kind { case "Dotnet": return "curlybraces"; case "Next.js": return "globe"; case "Node.js": return "shippingbox"; default: return "folder" }
    }
    func statusColor(_ status: String) -> Color { status.contains("?") || status == "A" ? .green : status == "D" ? .red : .orange }
}
