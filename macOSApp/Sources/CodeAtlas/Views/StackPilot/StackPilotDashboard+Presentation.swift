import SwiftUI
import AppKit

extension StackPilotDashboard {
var header: some View {
        HStack(alignment: .center, spacing: 14) {
            VStack(alignment: .leading, spacing: 5) {
                Text(sectionHeadingTitle).font(.system(.title2, design: .rounded).weight(.bold))
                Text(sectionDescription)
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            if isLoading { ProgressView().controlSize(.small) }
            Button { Task { await refresh() } } label: {
                Label("Actualizar", systemImage: "arrow.clockwise")
            }
            .buttonStyle(.bordered)
            .disabled(isLoading || busyAction != nil)
        }
    }

    var sectionHeadingTitle: String {
        switch section {
        case "Perfiles": return "Perfiles y ciclo de vida"
        case "Diagnóstico": return "Salud del entorno"
        default: return "Procesos y dependencias"
        }
    }

    var sectionDescription: String {
        switch section {
        case "Perfiles": return "Agrupa APIs, workers y dependencias locales en perfiles de arranque y parada."
        case "Diagnóstico": return "Comprueba procesos administrados, Docker Compose y endpoints de health check configurados."
        default: return "Controla procesos .NET, aplicaciones Node.js y servicios Docker Compose desde un panel único."
        }
    }

    @ViewBuilder
    func configurationCard(_ response: StackPilotResponse) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "doc.text.magnifyingglass")
                .font(.title2).foregroundStyle(.secondary)
                .frame(width: 42, height: 42)
                .atlasGlassCard(cornerRadius: 10, shadow: false)
            VStack(alignment: .leading, spacing: 4) {
                Text("Configuración de StackPilot").font(.headline)
                Text(response.configurationPath)
                    .font(.caption.monospaced()).foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .lineLimit(2)
            }
            Spacer(minLength: 8)
            Button("Copiar ruta") { NSPasteboard.general.setString(response.configurationPath, forType: .string) }
                .buttonStyle(.bordered)
            Button("Abrir JSON") { openConfiguration(response.configurationPath) }
                .buttonStyle(.borderedProminent)
                .disabled(!FileManager.default.fileExists(atPath: response.configurationPath))
        }
        .padding(14)
        .atlasGlassCard(cornerRadius: 12)
    }

    var emptyConfiguration: some View {
        VStack(alignment: .leading, spacing: 12) {
            Image(systemName: "server.rack").font(.system(size: 34)).foregroundStyle(.secondary)
            Text("Todavía no hay configuración local").font(.title3.bold())
            Text("Genera un perfil inicial a partir de un repositorio. Los servicios detectados se crean desactivados por seguridad; revisa los comandos antes de habilitarlos.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Button { addCurrentOrSelectedWorkspace() } label: {
                Label("Crear perfil desde un repositorio…", systemImage: "folder.badge.plus")
            }
            .buttonStyle(.borderedProminent)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(24)
        .atlasGlassCard(cornerRadius: 14)
    }

    var emptyProfiles: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("No hay perfiles todavía").font(.headline)
            Text("Añade el repositorio seleccionado como perfil. CodeAtlas detectará proyectos ejecutables y paquetes con scripts de desarrollo, pero no los iniciará automáticamente.")
                .font(.callout).foregroundStyle(.secondary)
            Button { addCurrentOrSelectedWorkspace() } label: {
                Label("Añadir workspace", systemImage: "plus")
            }
            .buttonStyle(.borderedProminent)
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .atlasGlassCard(cornerRadius: 12)
    }
}
