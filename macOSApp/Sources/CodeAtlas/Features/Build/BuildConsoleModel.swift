import Foundation
import Combine

enum BuildConsoleTask: String, CaseIterable, Identifiable, Hashable {
    case dotnetBuild
    case dotnetTest
    case dotnetFormat
    case npmBuild
    case npmTest
    case npmLint
    case swiftBuild
    case swiftTest

    var id: String { rawValue }
    var title: String {
        switch self {
        case .dotnetBuild: return ".NET · Compilar"
        case .dotnetTest: return ".NET · Tests"
        case .dotnetFormat: return ".NET · Formatear"
        case .npmBuild: return "Node · Build"
        case .npmTest: return "Node · Tests"
        case .npmLint: return "Node · Lint"
        case .swiftBuild: return "Swift · Build"
        case .swiftTest: return "Swift · Tests"
        }
    }

    static func available(for root: URL) -> [BuildConsoleTask] {
        var tasks: [BuildConsoleTask] = []
        if findDotnetTarget(root: root) != nil {
            tasks += [.dotnetBuild, .dotnetTest, .dotnetFormat]
        }
        if let packageURL = packageJSON(in: root),
           let data = try? Data(contentsOf: packageURL),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let scripts = json["scripts"] as? [String: Any] {
            if scripts["build"] != nil { tasks.append(.npmBuild) }
            if scripts["test"] != nil { tasks.append(.npmTest) }
            if scripts["lint"] != nil { tasks.append(.npmLint) }
        }
        if FileManager.default.fileExists(atPath: root.appendingPathComponent("Package.swift").path) {
            tasks += [.swiftBuild, .swiftTest]
        }
        return tasks
    }

    func command(for root: URL) throws -> (executable: String, arguments: [String], workingDirectory: URL, display: String) {
        switch self {
        case .dotnetBuild, .dotnetTest, .dotnetFormat:
            guard let target = Self.findDotnetTarget(root: root) else {
                throw BuildConsoleError("No se encontró una solución .sln/.slnx o un proyecto .csproj en esta raíz.")
            }
            let verb: String
            switch self {
            case .dotnetBuild: verb = "build"
            case .dotnetTest: verb = "test"
            default: verb = "format"
            }
            let args = [verb, target.path]
            return ("dotnet", args, root, "dotnet " + args.joined(separator: " "))
        case .npmBuild, .npmTest, .npmLint:
            guard let package = Self.packageJSON(in: root) else {
                throw BuildConsoleError("No se encontró package.json en la raíz seleccionada.")
            }
            let script = self == .npmBuild ? "build" : self == .npmTest ? "test" : "lint"
            let args = ["run", script]
            return ("npm", args, package.deletingLastPathComponent(), "npm " + args.joined(separator: " ") + " · \(package.deletingLastPathComponent().path)")
        case .swiftBuild, .swiftTest:
            guard FileManager.default.fileExists(atPath: root.appendingPathComponent("Package.swift").path) else {
                throw BuildConsoleError("No se encontró Package.swift en la raíz seleccionada.")
            }
            let args = [self == .swiftBuild ? "build" : "test"]
            return ("swift", args, root, "swift " + args.joined(separator: " "))
        }
    }

    private static func findDotnetTarget(root: URL) -> URL? {
        let fm = FileManager.default
        let immediate = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        if let solution = immediate.filter({ ["sln", "slnx"].contains($0.pathExtension.lowercased()) }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).first {
            return solution
        }
        if let project = immediate.filter({ $0.pathExtension.lowercased() == "csproj" }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).first {
            return project
        }
        for directory in immediate where (try? directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
            if [".git", "node_modules", "bin", "obj", "dist", "build"].contains(directory.lastPathComponent) { continue }
            let children = (try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
            if let solution = children.filter({ ["sln", "slnx"].contains($0.pathExtension.lowercased()) }).first { return solution }
            if let project = children.filter({ $0.pathExtension.lowercased() == "csproj" }).sorted(by: { $0.lastPathComponent < $1.lastPathComponent }).first { return project }
        }
        return nil
    }

    private static func packageJSON(in root: URL) -> URL? {
        let direct = root.appendingPathComponent("package.json")
        if FileManager.default.fileExists(atPath: direct.path) { return direct }
        let entries = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        for directory in entries where (try? directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
            if [".git", "node_modules", "dist", "build", ".next"].contains(directory.lastPathComponent) { continue }
            let candidate = directory.appendingPathComponent("package.json")
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
        }
        return nil
    }
}

struct BuildConsoleError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

@MainActor
final class BuildConsoleModel: ObservableObject {
    @Published private(set) var outputText = ""
    @Published private(set) var isRunning = false
    @Published private(set) var statusText = "Listo"
    @Published private(set) var currentCommand = ""
    @Published private(set) var exitCode: Int32?
    private var process: Process?
    private var outputPipe: Pipe?
    private var didRequestCancellation = false
    private var outputDecoder = UTF8StreamDecoder()

    func start(task: BuildConsoleTask, root: URL) {
        guard !isRunning else { return }
        do {
            let specification = try task.command(for: root)
            outputText = "$ cd \(specification.workingDirectory.path)\n$ \(specification.display)\n\n"
            currentCommand = specification.display
            statusText = "Ejecutando…"
            exitCode = nil
            didRequestCancellation = false
            outputDecoder = UTF8StreamDecoder()

            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            process.arguments = [specification.executable] + specification.arguments
            process.currentDirectoryURL = specification.workingDirectory
            var environment = ProcessInfo.processInfo.environment
            let standardPaths = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
            environment["PATH"] = (standardPaths + [environment["PATH"] ?? ""]).joined(separator: ":")
            process.environment = environment

            let pipe = Pipe()
            process.standardOutput = pipe
            process.standardError = pipe
            outputPipe = pipe
            pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                if data.isEmpty {
                    handle.readabilityHandler = nil
                    Task { @MainActor [weak self] in
                        guard let self else { return }
                        self.append(self.outputDecoder.finish())
                    }
                    return
                }
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    self.append(self.outputDecoder.append(data))
                }
            }
            process.terminationHandler = { [weak self] child in
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    self.isRunning = false
                    self.exitCode = child.terminationStatus
                    self.statusText = self.didRequestCancellation ? "Cancelado" : (child.terminationStatus == 0 ? "Finalizado correctamente" : "Falló · código \(child.terminationStatus)")
                    self.process = nil
                    self.outputPipe?.fileHandleForReading.readabilityHandler = nil
                    try? self.outputPipe?.fileHandleForReading.close()
                    self.outputPipe = nil
                    self.append("\n\n[CodeAtlas] \(self.statusText).\n")
                }
            }
            try process.run()
            self.process = process
            isRunning = true
        } catch {
            isRunning = false
            statusText = "No se pudo iniciar"
            append("\n[Error] \(error.localizedDescription)\n")
        }
    }

    func cancel() {
        guard isRunning, let process else { return }
        didRequestCancellation = true
        statusText = "Cancelando…"
        process.terminate()
    }

    func clear() {
        guard !isRunning else { return }
        outputText = ""
        currentCommand = ""
        exitCode = nil
        statusText = "Listo"
    }

    private func append(_ chunk: String) {
        outputText += chunk
        let maximumCharacters = 900_000
        if outputText.count > maximumCharacters {
            outputText = "[Salida anterior truncada para limitar memoria]\n" + String(outputText.suffix(maximumCharacters))
        }
    }
}
