import SwiftUI
import AppKit

struct StackPilotDashboard: View {
    let section: String
    let workspaceURLs: [URL]

    @State var response: StackPilotResponse?
    @State var isLoading = false
    @State var errorMessage: String?
    @State var busyAction: String?
    @State var selectedLog: StackPilotLogDocument?

    let engine = EngineClient()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                header
                if let response {
                    configurationCard(response)
                    if let message = response.message, !message.isEmpty {
                        Label(message, systemImage: "info.circle")
                            .font(.callout).foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    if !response.configured {
                        emptyConfiguration
                    } else if response.profiles.isEmpty {
                        emptyProfiles
                    } else {
                        ForEach(response.profiles) { profile in
                            profileCard(profile)
                        }
                    }
                } else if isLoading {
                    ProgressView("Cargando StackPilot…")
                        .frame(maxWidth: .infinity, minHeight: 200)
                } else {
                    emptyConfiguration
                }
            }
            .padding(26)
            .frame(maxWidth: 1500, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
        .task { await refresh() }
        .onChange(of: workspaceURLs.map(\.path).joined(separator: "|")) { _, _ in
            if !workspaceURLs.isEmpty { Task { await initializeWorkspacesIfMissing() } }
        }
        .alert("StackPilot", isPresented: Binding(
            get: { errorMessage != nil },
            set: { if !$0 { errorMessage = nil } }
        )) {
            Button("Aceptar", role: .cancel) { errorMessage = nil }
        } message: {
            Text(errorMessage ?? "Error desconocido")
        }
        .sheet(item: $selectedLog) { document in
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text(document.title).font(.headline)
                    Spacer()
                    Button("Copiar") { NSPasteboard.general.setString(document.content, forType: .string) }
                    Button("Cerrar") { selectedLog = nil }
                }
                Divider()
                ScrollView {
                    Text(document.content.isEmpty ? "No hay salida de logs." : document.content)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(12)
                }
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            }
            .padding(18)
            .frame(minWidth: 680, minHeight: 440)
        }
    }
}
