import Foundation

extension EngineClient {
func httpCatalog(repositoryURL: URL) async throws -> HTTPTestingCatalog {
        let root = repositoryURL.standardizedFileURL
        let data = try await runEngine(arguments: ["http-catalog", root.path])
        do {
            var catalog = try JSONDecoder().decode(HTTPTestingCatalog.self, from: data)
            catalog.endpoints = catalog.endpoints.map { endpoint in
                var copy = endpoint
                copy.workspaceRoot = root.path
                return copy
            }
            catalog.httpCalls = catalog.httpCalls.map { call in
                var copy = call
                copy.workspaceRoot = root.path
                return copy
            }
            catalog.requests = catalog.requests.map { request in
                var copy = request
                copy.workspaceRoot = root.path
                return copy
            }
            return catalog
        } catch {
            throw EngineError.invalidOutput("El catálogo HTTP devolvió JSON no válido: \(error.localizedDescription)")
        }
    }

    func httpCatalog(repositoryURLs: [URL]) async throws -> HTTPTestingCatalog {
        let roots = repositoryURLs.map { $0.standardizedFileURL }
        guard !roots.isEmpty else { throw EngineError.processFailed("Selecciona al menos una carpeta para el catálogo HTTP.") }
        if roots.count == 1, let only = roots.first { return try await httpCatalog(repositoryURL: only) }

        var allEndpoints: [HTTPApiEndpoint] = []
        var allRequests: [HTTPTestRequest] = []
        var allCalls: [HTTPCallTrace] = []
        var warnings: [String] = []

        for root in roots {
            let catalog = try await httpCatalog(repositoryURL: root)
            let prefix = WorkspaceSnapshotMerger.stablePrefix(root.path)
            func projectID(_ id: String) -> String { "\(prefix)::\(id)" }
            func endpointID(_ id: String) -> String { "\(prefix)::endpoint::\(id)" }

            allEndpoints += catalog.endpoints.map { endpoint in
                var copy = endpoint
                copy.id = endpointID(endpoint.id)
                copy.projectId = projectID(endpoint.projectId)
                copy.repositoryPath = "\(WorkspaceSnapshotMerger.label(for: root))/\(endpoint.repositoryPath)"
                copy.workspaceRoot = root.path
                return copy
            }
            allRequests += catalog.requests.map { request in
                var copy = request
                copy.id = "\(prefix)::request::\(request.id)"
                if !request.projectId.isEmpty { copy.projectId = projectID(request.projectId) }
                copy.repositoryPath = "\(WorkspaceSnapshotMerger.label(for: root))/\(request.repositoryPath)"
                copy.workspaceRoot = root.path
                copy.associatedEndpointIds = request.associatedEndpointIds.map(endpointID)
                return copy
            }
            allCalls += catalog.httpCalls.map { call in
                var copy = call
                copy.id = "\(prefix)::http-call::\(call.id)"
                copy.sourceProjectId = projectID(call.sourceProjectId)
                copy.repositoryPath = "\(WorkspaceSnapshotMerger.label(for: root))/\(call.repositoryPath)"
                copy.workspaceRoot = root.path
                copy.targetEndpointIds = call.targetEndpointIds.map(endpointID)
                return copy
            }
            warnings += catalog.warnings.map { "\(WorkspaceSnapshotMerger.label(for: root)): \($0)" }
        }

        // Re-resolve method + route candidates across every selected repository, not just within each individual scan.
        allCalls = allCalls.map { call in
            var copy = call
            let candidates = allEndpoints.filter { endpoint in
                endpoint.projectId != call.sourceProjectId
                    && (endpoint.method.caseInsensitiveCompare(call.method) == .orderedSame || endpoint.method == "ANY")
                    && Self.routesMatch(endpoint.route, call.route)
            }
            copy.targetEndpointIds = candidates.map(\.id)
            copy.confidence = candidates.count == 1 ? "inferred" : candidates.count > 1 ? "ambiguous" : "unresolved"
            copy.detail = candidates.count == 1
                ? "Coincidencia método/ruta entre repositorios seleccionados; no demuestra tráfico HTTP real."
                : candidates.count > 1
                    ? "Varios endpoints de repositorios seleccionados coinciden; confirma BaseAddress, DNS y configuración."
                    : "No se encontró un endpoint receptor coincidente en las carpetas seleccionadas."
            return copy
        }

        return HTTPTestingCatalog(rootPath: roots.map(\.path).joined(separator: ";"), endpoints: allEndpoints,
            requests: allRequests, httpCalls: allCalls, warnings: Array(Set(warnings)).sorted())
    }

    private static func routesMatch(_ first: String, _ second: String) -> Bool {
        func canonical(_ value: String) -> String {
            let lower = value.split(separator: "?").first.map(String.init) ?? value
            let trimmed = lower.hasSuffix("/") && lower.count > 1 ? String(lower.dropLast()) : lower
            let pattern = #"\{[^}]+\}|:[A-Za-z][A-Za-z0-9_]*|\[\.\.\.[^]]+\]"#
            guard let regex = try? NSRegularExpression(pattern: pattern) else { return trimmed.lowercased() }
            let range = NSRange(trimmed.startIndex..., in: trimmed)
            return regex.stringByReplacingMatches(in: trimmed, range: range, withTemplate: "*").lowercased()
        }
        return canonical(first) == canonical(second)
    }

func executeHTTPRequest(method: String, url: String, headersText: String, body: String) async throws -> HTTPExecutionResult {
        let headers = Data(headersText.utf8).base64EncodedString()
        let bodyData = Data(body.utf8).base64EncodedString()
        let data = try await runEngine(arguments: ["http-call", method, url, headers, bodyData])
        do {
            return try JSONDecoder().decode(HTTPExecutionResult.self, from: data)
        } catch {
            throw EngineError.invalidOutput("El ejecutor HTTP devolvió JSON no válido: \(error.localizedDescription)")
        }
    }

    func executeHTTPFile(repositoryURL: URL, request: HTTPTestRequest, environment: String,
                         method: String, url: String, headersText: String, body: String) async throws -> HTTPExecutionResult {
        let overrides = HTTPRequestOverrides(method: method, url: url, headersText: headersText, body: body)
        let data = try JSONEncoder().encode(overrides)
        let encodedOverrides = data.base64EncodedString()
        let response = try await runEngine(arguments: [
            "http-file-run", repositoryURL.path, request.sourcePath, String(request.index), environment, encodedOverrides
        ])
        do {
            return try JSONDecoder().decode(HTTPExecutionResult.self, from: response)
        } catch {
            throw EngineError.invalidOutput("El ejecutor del archivo .http devolvió JSON no válido: \(error.localizedDescription)")
        }
    }
}
