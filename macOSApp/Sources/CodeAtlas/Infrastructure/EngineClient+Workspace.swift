import Foundation

extension EngineClient {
func workspaceFingerprint(repositoryURLs: [URL]) async throws -> String {
        let roots = repositoryURLs.map { $0.standardizedFileURL }.sorted { $0.path < $1.path }
        var hasher = Hasher()
        for root in roots {
            hasher.combine(root.path)
            hasher.combine(try await workspaceFingerprint(repositoryURL: root))
        }
        return String(hasher.finalize())
    }
}
