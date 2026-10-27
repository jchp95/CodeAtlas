import SwiftUI
import AppKit

private struct DiagnosticServiceItem: Identifiable {
    let profile: StackPilotProfileStatus
    let service: StackPilotServiceStatus
    var id: String { profile.id + "::" + service.id }
}

struct StackPilotDiagnosticsView: View {
    let workspaceURLs: [URL]
    @State private var response: StackPilotResponse?
    @State private var isLoading = false
    @State private var errorMessage: String?
    @State private var filter = "Problemas"
    @State private var selectedLog: StackPilotLogDocument?
    private let engine = EngineClient()

    private var allServices: [DiagnosticServiceItem] {
        (response?.profiles ?? []).flatMap { profile in profile.services.map { DiagnosticServiceItem(profile: profile, service: $0) } }
    }
    private var problems: [DiagnosticServiceItem] {
        allServices.filter { item in
            let service = item.service
            return service.status == "unavailable" || (service.enabled && service.status != "running") || (service.healthStatus != "healthy" && service.healthStatus != "not-configured") || service.error != nil
        }
    }
    private var runningCount: Int { allServices.filter { $0.service.status == "running" }.count }
    private var unhealthyCount: Int { allServices.filter { $0.service.healthStatus != "healthy" && $0.service.healthStatus != "not-configured" }.count }
    private var visibleServices: [DiagnosticServiceItem] {
        switch filter {
        case "Problemas": return problems
        case "Salud": return allServices.filter { $0.service.healthStatus != "not-configured" }
        case "Procesos": return allServices.filter { $0.service.status == "running" || $0.service.status == "unavailable" }
        default: return allServices
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Diagnóstico del entorno").font(.system(.largeTitle, design: .rounded).bold())
                        Text("Estado de procesos, checks HTTP, rutas de logs y dependencias registradas. El diagnóstico no reinicia servicios ni modifica configuración.")
                            .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer()
                    Button { Task { await diagnose() } } label: { Label("Ejecutar diagnóstico", systemImage: "stethoscope") }
                        .buttonStyle(.borderedProminent).disabled(isLoading)
                }
                if isLoading && response == nil { ProgressView("Consultando servicios…").frame(maxWidth: .infinity, minHeight: 160) }
                else if let response, !response.configured {
                    VStack(alignment: .leading, spacing: 10) {
                        Label("StackPilot no está configurado", systemImage: "info.circle").font(.title3.bold())
                        Text(response.message ?? "Añade un workspace desde Perfiles de ejecución para que StackPilot pueda diagnosticarlo.").foregroundStyle(.secondary)
                        Button("Abrir Perfiles de ejecución") { NotificationCenter.default.post(name: .codeAtlasNavigateSection, object: "Perfiles") }.buttonStyle(.borderedProminent)
                    }.padding(20).atlasGlassCard(cornerRadius: 14)
                } else if let response {
                    HStack(spacing: 12) {
                        metric("Servicios", "\(allServices.count)", "Todos los perfiles", "server.rack", .secondary)
                        metric("Ejecutándose", "\(runningCount)", "Procesos activos", "play.circle", .green)
                        metric("Con alertas", "\(problems.count)", "Requieren revisión", "exclamationmark.triangle", problems.isEmpty ? .green : .orange)
                        metric("Health checks", "\(unhealthyCount)", "Fallos o respuestas no saludables", "waveform.path.ecg", unhealthyCount == 0 ? .green : .red)
                    }
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            Text("Resultados").font(.title3.bold())
                            Spacer()
                            Picker("Filtro", selection: $filter) {
                                ForEach(["Problemas", "Todos", "Salud", "Procesos"], id: \.self) { Text($0).tag($0) }
                            }.pickerStyle(.segmented).frame(maxWidth: 380)
                        }
                        if visibleServices.isEmpty {
                            ContentUnavailableView(filter == "Problemas" ? "Sin problemas detectados" : "No hay servicios para este filtro",
                                systemImage: filter == "Problemas" ? "checkmark.shield" : "line.3.horizontal.decrease.circle",
                                description: Text(filter == "Problemas" ? "No hay servicios que coincidan con las reglas de alerta de StackPilot." : "Prueba otro filtro o actualiza el diagnóstico."))
                                .frame(minHeight: 170)
                        } else {
                            ForEach(visibleServices) { item in diagnosticRow(profile: item.profile, service: item.service) }
                        }
                    }.padding(16).atlasGlassCard(cornerRadius: 14)
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Guía de diagnóstico").font(.headline)
                        diagnosticHint("Servicio deshabilitado", "Habilítalo en Perfiles de ejecución después de revisar comando, directorio y variables.", "shield.lefthalf.filled")
                        diagnosticHint("Proceso detenido", "Comprueba dependencias, ejecuta Build/Tests y revisa los logs antes de volver a iniciar.", "terminal")
                        diagnosticHint("Health check fallido", "Verifica que el puerto esté libre, la URL configurada sea correcta y el servicio acepte conexiones locales.", "waveform.path.ecg")
                        Text("Configuración: \(response.configurationPath)").font(.caption2.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                    }.padding(16).atlasGlassCard(cornerRadius: 14)
                }
            }
            .padding(24).frame(maxWidth: 1450, alignment: .leading).frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
        .task { await diagnose() }
        .onChange(of: workspaceURLs.map(\.path).joined(separator: "|")) { _, _ in Task { await diagnose() } }
        .alert("Diagnóstico", isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })) {
            Button("Aceptar", role: .cancel) { errorMessage = nil }
        } message: { Text(errorMessage ?? "Error desconocido") }
        .sheet(item: $selectedLog) { document in
            VStack(alignment: .leading, spacing: 12) {
                HStack { Text(document.title).font(.headline); Spacer(); Button("Copiar") { NSPasteboard.general.setString(document.content, forType: .string) }; Button("Cerrar") { selectedLog = nil } }
                Divider()
                ScrollView { Text(document.content.isEmpty ? "No hay salida de logs." : document.content).font(.system(.caption, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12) }
                    .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            }.padding(18).frame(minWidth: 680, minHeight: 440)
        }
    }

    private func metric(_ title: String, _ value: String, _ detail: String, _ icon: String, _ tint: Color) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Image(systemName: icon).font(.title2).foregroundStyle(tint)
            Text(value).font(.system(.largeTitle, design: .rounded).bold())
            Text(title).font(.callout.weight(.semibold))
            Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
        }.frame(maxWidth: .infinity, minHeight: 120, alignment: .leading).padding(14).atlasGlassCard(cornerRadius: 12)
    }

    private func diagnosticRow(profile: StackPilotProfileStatus, service: StackPilotServiceStatus) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Image(systemName: service.status == "running" ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                    .foregroundStyle(service.status == "running" ? .green : .orange)
                VStack(alignment: .leading, spacing: 2) {
                    Text(service.name).font(.callout.weight(.semibold))
                    Text(profile.name + " · " + service.workingDirectory).font(.caption2.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                Text(service.status.uppercased()).font(.caption2.weight(.bold)).padding(.horizontal, 7).padding(.vertical, 4).background(.quaternary, in: Capsule())
                Button { Task { await loadLogs(profile: profile, service: service) } } label: { Label("Logs", systemImage: "text.alignleft") }.buttonStyle(.bordered)
            }
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 7) {
                GridRow { Text("Health").foregroundStyle(.secondary); Text(service.healthStatus).foregroundStyle(healthColor(service.healthStatus)) }
                if let pid = service.pid { GridRow { Text("PID").foregroundStyle(.secondary); Text(String(pid)).font(.system(.caption, design: .monospaced)) } }
                if let url = service.healthUrl, !url.isEmpty { GridRow { Text("URL").foregroundStyle(.secondary); Text(url).font(.system(.caption, design: .monospaced)).textSelection(.enabled) } }
                if !service.dependsOn.isEmpty { GridRow { Text("Depende de").foregroundStyle(.secondary); Text(service.dependsOn.joined(separator: ", ")).font(.caption) } }
            }.font(.caption)
            Text(service.command).font(.caption.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
            if let error = service.error, !error.isEmpty { Label(error, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange) }
        }.padding(.vertical, 10)
        .overlay(alignment: .bottom) { Rectangle().fill(Color.primary.opacity(0.08)).frame(height: 1) }
    }

    private func diagnosticHint(_ title: String, _ detail: String, _ icon: String) -> some View {
        HStack(alignment: .top, spacing: 10) { Image(systemName: icon).foregroundStyle(.secondary).frame(width: 22); VStack(alignment: .leading, spacing: 3) { Text(title).font(.callout.weight(.semibold)); Text(detail).font(.caption).foregroundStyle(.secondary) } }
    }

    private func healthColor(_ status: String) -> Color {
        if status == "healthy" { return .green }
        if status == "not-configured" { return .secondary }
        return .red
    }

    private func diagnose() async {
        guard !isLoading else { return }
        isLoading = true; defer { isLoading = false }
        do { response = try await engine.stackPilot(arguments: ["doctor"]); if let error = response?.error { errorMessage = error } }
        catch { errorMessage = error.localizedDescription }
    }

    private func loadLogs(profile: StackPilotProfileStatus, service: StackPilotServiceStatus) async {
        do { selectedLog = StackPilotLogDocument(title: "Diagnóstico · \(service.name)", content: try await engine.stackPilotLogs(profileID: profile.id, serviceID: service.id)) }
        catch { errorMessage = error.localizedDescription }
    }
}
