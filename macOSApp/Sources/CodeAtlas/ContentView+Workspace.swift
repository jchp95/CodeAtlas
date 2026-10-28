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
        startRescan()
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
        startRescan()
    }

    @MainActor func startRescan() {
        let roots = repositoryURLs.map { $0.standardizedFileURL }
        guard !roots.isEmpty else { return }
        let scanID = UUID()
        let previousTask = workspaceScanTask
        previousTask?.cancel()
        activeScanID = scanID
        isCancellingScan = false
        isScanning = true
        scanProgress = WorkspaceScanProgress(phase: "preparing", completedRoots: 0, totalRoots: roots.count, rootName: "")

        // Wait until the previous process has actually stopped before starting another scan.
        // This prevents duplicate dotnet processes from competing over the NuGet cache.
        workspaceScanTask = Task { @MainActor in
            if let previousTask { await previousTask.value }
            guard !Task.isCancelled else {
                finishScanIfCurrent(scanID)
                return
            }
            await rescan(scanID: scanID, roots: roots)
        }
    }

    @MainActor func cancelWorkspaceScan() {
        guard isScanning, !isCancellingScan else { return }
        isCancellingScan = true
        if let progress = scanProgress {
            scanProgress = WorkspaceScanProgress(
                phase: "cancelling",
                completedRoots: progress.completedRoots,
                totalRoots: progress.totalRoots,
                rootName: progress.rootName
            )
        }
        workspaceScanTask?.cancel()
    }

    @MainActor private func rescan(scanID: UUID, roots: [URL]) async {
        guard activeScanID == scanID else { return }
        repositoryURL = roots.first
        do {
            try Task.checkCancellation()
            let result = try await engine.scanWorkspace(repositoryURLs: roots) { progress in
                Task { @MainActor in
                    guard self.activeScanID == scanID, !self.isCancellingScan else { return }
                    self.scanProgress = progress
                }
            }
            try Task.checkCancellation()
            guard activeScanID == scanID else { return }
            guard roots.map(\.path) == repositoryURLs.map({ $0.standardizedFileURL.path }) else {
                finishScanIfCurrent(scanID)
                return
            }
            snapshot = result
            if let selectedProjectID, !result.projects.contains(where: { $0.id == selectedProjectID }) { self.selectedProjectID = nil }
            lastFingerprint = try? await engine.workspaceFingerprint(repositoryURLs: roots)
            try Task.checkCancellation()
            guard activeScanID == scanID else { return }
            if selectedSection == "Endpoints HTTP" { await refreshHTTPCatalog() }
        } catch is CancellationError {
            // Cancellation is a normal user action. Do not surface it as an analysis error.
        } catch {
            if activeScanID == scanID { errorMessage = error.localizedDescription }
        }
        finishScanIfCurrent(scanID)
    }

    @MainActor private func finishScanIfCurrent(_ scanID: UUID) {
        guard activeScanID == scanID else { return }
        isScanning = false
        isCancellingScan = false
        scanProgress = nil
        activeScanID = nil
        workspaceScanTask = nil
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
                startRescan()
            }
        }
    }

    func icon(for kind: String) -> String {
        switch kind { case "Dotnet": return "curlybraces"; case "Next.js": return "globe"; case "Node.js": return "shippingbox"; default: return "folder" }
    }
    func statusColor(_ status: String) -> Color { status.contains("?") || status == "A" ? .green : status == "D" ? .red : .orange }
}
