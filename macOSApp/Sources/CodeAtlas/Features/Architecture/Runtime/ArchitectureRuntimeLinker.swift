import Foundation

/// Resolves source-code projects to explicitly configured StackPilot services.
/// This only links local project metadata to configured launch entries; it does not infer runtime traffic.
struct ArchitectureRuntimeBinding {
    let profileID: String
    let profileName: String
    let service: StackPilotServiceStatus
}

enum ArchitectureRuntimeLinker {
    static func matchingProfile(
        snapshot: ArchitectureSnapshot,
        workspaceURL: URL?,
        response: StackPilotResponse?
    ) -> StackPilotProfileStatus? {
        guard let response else { return nil }
        let repositoryPath = normalizePath(workspaceURL?.path ?? snapshot.repositoryPath)
        return response.profiles.first { normalizePath($0.workspaceRoot) == repositoryPath }
    }

    static func binding(
        for project: ProjectNode,
        snapshot: ArchitectureSnapshot,
        workspaceURL: URL?,
        response: StackPilotResponse?
    ) -> ArchitectureRuntimeBinding? {
        guard let response else { return nil }
        let rootPath = project.repositoryRoot ?? workspaceURL?.path ?? snapshot.repositoryPath

        // A shared infrastructure node may be discovered from source in one repository
        // while its Compose definition lives in another selected repository. Search all
        // selected profiles, but bind only if exactly one explicit, exact-name match exists.
        if project.kind.caseInsensitiveCompare("Infrastructure") == .orderedSame {
            let matches = response.profiles.compactMap { infrastructureBinding(for: project, profile: $0) }
            return matches.count == 1 ? matches[0] : nil
        }

        let normalizedRoot = normalizePath(rootPath)
        guard let profile = response.profiles.first(where: { normalizePath($0.workspaceRoot) == normalizedRoot }) else { return nil }
        let rootURL = URL(fileURLWithPath: rootPath, isDirectory: true).standardizedFileURL
        let projectFilePath = resolve(project.path, relativeTo: rootURL)
        let projectDirectoryPath = URL(fileURLWithPath: projectFilePath).deletingLastPathComponent().standardizedFileURL.path
        let projectRelativePath = project.path.replacingOccurrences(of: "\\", with: "/").lowercased()
        let projectFileName = URL(fileURLWithPath: project.path).lastPathComponent.lowercased()
        let projectName = project.name.lowercased()

        let candidates: [(service: StackPilotServiceStatus, score: Int)] = profile.services.compactMap { service in
            // Compose entries represent infrastructure, not source projects. They remain visible in StackPilot.
            guard service.kind.lowercased() != "compose" else { return nil }

            let workingDirectory = resolve(service.workingDirectory, relativeTo: rootURL)
            let command = service.command.replacingOccurrences(of: "\\", with: "/").lowercased()
            var score = 0

            if project.kind == "Dotnet" && command.contains(projectRelativePath) {
                score = 100
            } else if workingDirectory == projectDirectoryPath {
                score = 90
            } else if project.kind == "Dotnet" && command.contains(projectFileName) {
                score = 70
            } else if service.name.lowercased() == projectName {
                score = 50
            }

            return score > 0 ? (service, score) : nil
        }
        .sorted { $0.score > $1.score }

        guard let best = candidates.first else { return nil }
        // If two configured services match equally, do not guess which one controls this project.
        if candidates.dropFirst().first?.score == best.score { return nil }
        return ArchitectureRuntimeBinding(profileID: profile.id, profileName: profile.name, service: best.service)
    }


    private static func infrastructureBinding(
        for project: ProjectNode,
        profile: StackPilotProfileStatus
    ) -> ArchitectureRuntimeBinding? {
        let key = project.id.split(separator: ":").last.map(String.init) ?? normalizeIdentifier(project.name)
        let aliases = infrastructureAliases[key] ?? [normalizeIdentifier(project.name)]
        let candidates: [(service: StackPilotServiceStatus, score: Int)] = profile.services.compactMap { service in
            guard service.kind.caseInsensitiveCompare("compose") == .orderedSame else { return nil }
            let id = normalizeIdentifier(service.id)
            let name = normalizeIdentifier(service.name)
            let composeName = composeServiceName(from: service.command).map(normalizeIdentifier) ?? ""
            let normalizedAliases = Set(aliases.map(normalizeIdentifier))
            if normalizedAliases.contains(id) || normalizedAliases.contains(composeName) {
                return (service, 100)
            }
            if normalizedAliases.contains(name) {
                return (service, 90)
            }
            return nil
        }.sorted { lhs, rhs in
            if lhs.score != rhs.score { return lhs.score > rhs.score }
            return lhs.service.id.localizedCaseInsensitiveCompare(rhs.service.id) == .orderedAscending
        }

        guard let best = candidates.first else { return nil }
        if candidates.dropFirst().first?.score == best.score { return nil }
        return ArchitectureRuntimeBinding(profileID: profile.id, profileName: profile.name, service: best.service)
    }

    private static let infrastructureAliases: [String: [String]] = [
        "rabbitmq": ["rabbitmq", "rabbit-mq", "rabbit_mq"],
        "minio": ["minio"],
        "redis": ["redis"],
        "sqlserver": ["sqlserver", "sql-server", "mssql"],
        "postgresql": ["postgresql", "postgres", "postgres-db"],
        "mongodb": ["mongodb", "mongo"],
        "meilisearch": ["meilisearch"],
        "kafka": ["kafka"],
        "elasticsearch": ["elasticsearch"]
    ]

    private static func composeServiceName(from command: String) -> String? {
        guard let separator = command.range(of: "·", options: .backwards) else { return nil }
        let value = command[separator.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    private static func normalizeIdentifier(_ value: String) -> String {
        value.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    private static func resolve(_ path: String, relativeTo rootURL: URL) -> String {
        URL(fileURLWithPath: path, relativeTo: rootURL).standardizedFileURL.path
    }

    private static func normalizePath(_ path: String) -> String {
        let normalized = URL(fileURLWithPath: path).standardizedFileURL.path
        return normalized == "/" ? normalized : normalized.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }
}
