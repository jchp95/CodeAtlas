import Foundation
import Combine
import Darwin

/// Small, loopback-only HTTP bridge for the companion VS Code extension.
/// The secret is stored with owner-only permissions in Application Support.
final class VSCodeBridgeService: ObservableObject {
    static let shared = VSCodeBridgeService()

    static let port: UInt16 = 38_471
    static let configurationFileName = "vscode-bridge.json"

    @Published private(set) var isRunning = false
    @Published private(set) var statusMessage = "Puente VS Code sin iniciar"
    @Published private(set) var lastRequestAt: Date?

    private let lock = NSLock()
    private var serverFD: Int32 = -1
    private var bridgeToken = ""
    private var started = false

    private init() {}

    var configurationURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library", isDirectory: true)
            .appendingPathComponent("Application Support", isDirectory: true)
            .appendingPathComponent("CodeAtlas", isDirectory: true)
            .appendingPathComponent(Self.configurationFileName, isDirectory: false)
    }

    func start() {
        lock.lock()
        guard !started else {
            lock.unlock()
            return
        }
        started = true
        lock.unlock()

        do {
            try prepareTokenConfiguration()
            let fd = Darwin.socket(AF_INET, SOCK_STREAM, 0)
            guard fd >= 0 else { throw bridgeError("No se pudo crear el socket local (\(errno)).") }

            var noSigPipe: Int32 = 1
            _ = setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
            var reuseAddress: Int32 = 1
            _ = setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuseAddress, socklen_t(MemoryLayout<Int32>.size))

            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = Self.port.bigEndian
            address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))

            let bindResult = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress in
                    Darwin.bind(fd, socketAddress, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            guard bindResult == 0 else {
                let code = errno
                Darwin.close(fd)
                throw bridgeError(code == EADDRINUSE
                    ? "El puerto local \(Self.port) ya está ocupado. Cierra la otra instancia de CodeAtlas."
                    : "No se pudo escuchar en 127.0.0.1:\(Self.port) (\(code)).")
            }
            guard Darwin.listen(fd, 8) == 0 else {
                let code = errno
                Darwin.close(fd)
                throw bridgeError("No se pudo iniciar el listener local (\(code)).")
            }

            lock.lock()
            serverFD = fd
            lock.unlock()
            DispatchQueue.main.async {
                self.isRunning = true
                self.statusMessage = "VS Code conectado mediante loopback"
            }
            DispatchQueue.global(qos: .utility).async { [weak self] in
                self?.acceptLoop(fd: fd)
            }
        } catch {
            DispatchQueue.main.async {
                self.isRunning = false
                self.statusMessage = error.localizedDescription
            }
        }
    }

    private func prepareTokenConfiguration() throws {
        let directory = configurationURL.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        if let data = try? Data(contentsOf: configurationURL),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let token = object["token"] as? String, token.count >= 48,
           let port = object["port"] as? Int, port == Int(Self.port) {
            bridgeToken = token
            _ = Darwin.chmod(configurationURL.path, mode_t(S_IRUSR | S_IWUSR))
            return
        }

        let token = (0..<32).map { _ in String(format: "%02x", Int.random(in: 0...255)) }.joined()
        let object: [String: Any] = [
            "schemaVersion": 1,
            "port": Int(Self.port),
            "token": token,
            "bindAddress": "127.0.0.1"
        ]
        let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: configurationURL, options: .atomic)
        guard Darwin.chmod(configurationURL.path, mode_t(S_IRUSR | S_IWUSR)) == 0 else {
            throw bridgeError("No se pudieron aplicar permisos privados al token del puente VS Code.")
        }
        bridgeToken = token
    }

    private func acceptLoop(fd: Int32) {
        while true {
            let clientFD = Darwin.accept(fd, nil, nil)
            if clientFD < 0 {
                if errno == EINTR { continue }
                return
            }
            DispatchQueue.global(qos: .utility).async { [weak self] in
                self?.handleConnection(clientFD: clientFD)
            }
        }
    }

    private func handleConnection(clientFD: Int32) {
        defer { Darwin.close(clientFD) }
        guard let request = readRequest(fd: clientFD) else {
            writeResponse(fd: clientFD, status: 400, payload: ["ok": false, "error": "Invalid HTTP request"])
            return
        }

        guard request.headers["x-codeatlas-token"] == bridgeToken else {
            writeResponse(fd: clientFD, status: 401, payload: ["ok": false, "error": "Unauthorized"])
            return
        }

        switch (request.method, request.path) {
        case ("GET", "/v1/status"):
            writeResponse(fd: clientFD, status: 200, payload: [
                "ok": true,
                "service": "CodeAtlas VS Code bridge",
                "schemaVersion": 1,
                "version": "0.16.0"
            ])
        case ("POST", "/v1/open"):
            handleOpen(request.body, clientFD: clientFD)
        case ("POST", "/v1/open-workspace"):
            handleOpenWorkspace(request.body, clientFD: clientFD)
        default:
            writeResponse(fd: clientFD, status: 404, payload: ["ok": false, "error": "Endpoint not found"])
        }
    }

    private func handleOpen(_ body: Data, clientFD: Int32) {
        guard let object = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any],
              let path = object["path"] as? String, path.hasPrefix("/"),
              FileManager.default.fileExists(atPath: path) else {
            writeResponse(fd: clientFD, status: 422, payload: ["ok": false, "error": "An existing absolute file path is required"])
            return
        }

        let fileURL = URL(fileURLWithPath: path).standardizedFileURL
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: fileURL.path, isDirectory: &isDirectory), !isDirectory.boolValue else {
            writeResponse(fd: clientFD, status: 422, payload: ["ok": false, "error": "The path is not a file"])
            return
        }
        let suppliedRoot = object["workspaceRoot"] as? String
        let root = suppliedRoot.flatMap { $0.hasPrefix("/") ? URL(fileURLWithPath: $0).standardizedFileURL.path : nil }
        let line = max(1, (object["line"] as? Int) ?? 1)
        let filePath = fileURL.path
        DispatchQueue.main.async {
            SourceNavigator.open(filePath, root: root, line: line)
            self.lastRequestAt = Date()
        }
        writeResponse(fd: clientFD, status: 202, payload: ["ok": true, "accepted": true])
    }

    private func handleOpenWorkspace(_ body: Data, clientFD: Int32) {
        guard let object = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any],
              let path = object["path"] as? String, path.hasPrefix("/"),
              FileManager.default.fileExists(atPath: path) else {
            writeResponse(fd: clientFD, status: 422, payload: ["ok": false, "error": "An existing absolute workspace path is required"])
            return
        }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else {
            writeResponse(fd: clientFD, status: 422, payload: ["ok": false, "error": "The path is not a directory"])
            return
        }
        let standardizedPath = URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL.path
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: .codeAtlasOpenWorkspace, object: nil, userInfo: ["path": standardizedPath])
            self.lastRequestAt = Date()
        }
        writeResponse(fd: clientFD, status: 202, payload: ["ok": true, "accepted": true])
    }

    private struct HTTPRequest {
        let method: String
        let path: String
        let headers: [String: String]
        let body: Data
    }

    private func readRequest(fd: Int32) -> HTTPRequest? {
        var bytes: [UInt8] = []
        var chunk = [UInt8](repeating: 0, count: 4096)
        let maxBytes = 65_536
        var headerEnd: Int?
        var requiredSize: Int?

        while bytes.count < maxBytes {
            let received = chunk.withUnsafeMutableBytes { rawBuffer in
                Darwin.recv(fd, rawBuffer.baseAddress, rawBuffer.count, 0)
            }
            guard received > 0 else { return nil }
            bytes.append(contentsOf: chunk[0..<received])

            if headerEnd == nil, let end = headerBoundary(in: bytes) {
                headerEnd = end
                let headerText = String(decoding: bytes[0..<end], as: UTF8.self)
                let contentLength = headerText.components(separatedBy: "\r\n").dropFirst().first { $0.lowercased().hasPrefix("content-length:") }
                    .flatMap { Int($0.dropFirst("content-length:".count).trimmingCharacters(in: .whitespaces)) } ?? 0
                guard contentLength >= 0, contentLength <= 48_000 else { return nil }
                requiredSize = end + 4 + contentLength
            }
            if let requiredSize, bytes.count >= requiredSize { break }
        }

        guard let headerEnd, let requiredSize, bytes.count >= requiredSize else { return nil }
        let headerText = String(decoding: bytes[0..<headerEnd], as: UTF8.self)
        let lines = headerText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { return nil }
        let parts = requestLine.split(separator: " ")
        guard parts.count >= 2 else { return nil }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let separator = line.firstIndex(of: ":") else { continue }
            headers[String(line[..<separator]).trimmingCharacters(in: .whitespaces).lowercased()] = String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
        }
        let bodyStart = headerEnd + 4
        let body = Data(bytes[bodyStart..<requiredSize])
        return HTTPRequest(method: String(parts[0]).uppercased(), path: String(parts[1]), headers: headers, body: body)
    }

    private func headerBoundary(in bytes: [UInt8]) -> Int? {
        guard bytes.count >= 4 else { return nil }
        for index in 0...(bytes.count - 4) {
            if bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10 {
                return index
            }
        }
        return nil
    }

    private func writeResponse(fd: Int32, status: Int, payload: [String: Any]) {
        let body = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)
        let reason: String
        switch status {
        case 200: reason = "OK"
        case 202: reason = "Accepted"
        case 400: reason = "Bad Request"
        case 401: reason = "Unauthorized"
        case 404: reason = "Not Found"
        case 422: reason = "Unprocessable Entity"
        default: reason = "Internal Server Error"
        }
        var response = Data("HTTP/1.1 \(status) \(reason)\r\nContent-Type: application/json; charset=utf-8\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Length: \(body.count)\r\n\r\n".utf8)
        response.append(body)
        response.withUnsafeBytes { raw in
            guard let baseAddress = raw.baseAddress else { return }
            var totalSent = 0
            while totalSent < raw.count {
                let sent = Darwin.send(fd, baseAddress.advanced(by: totalSent), raw.count - totalSent, 0)
                if sent <= 0 { break }
                totalSent += sent
            }
        }
    }

    private func bridgeError(_ description: String) -> NSError {
        NSError(domain: "CodeAtlas.VSCodeBridge", code: 1, userInfo: [NSLocalizedDescriptionKey: description])
    }
}
