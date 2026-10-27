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

struct EngineClient {
    func scan(repositoryURL: URL) async throws -> ArchitectureSnapshot {
        try await Task.detached(priority: .userInitiated) {
            let engine = try Self.resolveEngineDirectory()
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = [
                "dotnet", "run", "--verbosity", "quiet",
                "--project", engine.appendingPathComponent("CodeAtlas.Engine.csproj").path,
                "--", "scan", repositoryURL.path
            ]
            process.currentDirectoryURL = engine

            let stdout = Pipe()
            let stderr = Pipe()
            process.standardOutput = stdout
            process.standardError = stderr
            try process.run()

            // Read both streams concurrently to avoid blocking if either pipe fills.
            let outputTask = Task.detached { stdout.fileHandleForReading.readDataToEndOfFile() }
            let errorTask = Task.detached { stderr.fileHandleForReading.readDataToEndOfFile() }
            process.waitUntilExit()
            let outputData = await outputTask.value
            let errorData = await errorTask.value

            guard process.terminationStatus == 0 else {
                throw EngineError.processFailed(String(data: errorData, encoding: .utf8) ?? "exit \(process.terminationStatus)")
            }
            do {
                return try JSONDecoder().decode(ArchitectureSnapshot.self, from: outputData)
            } catch {
                throw EngineError.invalidOutput("\(error.localizedDescription). Comprueba que stdout contiene solo JSON válido.")
            }
        }.value
    }

    func scanWorkspace(repositoryURLs: [URL]) async throws -> ArchitectureSnapshot {
        let roots = repositoryURLs.map { $0.standardizedFileURL }
        guard !roots.isEmpty else { throw EngineError.processFailed("Selecciona al menos una carpeta para analizar.") }
        if roots.count == 1, let only = roots.first {
            var snapshot = try await scan(repositoryURL: only)
            snapshot.workspaceRoots = [only.path]
            snapshot.projects = snapshot.projects.map { project in var copy = project; copy.repositoryRoot = only.path; return copy }
            return snapshot
        }
        var entries: [(root: URL, snapshot: ArchitectureSnapshot)] = []
        // Keep scans sequential to avoid several dotnet restore/build processes competing for the same NuGet cache.
        for root in roots {
            entries.append((root: root, snapshot: try await scan(repositoryURL: root)))
        }
        guard let merged = WorkspaceSnapshotMerger.merge(entries) else {
            throw EngineError.invalidOutput("No se pudo combinar el workspace seleccionado.")
        }
        return merged
    }

    func workspaceFingerprint(repositoryURL: URL) async throws -> String {
        try await Task.detached(priority: .utility) {
            let head = try await Self.runGit(["rev-parse", "HEAD"], at: repositoryURL)
            let branch = try await Self.runGit(["branch", "--show-current"], at: repositoryURL)
            let status = try await Self.runGit(["status", "--porcelain=v1", "--untracked-files=all"], at: repositoryURL)
            // Include the actual tracked diff so edits to an already-dirty file are detected too.
            let trackedDiff = try await Self.runGit(["diff", "--binary", "HEAD", "--"], at: repositoryURL)
            let untrackedMetadata = status.split(separator: "\n").compactMap { line -> String? in
                guard line.hasPrefix("?? ") else { return nil }
                let relativePath = String(line.dropFirst(3))
                let url = repositoryURL.appendingPathComponent(relativePath)
                guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else { return relativePath }
                let modified = values.contentModificationDate?.timeIntervalSince1970 ?? 0
                return "\(relativePath)|\(values.fileSize ?? -1)|\(modified)"
            }.joined(separator: "\n")
            let material = [head, branch, status, trackedDiff, untrackedMetadata].joined(separator: "\n")
            // Hasher is intentionally process-local: the fingerprint is only compared during
            // this app session, so a cryptographic persisted hash is unnecessary.
            var hasher = Hasher()
            hasher.combine(material)
            return String(hasher.finalize())
        }.value
    }
}
