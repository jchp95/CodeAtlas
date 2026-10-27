import Foundation

struct FormatterCommand {
    let executable: String
    let arguments: [String]
    let workingDirectory: URL
    let display: String
}

enum CodeFormatter {
    static func command(for file: URL, workspaceRoot: URL) throws -> FormatterCommand {
        let extensionName = file.pathExtension.lowercased()
        switch extensionName {
        case "cs":
            guard let target = findDotnetTarget(root: workspaceRoot) else {
                throw BuildConsoleError("No se encontró una solución .NET para aplicar dotnet format.")
            }
            let targetRoot = target.deletingLastPathComponent()
            let relativePath = relativePath(of: file, to: targetRoot)
            let args = ["format", "whitespace", target.path, "--include", relativePath]
            return FormatterCommand(executable: "dotnet", arguments: args, workingDirectory: targetRoot,
                                    display: "dotnet " + args.joined(separator: " "))
        case "swift":
            let args = ["format", "--in-place", file.path]
            return FormatterCommand(executable: "swift-format", arguments: args, workingDirectory: workspaceRoot,
                                    display: "swift-format " + args.joined(separator: " "))
        case "sh", "bash", "zsh":
            let args = ["-w", file.path]
            return FormatterCommand(executable: "shfmt", arguments: args, workingDirectory: workspaceRoot,
                                    display: "shfmt " + args.joined(separator: " "))
        case "js", "jsx", "mjs", "cjs", "ts", "tsx", "json", "jsonc", "md", "mdx", "yml", "yaml", "html", "css", "scss":
            let packageRoot = nearestPackageRoot(for: file, workspaceRoot: workspaceRoot) ?? workspaceRoot
            let path = relativePath(of: file, to: packageRoot)
            let args = ["--no-install", "prettier", "--write", path]
            return FormatterCommand(executable: "npx", arguments: args, workingDirectory: packageRoot,
                                    display: "npx " + args.joined(separator: " "))
        default:
            throw BuildConsoleError("No hay un formateador configurado para .\(extensionName). Configura una herramienta para esta extensión.")
        }
    }

    private static func nearestPackageRoot(for file: URL, workspaceRoot: URL) -> URL? {
        let root = workspaceRoot.standardizedFileURL.path
        var cursor = file.deletingLastPathComponent().standardizedFileURL
        while cursor.path == root || cursor.path.hasPrefix(root + "/") {
            if FileManager.default.fileExists(atPath: cursor.appendingPathComponent("package.json").path) { return cursor }
            if cursor.path == root { break }
            let parent = cursor.deletingLastPathComponent()
            if parent.path == cursor.path { break }
            cursor = parent
        }
        return nil
    }

    private static func findDotnetTarget(root: URL) -> URL? {
        let manager = FileManager.default
        let entries = (try? manager.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        if let solution = entries.filter({ ["sln", "slnx"].contains($0.pathExtension.lowercased()) }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).first { return solution }
        if let project = entries.filter({ $0.pathExtension.lowercased() == "csproj" }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).first { return project }
        for directory in entries where (try? directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
            if [".git", "node_modules", "bin", "obj", "dist", "build"].contains(directory.lastPathComponent) { continue }
            let nested = (try? manager.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
            if let solution = nested.filter({ ["sln", "slnx"].contains($0.pathExtension.lowercased()) }).first { return solution }
            if let project = nested.filter({ $0.pathExtension.lowercased() == "csproj" }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).first { return project }
        }
        return nil
    }

    private static func relativePath(of file: URL, to root: URL) -> String {
        let rootPath = root.standardizedFileURL.path
        let filePath = file.standardizedFileURL.path
        guard filePath.hasPrefix(rootPath + "/") else { return filePath }
        return String(filePath.dropFirst(rootPath.count + 1))
    }
}
