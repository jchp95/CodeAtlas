import Foundation

/// Persistent cache for expensive repository scans. Cached snapshots are valid only
/// when the stable source fingerprint and analyzer schema both match.
actor WorkspaceSnapshotCache {
    static let shared = WorkspaceSnapshotCache()
    private static let schemaVersion = 19

    private struct Entry: Codable {
        let schemaVersion: Int
        let rootPath: String
        let fingerprint: String
        let savedAt: Date
        let snapshot: ArchitectureSnapshot
    }

    private let fileManager = FileManager.default
    private let directoryURL: URL

    init() {
        let base = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support", isDirectory: true)
        directoryURL = base.appendingPathComponent("CodeAtlas/SnapshotCache", isDirectory: true)
    }

    func load(rootPath: String, fingerprint: String) -> ArchitectureSnapshot? {
        let url = cacheURL(rootPath: rootPath)
        guard let data = try? Data(contentsOf: url),
              let entry = try? JSONDecoder().decode(Entry.self, from: data),
              entry.schemaVersion == Self.schemaVersion,
              entry.rootPath == rootPath,
              entry.fingerprint == fingerprint else { return nil }
        return entry.snapshot
    }

    func save(_ snapshot: ArchitectureSnapshot, rootPath: String, fingerprint: String) {
        do {
            try fileManager.createDirectory(at: directoryURL, withIntermediateDirectories: true, attributes: nil)
            let entry = Entry(schemaVersion: Self.schemaVersion, rootPath: rootPath, fingerprint: fingerprint,
                              savedAt: Date(), snapshot: snapshot)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys]
            let data = try encoder.encode(entry)
            try data.write(to: cacheURL(rootPath: rootPath), options: [.atomic])
            pruneCacheIfNeeded(maximumBytes: 256 * 1024 * 1024)
        } catch {
            // Cache failures must never prevent a source scan or change its result.
        }
    }

    func remove(rootPath: String) {
        try? fileManager.removeItem(at: cacheURL(rootPath: rootPath))
    }

    func clear() {
        try? fileManager.removeItem(at: directoryURL)
    }

    private func pruneCacheIfNeeded(maximumBytes: Int) {
        guard let urls = try? fileManager.contentsOfDirectory(at: directoryURL, includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey], options: [.skipsHiddenFiles]) else { return }
        let records = urls.filter { $0.pathExtension == "json" }.compactMap { url -> (url: URL, size: Int, modified: Date)? in
            guard let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]) else { return nil }
            return (url, values.fileSize ?? 0, values.contentModificationDate ?? .distantPast)
        }.sorted { $0.modified < $1.modified }
        var total = records.reduce(0) { $0 + $1.size }
        for record in records where total > maximumBytes {
            do {
                try fileManager.removeItem(at: record.url)
                total -= record.size
            } catch {
                // A cache entry that cannot be removed must not block scanning.
            }
        }
    }

    private func cacheURL(rootPath: String) -> URL {
        let key = EngineClient.stableFingerprint(Data(rootPath.utf8))
        return directoryURL.appendingPathComponent("\(key).json", isDirectory: false)
    }
}
