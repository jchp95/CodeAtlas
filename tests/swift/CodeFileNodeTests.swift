import Foundation

@main
struct CodeFileNodeTests {
    static func main() throws {
        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory.appendingPathComponent("CodeAtlasTreeTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? fileManager.removeItem(at: root) }
        let sourceDirectory = root.appendingPathComponent("src/Features", isDirectory: true)
        let ignoredDirectory = root.appendingPathComponent("node_modules/package", isDirectory: true)
        let gitDirectory = root.appendingPathComponent(".git/objects", isDirectory: true)
        try fileManager.createDirectory(at: sourceDirectory, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: ignoredDirectory, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: gitDirectory, withIntermediateDirectories: true)
        try Data("class CargoService {}".utf8).write(to: sourceDirectory.appendingPathComponent("CargoService.cs"))
        try Data("ignored".utf8).write(to: ignoredDirectory.appendingPathComponent("package.js"))
        try Data("git".utf8).write(to: gitDirectory.appendingPathComponent("config"))
        try Data("readme".utf8).write(to: root.appendingPathComponent("README.md"))

        let roots = CodeFileNode.loadTree(roots: [root])
        guard roots.count == 1, roots[0].isDirectory else { fail("workspace root was not represented") }
        let allPaths = flatten(roots).map(\.path)
        guard allPaths.contains(where: { $0.hasSuffix("CargoService.cs") }) else { fail("source file was not discovered") }
        guard allPaths.contains(where: { $0.hasSuffix("README.md") }) else { fail("root README was not discovered") }
        guard !allPaths.contains(where: { $0.contains("node_modules") }) else { fail("node_modules was not excluded") }
        guard !allPaths.contains(where: { $0.contains("/.git/") }) else { fail(".git content was not excluded") }
        guard let filtered = roots[0].filtered(by: "CargoService"), flatten([filtered]).contains(where: { $0.name == "CargoService.cs" }) else {
            fail("file-name filtering failed")
        }
        guard roots[0].filtered(by: "does-not-exist") == nil else { fail("empty search should have no result") }
        print("Code file tree tests passed: workspace roots, nested source files, ignored directories, and search filtering.")
    }

    private static func flatten(_ nodes: [CodeFileNode]) -> [CodeFileNode] {
        nodes.flatMap { node in [node] + flatten(node.children ?? []) }
    }

    private static func fail(_ message: String) -> Never {
        fputs("Code file tree test failed: \(message)\n", stderr)
        exit(1)
    }
}
