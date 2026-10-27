import Foundation

struct CodeFileNode: Identifiable, Hashable {
    let id: String
    let name: String
    let path: String
    let rootPath: String
    let isDirectory: Bool
    let children: [CodeFileNode]?

    static func loadTree(roots: [URL], maximumNodes: Int = 18_000) -> [CodeFileNode] {
        var remaining = maximumNodes
        return roots.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }.compactMap { root in
            guard FileManager.default.fileExists(atPath: root.path) else { return nil }
            return buildNode(url: root, rootPath: root.path, depth: 0, remaining: &remaining)
        }
    }

    private static let ignoredDirectories: Set<String> = [
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".next", "dist", "build",
        "coverage", ".turbo", ".vercel", "vendor", "Pods", "DerivedData", "TestResults", ".build"
    ]

    private static func buildNode(url: URL, rootPath: String, depth: Int, remaining: inout Int) -> CodeFileNode? {
        guard remaining > 0, depth <= 16 else { return nil }
        remaining -= 1
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) else { return nil }
        let directory = isDirectory.boolValue
        var children: [CodeFileNode] = []
        if directory {
            let name = url.lastPathComponent
            guard !ignoredDirectories.contains(name), !name.hasPrefix(".") || depth == 0 else { return nil }
            let entries = (try? FileManager.default.contentsOfDirectory(
                at: url,
                includingPropertiesForKeys: [.isDirectoryKey, .isRegularFileKey],
                options: [.skipsHiddenFiles]
            )) ?? []
            for entry in entries.sorted(by: {
                let firstDirectory = (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false
                let secondDirectory = (try? $1.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) ?? false
                if firstDirectory != secondDirectory { return firstDirectory }
                return $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending
            }) {
                if remaining <= 0 { break }
                if let child = buildNode(url: entry, rootPath: rootPath, depth: depth + 1, remaining: &remaining) {
                    children.append(child)
                }
            }
        }
        return CodeFileNode(id: url.path, name: url.lastPathComponent, path: url.path,
                            rootPath: rootPath, isDirectory: directory, children: directory ? children : nil)
    }

    func filtered(by query: String) -> CodeFileNode? {
        let normalized = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return self }
        let sourceChildren = children ?? []
        let filteredChildren = sourceChildren.compactMap { $0.filtered(by: normalized) }
        if name.localizedCaseInsensitiveContains(normalized) || path.localizedCaseInsensitiveContains(normalized) {
            return CodeFileNode(id: id, name: name, path: path, rootPath: rootPath,
                                isDirectory: isDirectory, children: children)
        }
        guard isDirectory, !filteredChildren.isEmpty else { return nil }
        return CodeFileNode(id: id, name: name, path: path, rootPath: rootPath,
                            isDirectory: true, children: filteredChildren)
    }
}
