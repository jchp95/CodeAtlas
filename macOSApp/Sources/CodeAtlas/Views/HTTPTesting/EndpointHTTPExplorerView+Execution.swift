import SwiftUI
import AppKit

extension EndpointHTTPExplorerView {
var responseSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Respuesta").font(.headline)
                Spacer()
                if let result {
                    if result.success {
                        Text("\(result.statusCode) \(result.reasonPhrase) · \(result.durationMs) ms")
                            .font(.caption.bold()).foregroundStyle(result.statusCode < 400 ? .green : .red)
                            .padding(.horizontal, 8).padding(.vertical, 5)
                            .background((result.statusCode < 400 ? Color.green : Color.red).opacity(0.12), in: Capsule())
                    } else {
                        Text("Error de conexión").font(.caption.bold()).foregroundStyle(.red)
                    }
                }
            }
            if let result {
                if let error = result.error {
                    Text(error).font(.callout).foregroundStyle(.red).textSelection(.enabled)
                }
                if !result.headers.isEmpty {
                    DisclosureGroup("Cabeceras de respuesta (\(result.headers.count))") {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(result.headers.keys.sorted(), id: \.self) { key in
                                Text("\(key): \(result.headers[key] ?? "")")
                                    .font(.system(.caption2, design: .monospaced)).textSelection(.enabled)
                            }
                        }.padding(.vertical, 6)
                    }.font(.caption)
                }
                if !result.body.isEmpty {
                    ScrollView([.vertical, .horizontal]) {
                        Text(result.body).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading).padding(10)
                    }.frame(minHeight: 90, maxHeight: 280)
                        .background(.black.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
                        .overlay(RoundedRectangle(cornerRadius: 8).stroke(.quaternary))
                    if result.bodyTruncated {
                        Label("Respuesta truncada a 512 KiB para proteger la memoria de la aplicación.", systemImage: "exclamationmark.triangle")
                            .font(.caption2).foregroundStyle(.orange)
                    }
                } else if result.success {
                    Text("El servidor respondió sin cuerpo.").font(.caption).foregroundStyle(.secondary)
                }
                HStack {
                    Text(result.url).font(.caption2.monospaced()).foregroundStyle(.secondary).lineLimit(2)
                    Spacer()
                    Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(result.body, forType: .string) } label: {
                        Label("Copiar cuerpo", systemImage: "doc.on.doc")
                    }.buttonStyle(.link).disabled(result.body.isEmpty)
                }
            } else {
                Text("La respuesta HTTP aparecerá aquí. Las operaciones POST, PUT, PATCH y DELETE pueden modificar datos del entorno; verifica la URL antes de ejecutarlas.")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(14).frame(maxWidth: .infinity, alignment: .leading)
                    .atlasGlassCard(cornerRadius: 8, shadow: false)
            }
        }
    }

    func executeButton(title: String) -> some View {
        HStack {
            Button {
                Task { await executeCurrent() }
            } label: {
                if isExecuting { ProgressView().controlSize(.small); Text("Ejecutando…") }
                else { Label(title, systemImage: "paperplane.fill") }
            }
            .buttonStyle(.borderedProminent).disabled(isExecuting || requestURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            if let localError {
                Text(localError).font(.caption).foregroundStyle(.red).lineLimit(2)
            }
            Spacer()
        }
    }

    var requestRepositoryKeys: [String] { Array(Set(filteredRequests.map(\.repositoryPath))).sorted() }
    var callRepositoryKeys: [String] { Array(Set(filteredCalls.map(\.repositoryPath))).sorted() }
    var endpointRepositoryKeys: [String] { Array(Set(filteredEndpoints.map(\.repositoryPath))).sorted() }
    func displayRepo(_ repo: String) -> String { repo == "." ? "Raíz" : repo }

    func methodBadge(_ value: String) -> some View {
        Text(value.uppercased()).font(.system(size: 10, weight: .bold, design: .rounded))
            .foregroundStyle(methodColor(value)).padding(.horizontal, 6).padding(.vertical, 4)
            .background(methodColor(value).opacity(0.13), in: RoundedRectangle(cornerRadius: 5))
    }

    func methodColor(_ value: String) -> Color {
        switch value.uppercased() {
        case "GET": return .green
        case "POST": return .blue
        case "PUT", "PATCH": return .orange
        case "DELETE": return .red
        default: return .secondary
        }
    }

    func schemaLabel(_ value: String) -> String {
        switch value {
        case "source-type": return "Tipo del código fuente"
        case "type-unresolved": return "Tipo localizado; esquema incompleto"
        default: return "Sin esquema inferible"
        }
    }

    func selectFirstIfNeeded() {
        guard selectedID == nil else { return }
        if let request = catalog?.requests.first { selectRequest(request) }
        else if let call = catalog?.httpCalls.first { selectHTTPCall(call) }
        else if let endpoint = catalog?.endpoints.first { selectEndpoint(endpoint) }
    }

    func validateSelection() {
        guard let selectedID else { selectFirstIfNeeded(); return }
        let exists: Bool
        if selectedID.hasPrefix("endpoint:") {
            exists = selectedEndpoint != nil
        } else if selectedID.hasPrefix("request:") {
            exists = selectedRequest != nil
        } else if selectedID.hasPrefix("call:") {
            exists = selectedHTTPCall != nil
        } else { exists = false }
        if !exists { self.selectedID = nil; result = nil; selectFirstIfNeeded() }
    }

    func selectHTTPCall(_ call: HTTPCallTrace) {
        selectedID = "call:\(call.id)"
        result = nil; localError = nil
    }

    func selectEndpoint(_ endpoint: HTTPApiEndpoint) {
        selectedID = "endpoint:\(endpoint.id)"
        method = endpoint.method == "ANY" ? "GET" : endpoint.method
        requestURL = baseURL(for: endpoint).trimmingCharacters(in: CharacterSet(charactersIn: "/")) + endpoint.route
        headersText = endpoint.sampleBodyJson == nil ? "Accept: application/json" : "Accept: application/json\nContent-Type: application/json"
        bodyText = endpoint.sampleBodyJson ?? ""
        selectedEnvironment = ""
        result = nil; localError = nil
    }

    func selectRequest(_ request: HTTPTestRequest) {
        selectedID = "request:\(request.id)"
        method = request.method == "ANY" ? "GET" : request.method
        requestURL = request.url
        headersText = request.headersText
        bodyText = request.body
        selectedEnvironment = request.availableEnvironments.count == 1 ? request.availableEnvironments[0] : ""
        result = nil; localError = nil
    }

    func baseURL(for endpoint: HTTPApiEndpoint) -> String {
        for address in endpoint.listenAddresses {
            var value = address.trimmingCharacters(in: .whitespacesAndNewlines)
            guard value.hasPrefix("http://") || value.hasPrefix("https://") else { continue }
            value = value.replacingOccurrences(of: "0.0.0.0", with: "localhost")
                .replacingOccurrences(of: "*", with: "localhost")
                .replacingOccurrences(of: "+", with: "localhost")
            if URLComponents(string: value)?.host != nil { return value.trimmingCharacters(in: CharacterSet(charactersIn: "/")) }
        }
        return endpoint.framework.hasPrefix("Next.js") ? "http://localhost:3000" : "http://localhost:5000"
    }

    @MainActor func executeCurrent() async {
        guard let selectedID else { return }
        isExecuting = true; defer { isExecuting = false }
        result = nil; localError = nil
        do {
            if let request = selectedRequest {
                result = try await onFileRun(request, selectedEnvironment, method, requestURL, headersText, bodyText)
            } else {
                result = try await onDirectRun(method, requestURL, headersText, bodyText)
            }
        } catch { localError = error.localizedDescription }
    }

    func formatJSONBody() {
        guard let data = bodyText.data(using: .utf8), let object = try? JSONSerialization.jsonObject(with: data),
              let pretty = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]),
              let text = String(data: pretty, encoding: .utf8) else { return }
        bodyText = text
    }

    func saveGeneratedHTTP(_ endpoint: HTTPApiEndpoint) {
        let panel = NSSavePanel()
        panel.title = "Crear prueba HTTP"
        panel.prompt = "Guardar .http"
        panel.nameFieldStringValue = "\(safeFileName(endpoint.projectName))-\(safeFileName(endpoint.method.lowercased()))-\(safeFileName(endpoint.route.replacingOccurrences(of: "/", with: "-"))).http"
        panel.canCreateDirectories = true
        panel.directoryURL = endpoint.workspaceRoot.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? repositoryURL
        guard panel.runModal() == .OK, let chosenURL = panel.url else { return }
        let targetURL = chosenURL.pathExtension.lowercased() == "http" ? chosenURL : chosenURL.appendingPathExtension("http")
        let contentTypePresent = headersText.split(separator: "\n").contains { $0.lowercased().hasPrefix("content-type:") }
        var lines = ["# Generated by CodeAtlas. Source: \(endpoint.sourcePath):\(endpoint.line)", "### \(method) \(endpoint.route)", "\(method) \(requestURL)"]
        lines.append(contentsOf: headersText.split(separator: "\n", omittingEmptySubsequences: false).map(String.init).filter { !$0.isEmpty })
        if !bodyText.isEmpty && !contentTypePresent { lines.append("Content-Type: application/json") }
        if !bodyText.isEmpty { lines.append(""); lines.append(bodyText) }
        do {
            try (lines.joined(separator: "\n") + "\n").write(to: targetURL, atomically: true, encoding: .utf8)
            onReload()
        } catch { localError = "No se pudo guardar el archivo .http: \(error.localizedDescription)" }
    }

    func openSourceFile(_ relativePath: String, line: Int = 1, workspaceRoot: String? = nil) {
        let root = workspaceRoot ?? repositoryURL?.path
        SourceNavigator.open(relativePath, root: root, line: line)
    }

    func openHTTPFile(_ request: HTTPTestRequest) {
        openSourceFile(request.sourcePath, line: request.line, workspaceRoot: request.workspaceRoot)
    }

    func safeFileName(_ value: String) -> String {
        let clean = value.replacingOccurrences(of: "[^A-Za-z0-9_-]+", with: "-", options: .regularExpression)
            .trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return clean.isEmpty ? "endpoint" : clean
    }
}
