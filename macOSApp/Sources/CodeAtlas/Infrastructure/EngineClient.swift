import Foundation

enum EngineError: LocalizedError {
    case engineNotFound(String)
    case processFailed(String)
    case invalidOutput(String)
    case gitFailed(String)

    var errorDescription: String? {
        switch self {
        case .engineNotFound(let path): return "No se encontró el motor .NET en: \(path)"
        case .processFailed(let message): return "El análisis falló: \(message)"
        case .invalidOutput(let message): return "La salida del motor no es válida: \(message)"
        case .gitFailed(let message): return "No se pudo consultar Git: \(message)"
        }
    }
}

struct WorkspaceScanProgress: Sendable {
    let phase: String
    let completedRoots: Int
    let totalRoots: Int
    let rootName: String

    var fraction: Double {
        guard totalRoots > 0 else { return 0 }
        return min(1, max(0, Double(completedRoots) / Double(totalRoots)))
    }

    var message: String {
        switch phase {
        case "preparing": return "Preparando el análisis del workspace…"
        case "scanning": return "Analizando \(rootName) · repositorio \(min(completedRoots + 1, max(totalRoots, 1))) de \(totalRoots)"
        case "merging": return "Combinando proyectos, relaciones y flujos…"
        case "completed": return "Análisis del workspace completado"
        case "cancelling": return "Cancelando el análisis y cerrando el proceso actual…"
        default: return phase
        }
    }
}

struct EngineClient {
    func scan(repositoryURL: URL) async throws -> ArchitectureSnapshot {
        let output = try await runEngine(arguments: ["scan", repositoryURL.standardizedFileURL.path])
        do {
            // Decode large architecture snapshots away from the main actor so graph construction
            // and JSON parsing do not freeze the editor while a workspace is being rescanned.
            return try await Task.detached(priority: .userInitiated) {
                try JSONDecoder().decode(ArchitectureSnapshot.self, from: output)
            }.value
        } catch {
            if error is CancellationError { throw error }
            throw EngineError.invalidOutput("\(error.localizedDescription). Comprueba que stdout contiene solo JSON válido.")
        }
    }

    func scanWorkspace(
        repositoryURLs: [URL],
        onProgress: (@Sendable (WorkspaceScanProgress) -> Void)? = nil
    ) async throws -> ArchitectureSnapshot {
        let roots = repositoryURLs.map { $0.standardizedFileURL }
        guard !roots.isEmpty else { throw EngineError.processFailed("Selecciona al menos una carpeta para analizar.") }
        try Task.checkCancellation()
        onProgress?(WorkspaceScanProgress(phase: "preparing", completedRoots: 0, totalRoots: roots.count, rootName: ""))
        if roots.count == 1, let only = roots.first {
            try Task.checkCancellation()
            onProgress?(WorkspaceScanProgress(phase: "scanning", completedRoots: 0, totalRoots: 1, rootName: only.lastPathComponent))
            var snapshot = try await scan(repositoryURL: only)
            try Task.checkCancellation()
            snapshot.workspaceRoots = [only.path]
            snapshot.projects = snapshot.projects.map { project in var copy = project; copy.repositoryRoot = only.path; return copy }
            onProgress?(WorkspaceScanProgress(phase: "completed", completedRoots: 1, totalRoots: 1, rootName: only.lastPathComponent))
            return snapshot
        }
        var entries: [(root: URL, snapshot: ArchitectureSnapshot)] = []
        // Keep scans sequential to avoid several dotnet restore/build processes competing for the same NuGet cache.
        for (index, root) in roots.enumerated() {
            try Task.checkCancellation()
            onProgress?(WorkspaceScanProgress(phase: "scanning", completedRoots: index, totalRoots: roots.count, rootName: root.lastPathComponent))
            entries.append((root: root, snapshot: try await scan(repositoryURL: root)))
            try Task.checkCancellation()
            onProgress?(WorkspaceScanProgress(phase: "scanning", completedRoots: index + 1, totalRoots: roots.count, rootName: root.lastPathComponent))
        }
        onProgress?(WorkspaceScanProgress(phase: "merging", completedRoots: roots.count, totalRoots: roots.count, rootName: ""))
        let merged = await Task.detached(priority: .userInitiated) {
            WorkspaceSnapshotMerger.merge(entries)
        }.value
        try Task.checkCancellation()
        guard let merged else {
            throw EngineError.invalidOutput("No se pudo combinar el workspace seleccionado.")
        }
        onProgress?(WorkspaceScanProgress(phase: "completed", completedRoots: roots.count, totalRoots: roots.count, rootName: ""))
        return merged
    }

    func workspaceFingerprint(repositoryURL: URL) async throws -> String {
        let root = repositoryURL.standardizedFileURL
        try Task.checkCancellation()
        let head = try await Self.runGit(["rev-parse", "HEAD"], at: root)
        try Task.checkCancellation()
        let branch = try await Self.runGit(["branch", "--show-current"], at: root)
        try Task.checkCancellation()
        let status = try await Self.runGit(["status", "--porcelain=v1", "--untracked-files=all", "-z"], at: root)

        // Only hash changed tracked paths, not a giant `git diff --binary` string. This keeps
        // memory bounded for repositories with large assets while still noticing edits to a
        // file which was already dirty when the previous fingerprint was captured.
        let changedPaths = try await Self.runGit(["diff", "--name-only", "-z", "HEAD", "--"], at: root)
            .split(separator: "\0").map(String.init)
        let changedContent = await Task.detached(priority: .utility) {
            var fingerprints: [String] = []
            for path in changedPaths.sorted() {
                let fileURL = root.appendingPathComponent(path)
                if let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]),
                   values.isRegularFile == true,
                   let size = values.fileSize,
                   size <= 32 * 1024 * 1024,
                   let data = try? Data(contentsOf: fileURL, options: [.mappedIfSafe]) {
                    var fileHasher = Hasher()
                    fileHasher.combine(data)
                    fingerprints.append("\(path)|\(size)|\(fileHasher.finalize())")
                } else {
                    let values = try? fileURL.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
                    fingerprints.append("\(path)|\(values?.fileSize ?? -1)|\((values?.contentModificationDate?.timeIntervalSince1970) ?? 0)")
                }
            }
            return fingerprints
        }.value
        let untrackedMetadata = status.split(separator: "\0").compactMap { line -> String? in
            guard line.hasPrefix("?? ") else { return nil }
            let relativePath = String(line.dropFirst(3))
            let url = root.appendingPathComponent(relativePath)
            guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else { return relativePath }
            let modified = values.contentModificationDate?.timeIntervalSince1970 ?? 0
            return "\(relativePath)|\(values.fileSize ?? -1)|\(modified)"
        }.joined(separator: "\n")
        let material = [head, branch, status, changedContent.joined(separator: "\n"), untrackedMetadata].joined(separator: "\n")
        var hasher = Hasher()
        hasher.combine(material)
        return String(hasher.finalize())
    }

}
