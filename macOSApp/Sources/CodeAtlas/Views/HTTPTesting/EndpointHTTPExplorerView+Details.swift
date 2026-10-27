import SwiftUI
import AppKit

extension EndpointHTTPExplorerView {
func endpointDetail(_ endpoint: HTTPApiEndpoint) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top, spacing: 12) {
                    methodBadge(endpoint.method).scaleEffect(1.1, anchor: .leading)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(endpoint.route).font(.title2.bold().monospaced())
                        Text("\(endpoint.projectName) · \(endpoint.framework)").font(.callout).foregroundStyle(.secondary)
                        Button { openSourceFile(endpoint.sourcePath, line: endpoint.line, workspaceRoot: endpoint.workspaceRoot) } label: {
                            Label("\(endpoint.sourcePath):\(endpoint.line)", systemImage: "arrow.up.forward.square")
                                .font(.caption.monospaced()).foregroundStyle(.secondary)
                        }.buttonStyle(.plain).help("Abrir archivo y línea en el explorador de código")
                    }
                    Spacer()
                    Button { saveGeneratedHTTP(endpoint) } label: { Label("Crear .http", systemImage: "doc.badge.plus") }
                        .buttonStyle(.bordered)
                }
                if !endpoint.pathParameters.isEmpty {
                    Label("Ruta con parámetros: sustituye \(endpoint.pathParameters.map { "{\($0)}" }.joined(separator: ", ")) por valores reales.", systemImage: "point.topleft.down.curvedto.point.bottomright.up")
                        .font(.caption).foregroundStyle(.orange)
                }
                if let requestType = endpoint.requestType {
                    HStack(spacing: 8) {
                        Image(systemName: "curlybraces").foregroundStyle(.secondary)
                        Text("Cuerpo detectado: \(requestType)").font(.subheadline.weight(.medium))
                        Spacer()
                        Text(schemaLabel(endpoint.schemaSource)).font(.caption).foregroundStyle(.secondary)
                    }
                } else if ["POST", "PUT", "PATCH"].contains(endpoint.method) {
                    Label("No se pudo inferir un tipo de cuerpo fiable. Puedes introducir JSON manualmente.", systemImage: "info.circle")
                        .font(.caption).foregroundStyle(.secondary)
                }
                let associated = (catalog?.requests ?? []).filter { $0.associatedEndpointIds.contains(endpoint.id) }
                if !associated.isEmpty {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Pruebas .http asociadas").font(.headline)
                        ForEach(associated) { request in
                            Button { selectRequest(request) } label: {
                                HStack(spacing: 8) {
                                    methodBadge(request.method)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(request.name).font(.subheadline.weight(.medium))
                                        Text("\(request.sourcePath):\(request.line)").font(.caption.monospaced()).foregroundStyle(.secondary)
                                    }
                                    Spacer()
                                    Image(systemName: "arrow.up.right").foregroundStyle(.secondary)
                                }.padding(9).atlasGlassCard(cornerRadius: 8, shadow: false)
                            }.buttonStyle(.plain)
                        }
                    }
                } else {
                    Label("No hay una prueba .http asociada. Puedes probar este endpoint aquí o crear un archivo compatible con Rider / REST Client.", systemImage: "doc.text.magnifyingglass")
                        .font(.caption).foregroundStyle(.secondary)
                }
                requestEditor(isFileRequest: false)
                executeButton(title: "Ejecutar endpoint")
                responseSection
                Text(endpoint.detail + " El esquema JSON es una plantilla generada a partir de tipos del código fuente; revisa valores y validaciones antes de ejecutar operaciones que escriban datos.")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                    .atlasGlassCard(cornerRadius: 10, shadow: false)
            }
            .padding(22)
            .atlasGlassCard(cornerRadius: 18, shadow: false)
            .padding(16)
            .frame(maxWidth: 1040, alignment: .topLeading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    func requestDetail(_ request: HTTPTestRequest) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top, spacing: 12) {
                    methodBadge(request.method)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(request.name).font(.title2.bold())
                        Text("\(request.projectName) · \(request.sourcePath):\(request.line)")
                            .font(.caption.monospaced()).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button { openHTTPFile(request) } label: { Label("Abrir archivo", systemImage: "arrow.up.forward.square") }
                        .buttonStyle(.bordered)
                }
                if !request.associatedEndpointIds.isEmpty {
                    Label("Asociada a \(request.associatedEndpointIds.count) endpoint(s) detectado(s).", systemImage: "link")
                        .font(.caption).foregroundStyle(.green)
                } else {
                    Label("No se encontró una coincidencia exacta. La petición sigue siendo ejecutable; verifica URL, método y entorno.", systemImage: "link.badge.plus")
                        .font(.caption).foregroundStyle(.orange)
                }
                if !request.availableEnvironments.isEmpty {
                    Picker("Entorno HTTP", selection: $selectedEnvironment) {
                        Text("Variables compartidas / del archivo").tag("")
                        ForEach(request.availableEnvironments, id: \.self) { Text($0).tag($0) }
                    }
                    .pickerStyle(.menu)
                }
                if !request.unresolvedVariables.isEmpty {
                    Label("Variables por resolver: \(request.unresolvedVariables.joined(separator: ", "))", systemImage: "key.horizontal")
                        .font(.caption).foregroundStyle(.orange)
                }
                requestEditor(isFileRequest: true)
                executeButton(title: "Ejecutar petición .http")
                responseSection
                Text("La petición se vuelve a leer del archivo original al ejecutar. Se respetan las variables del archivo y las del entorno elegido; los campos editados en este panel se aplican como modificaciones temporales y no alteran el archivo fuente.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(22)
            .atlasGlassCard(cornerRadius: 18, shadow: false)
            .padding(16)
            .frame(maxWidth: 1040, alignment: .topLeading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    func httpCallDetail(_ call: HTTPCallTrace) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top, spacing: 12) {
                    methodBadge(call.method)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(call.route).font(.title2.bold().monospaced())
                        Text("Llamada saliente · \(call.sourceProjectName)").font(.callout).foregroundStyle(.secondary)
                        Text("\(call.sourcePath):\(call.line)").font(.caption.monospaced()).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button { openSourceFile(call.sourcePath, line: call.line, workspaceRoot: call.workspaceRoot) } label: { Label("Abrir origen", systemImage: "arrow.up.forward.square") }
                        .buttonStyle(.bordered)
                }
                HStack(spacing: 8) {
                    Image(systemName: call.confidence == "inferred" ? "link.circle.fill" : call.confidence == "ambiguous" ? "exclamationmark.triangle.fill" : "questionmark.circle")
                        .foregroundStyle(call.confidence == "inferred" ? .green : call.confidence == "ambiguous" ? .orange : .secondary)
                    Text(call.confidence == "inferred" ? "Un endpoint candidato coincide" : call.confidence == "ambiguous" ? "Destino ambiguo" : "Destino no resuelto")
                        .font(.subheadline.weight(.medium))
                    Spacer()
                }
                Text(call.detail).font(.caption).foregroundStyle(.secondary)
                    .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                    .atlasGlassCard(cornerRadius: 8, shadow: false)
                VStack(alignment: .leading, spacing: 8) {
                    Text("Endpoints receptores candidatos").font(.headline)
                    let targets = (catalog?.endpoints ?? []).filter { call.targetEndpointIds.contains($0.id) }
                    if targets.isEmpty {
                        ContentUnavailableView("No se resolvió un receptor", systemImage: "network.slash",
                            description: Text("La ruta es estática, pero no coincide con un endpoint de otro proyecto. Una BaseAddress dinámica, un gateway o una ruta construida en tiempo de ejecución pueden impedir la asociación."))
                            .frame(minHeight: 150)
                    } else {
                        ForEach(targets) { endpoint in
                            Button { selectEndpoint(endpoint) } label: {
                                HStack(alignment: .top, spacing: 10) {
                                    methodBadge(endpoint.method)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(endpoint.route).font(.system(.body, design: .monospaced, weight: .semibold))
                                        Text("\(endpoint.projectName) · \(endpoint.framework)").font(.caption).foregroundStyle(.secondary)
                                        Text("\(endpoint.sourcePath):\(endpoint.line)").font(.caption2.monospaced()).foregroundStyle(.tertiary)
                                    }
                                    Spacer()
                                    Image(systemName: "arrow.right.circle").foregroundStyle(.secondary)
                                }.padding(10).atlasGlassCard(cornerRadius: 8, shadow: false)
                            }.buttonStyle(.plain)
                        }
                    }
                }
                Label("Esta relación proviene del análisis estático del código; no demuestra que la llamada se ejecute ni que el destino de red resuelva a ese servicio en tiempo de ejecución.", systemImage: "info.circle")
                    .font(.caption).foregroundStyle(.secondary)
                HStack {
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString("\(call.method) \(call.route)", forType: .string)
                    } label: { Label("Copiar método y ruta", systemImage: "doc.on.doc") }
                    .buttonStyle(.bordered)
                    Spacer()
                }
            }
            .padding(22)
            .atlasGlassCard(cornerRadius: 18, shadow: false)
            .padding(16)
            .frame(maxWidth: 1040, alignment: .topLeading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    func requestEditor(isFileRequest: Bool) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Group {
                HStack {
                    Text("Petición HTTP").font(.headline)
                    Spacer()
                    Picker("Método", selection: $method) {
                        ForEach(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], id: \.self) { Text($0).tag($0) }
                    }.frame(width: 110)
                }
                HStack(alignment: .center, spacing: 8) {
                    TextField("http://localhost:5000/api/resource", text: $requestURL)
                        .textFieldStyle(.roundedBorder).font(.system(.body, design: .monospaced))
                }
                Text("Cabeceras · una por línea").font(.subheadline.weight(.medium))
                TextEditor(text: $headersText).font(.system(.callout, design: .monospaced)).frame(minHeight: 66, maxHeight: 115)
                    .padding(5).background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.12)))
                HStack {
                    Text("Cuerpo").font(.subheadline.weight(.medium))
                    Spacer()
                    if !bodyText.isEmpty {
                        Button("Formatear JSON") { formatJSONBody() }.buttonStyle(.link).font(.caption)
                    }
                }
                TextEditor(text: $bodyText).font(.system(.callout, design: .monospaced)).frame(minHeight: 160, maxHeight: 320)
                    .padding(5).background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.12)))
                if isFileRequest {
                    Text("Se mantiene la sintaxis de variables {{variable}} de .http. Selecciona el entorno adecuado cuando el archivo usa http-client.env.json.")
                        .font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
    }
}
