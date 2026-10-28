import SwiftUI
import AppKit

extension EndpointHTTPExplorerView {
var sidebar: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Buscar endpoint, repo o .http…", text: $searchText).textFieldStyle(.plain)
                if !searchText.isEmpty {
                    Button { searchText = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                        .buttonStyle(.plain)
                }
                Button(action: onReload) { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.plain).help("Volver a analizar endpoints y archivos .http")
            }
            .padding(12)
            Rectangle().fill(Color.primary.opacity(0.06)).frame(height: 1)
            if isLoading && catalog == nil {
                ProgressView("Analizando endpoints…").frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let catalog {
                List {
                    if filteredRequests.isEmpty {
                        Section("Pruebas .http") {
                            Text("No hay archivos .http o .rest detectados.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    } else {
                        ForEach(requestRepositoryKeys, id: \.self) { repo in
                            Section("Pruebas .http · \(displayRepo(repo))") {
                                ForEach(filteredRequests.filter { $0.repositoryPath == repo }) { request in
                                    requestRow(request)
                                }
                            }
                        }
                    }
                    if filteredCalls.isEmpty {
                        Section("Llamadas HTTP salientes") {
                            Text("No se detectaron llamadas HTTP con método y ruta estáticos.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    } else {
                        ForEach(callRepositoryKeys, id: \.self) { repo in
                            Section("Llamadas HTTP · \(displayRepo(repo))") {
                                ForEach(filteredCalls.filter { $0.repositoryPath == repo }) { call in
                                    callRow(call)
                                }
                            }
                        }
                    }
                    if filteredEndpoints.isEmpty {
                        Section("Endpoints del código") {
                            Text("No hay endpoints que coincidan con la búsqueda.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    } else {
                        ForEach(endpointRepositoryKeys, id: \.self) { repo in
                            Section("Endpoints · \(displayRepo(repo))") {
                                ForEach(filteredEndpoints.filter { $0.repositoryPath == repo }) { endpoint in
                                    endpointRow(endpoint)
                                }
                            }
                        }
                    }
                }
                .listStyle(.sidebar)
                .scrollContentBackground(.hidden)
                .background(.ultraThinMaterial)
                if !catalog.warnings.isEmpty {
                    Divider()
                    VStack(alignment: .leading, spacing: 7) {
                        HStack(spacing: 6) {
                            Label("Diagnóstico del catálogo", systemImage: "exclamationmark.bubble")
                                .font(.caption.bold())
                            Spacer(minLength: 4)
                            Text("\(catalog.warnings.count)")
                                .font(.caption2.monospacedDigit().weight(.semibold))
                                .foregroundStyle(.secondary)
                        }
                        ScrollView {
                            VStack(alignment: .leading, spacing: 8) {
                                ForEach(Array(catalog.warnings.enumerated()), id: \.offset) { _, warning in
                                    HStack(alignment: .top, spacing: 7) {
                                        Image(systemName: warningIcon(for: warning))
                                            .font(.caption2)
                                            .foregroundStyle(warningColor(for: warning))
                                            .frame(width: 12)
                                            .padding(.top, 1)
                                        Text(warning)
                                            .font(.caption2)
                                            .foregroundStyle(.secondary)
                                            .textSelection(.enabled)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .frame(maxHeight: 150)
                    }
                    .padding(10)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .accessibilityElement(children: .contain)
                    .accessibilityLabel("Diagnóstico del catálogo HTTP")
                }
            } else {
                ContentUnavailableView("Selecciona un workspace", systemImage: "folder.badge.questionmark",
                    description: Text("Abre una solución .NET, una aplicación Next.js o la carpeta de repositorios de tus microservicios."))
            }
        }
        .background(.ultraThinMaterial)
    }

    var detail: some View {
        Group {
            if let endpoint = selectedEndpoint {
                endpointDetail(endpoint)
            } else if let request = selectedRequest {
                requestDetail(request)
            } else if let call = selectedHTTPCall {
                httpCallDetail(call)
            } else {
                ContentUnavailableView("Selecciona una petición o endpoint", systemImage: "arrow.left.arrow.right",
                    description: Text("Los archivos .http asociados aparecen primero. También puedes generar una petición a partir del endpoint detectado."))
            }
        }
    }

    func warningIcon(for warning: String) -> String {
        let value = warning.lowercased()
        if value.contains("no se pudo") || value.contains("error") || value.contains("falló") { return "xmark.octagon.fill" }
        if value.contains("dinámic") || value.contains("no se detectaron") || value.contains("no se encontraron")
            || value.contains("no se pudieron") || value.contains("ambigu") { return "exclamationmark.triangle.fill" }
        return "info.circle.fill"
    }

    func warningColor(for warning: String) -> Color {
        let icon = warningIcon(for: warning)
        if icon == "xmark.octagon.fill" { return .red }
        if icon == "exclamationmark.triangle.fill" { return .orange }
        return .secondary
    }

    func requestRow(_ request: HTTPTestRequest) -> some View {
        let selected = selectedID == "request:\(request.id)"
        let associatedCount = request.associatedEndpointIds.count
        return Button { selectRequest(request) } label: {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    methodBadge(request.method)
                    Text(request.name).font(.subheadline.weight(.medium)).lineLimit(1)
                    Spacer(minLength: 0)
                    if associatedCount > 0 { Image(systemName: "link").foregroundStyle(.green).help("Asociada a \(associatedCount) endpoint(s)") }
                }
                Text(request.url).font(.system(.caption2, design: .monospaced)).foregroundStyle(.secondary).lineLimit(2)
                Text("\(request.projectName) · \(request.sourcePath):\(request.line)")
                    .font(.caption2).foregroundStyle(.tertiary).lineLimit(1)
            }
            .padding(.vertical, 4).contentShape(Rectangle())
            .background(selected ? Color.accentColor.opacity(0.12) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
    }

    func callRow(_ call: HTTPCallTrace) -> some View {
        let selected = selectedID == "call:\(call.id)"
        let targetCount = call.targetEndpointIds.count
        return Button { selectHTTPCall(call) } label: {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    methodBadge(call.method)
                    Text(call.route).font(.system(.subheadline, design: .monospaced, weight: .medium)).lineLimit(2)
                    Spacer(minLength: 0)
                    if targetCount == 1 {
                        Image(systemName: "arrow.right.circle.fill").foregroundStyle(.green).help("Destino candidato encontrado")
                    } else if targetCount > 1 {
                        Text("\(targetCount) destinos").font(.caption2.bold()).foregroundStyle(.orange)
                    } else {
                        Image(systemName: "questionmark.circle").foregroundStyle(.secondary).help("Destino no resuelto")
                    }
                }
                Text("\(call.sourceProjectName) · \(call.symbol)")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                Text("\(call.sourcePath):\(call.line)")
                    .font(.caption2.monospaced()).foregroundStyle(.tertiary).lineLimit(1)
            }
            .padding(.vertical, 4).contentShape(Rectangle())
            .background(selected ? Color.accentColor.opacity(0.12) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
    }

    func endpointRow(_ endpoint: HTTPApiEndpoint) -> some View {
        let selected = selectedID == "endpoint:\(endpoint.id)"
        let linkedCount = (catalog?.requests ?? []).filter { $0.associatedEndpointIds.contains(endpoint.id) }.count
        return Button { selectEndpoint(endpoint) } label: {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    methodBadge(endpoint.method)
                    Text(endpoint.route).font(.system(.subheadline, design: .monospaced, weight: .medium)).lineLimit(2)
                    Spacer(minLength: 0)
                    if linkedCount > 0 {
                        Text("\(linkedCount) .http").font(.caption2.bold()).foregroundStyle(.green)
                    }
                }
                Text("\(endpoint.projectName) · \(endpoint.sourcePath):\(endpoint.line)")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                if !endpoint.pathParameters.isEmpty {
                    Text("Parámetros: \(endpoint.pathParameters.joined(separator: ", "))")
                        .font(.caption2).foregroundStyle(.orange).lineLimit(1)
                }
            }
            .padding(.vertical, 4).contentShape(Rectangle())
            .background(selected ? Color.accentColor.opacity(0.12) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
    }
}
