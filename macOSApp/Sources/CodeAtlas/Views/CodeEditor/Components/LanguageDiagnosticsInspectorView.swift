import SwiftUI

struct LanguageDiagnosticsInspectorView: View {
    let status: LanguageServerStatus
    let diagnostics: [LanguageDiagnostic]
    let onSelect: (LanguageDiagnostic) -> Void

    private var errors: Int { diagnostics.filter { $0.severity == 1 }.count }
    private var warnings: Int { diagnostics.filter { $0.severity == 2 }.count }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 7) {
                Circle()
                    .fill(status.state == .ready ? Color.green : status.state == .failed ? Color.red : Color.secondary)
                    .frame(width: 8, height: 8)
                Text(status.serverName).font(.caption.weight(.semibold)).lineLimit(1)
                Spacer(minLength: 0)
                Text(status.state == .ready ? "Activo" : status.state == .starting ? "Iniciando" : status.state == .failed ? "Error" : "Inactivo")
                    .font(.caption2).foregroundStyle(.secondary)
            }
            Text(status.message)
                .font(.caption2).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if !diagnostics.isEmpty {
                HStack(spacing: 8) {
                    Label("\(errors)", systemImage: "xmark.circle.fill").foregroundStyle(.red)
                    Label("\(warnings)", systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    Spacer(minLength: 0)
                    Text("\(diagnostics.count)").foregroundStyle(.secondary)
                }
                .font(.caption2)
                Divider()
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 3) {
                        ForEach(diagnostics) { item in
                            Button { onSelect(item) } label: {
                                HStack(alignment: .top, spacing: 7) {
                                    Image(systemName: icon(for: item.severity))
                                        .foregroundStyle(color(for: item.severity))
                                        .frame(width: 14)
                                        .padding(.top, 2)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(item.message)
                                            .font(.system(size: 11))
                                            .multilineTextAlignment(.leading)
                                            .fixedSize(horizontal: false, vertical: true)
                                        HStack(spacing: 4) {
                                            Text("L\(item.line):\(item.column)")
                                            if let code = item.code { Text("· \(code)") }
                                            if let source = item.source { Text("· \(source)").lineLimit(1) }
                                        }
                                        .font(.system(size: 9, design: .monospaced))
                                        .foregroundStyle(.tertiary)
                                    }
                                    Spacer(minLength: 0)
                                }
                                .padding(7)
                                .contentShape(Rectangle())
                                .background(RoundedRectangle(cornerRadius: 7).fill(Color.primary.opacity(0.035)))
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            } else if status.state == .ready {
                ContentUnavailableView("Sin diagnósticos", systemImage: "checkmark.circle",
                    description: Text("El servidor no ha publicado errores ni advertencias para este documento."))
                    .frame(minHeight: 140)
            } else {
                ContentUnavailableView("Diagnósticos semánticos no disponibles", systemImage: "puzzlepiece.extension",
                    description: Text("CodeAtlas no instalará servidores de lenguaje automáticamente. Configura el servidor recomendado y vuelve a abrir el archivo."))
                    .frame(minHeight: 140)
            }
            Spacer(minLength: 0)
        }
    }

    private func icon(for severity: Int) -> String {
        switch severity {
        case 1: return "xmark.circle.fill"
        case 2: return "exclamationmark.triangle.fill"
        case 4: return "lightbulb"
        default: return "info.circle.fill"
        }
    }

    private func color(for severity: Int) -> Color {
        switch severity {
        case 1: return .red
        case 2: return .orange
        case 4: return .secondary
        default: return .blue
        }
    }
}
