import SwiftUI
import AppKit

extension StackPilotDashboard {
func profileCard(_ profile: StackPilotProfileStatus) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: "square.stack.3d.up.fill").font(.title3).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 3) {
                    Text(profile.name).font(.headline)
                    Text(profile.workspaceRoot).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer()
                Text("\(profile.services.filter { $0.status == "running" }.count) / \(profile.services.count) activos")
                    .font(.caption.weight(.medium)).foregroundStyle(.secondary)
                Button { Task { await perform(["start-profile", profile.id]) } } label: {
                    Label("Iniciar perfil", systemImage: "play.fill")
                }
                .buttonStyle(.borderedProminent)
                .disabled(isLoading || busyAction != nil || profile.services.allSatisfy { !$0.enabled })
                Button { Task { await perform(["stop-profile", profile.id]) } } label: {
                    Label("Detener perfil", systemImage: "stop.fill")
                }
                .buttonStyle(.bordered)
                .disabled(isLoading || busyAction != nil || profile.services.allSatisfy { $0.status != "running" })
            }
            Divider()
            if profile.services.isEmpty {
                ContentUnavailableView("No se detectaron servicios ejecutables", systemImage: "shippingbox", description: Text("Puedes añadir una entrada al JSON para registrar un servicio o un contenedor de Docker Compose."))
                    .frame(minHeight: 110)
            } else {
                VStack(spacing: 0) {
                    ForEach(profile.services) { service in
                        serviceRow(profile: profile, service: service)
                        if service.id != profile.services.last?.id { Divider().padding(.leading, 54) }
                    }
                }
            }
        }
        .padding(16)
        .atlasGlassCard(cornerRadius: 14)
    }

    func serviceRow(profile: StackPilotProfileStatus, service: StackPilotServiceStatus) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: icon(for: service))
                .font(.title3).foregroundStyle(color(for: service.status))
                .frame(width: 34, height: 34)
                .background(color(for: service.status).opacity(0.12), in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 8) {
                    Text(service.name).font(.callout.weight(.semibold))
                    statusPill(service.status)
                    if service.kind.lowercased() == "compose" {
                        Label("Compose", systemImage: "shippingbox.fill").font(.caption2).foregroundStyle(.secondary)
                    }
                }
                Text(service.command).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(2)
                Text(service.workingDirectory).font(.caption2.monospaced()).foregroundStyle(.tertiary).lineLimit(1)
                if let url = service.healthUrl, !url.isEmpty {
                    Label("\(url) · \(service.healthStatus)", systemImage: service.healthStatus == "healthy" ? "checkmark.shield.fill" : "waveform.path.ecg")
                        .font(.caption2).foregroundStyle(color(forHealth: service.healthStatus)).lineLimit(1)
                }
                if let detail = service.error, !detail.isEmpty {
                    Text(detail).font(.caption).foregroundStyle(.orange).lineLimit(3)
                }
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 8) {
                HStack(spacing: 6) {
                    Button {
                        Task { await perform([service.enabled ? "disable" : "enable", profile.id, service.id]) }
                    } label: {
                        Image(systemName: service.enabled ? "checkmark.circle.fill" : "circle.dashed")
                    }
                    .buttonStyle(.borderless)
                    .help(service.enabled ? "Deshabilitar plantilla" : "Habilitar servicio")
                    .disabled(isLoading || busyAction != nil || (service.enabled && service.status == "running"))

                    Button {
                        Task { await perform(["start", profile.id, service.id]) }
                    } label: { Image(systemName: "play.fill") }
                    .buttonStyle(.bordered)
                    .help("Iniciar servicio")
                    .disabled(isLoading || busyAction != nil || !service.enabled || service.status == "running")

                    Button {
                        Task { await perform(["stop", profile.id, service.id]) }
                    } label: { Image(systemName: "stop.fill") }
                    .buttonStyle(.bordered)
                    .help("Detener servicio")
                    .disabled(isLoading || busyAction != nil || service.status != "running")

                    Button {
                        Task { await showLogs(profile: profile, service: service) }
                    } label: { Image(systemName: "text.alignleft") }
                    .buttonStyle(.bordered)
                    .help("Ver últimos logs")
                    .disabled(isLoading || busyAction != nil)
                }
                if let pid = service.pid {
                    Text("PID \(pid)").font(.caption2.monospaced()).foregroundStyle(.secondary)
                }
            }
        }
        .padding(.vertical, 12)
    }

    func statusPill(_ status: String) -> some View {
        Text(statusLabel(status))
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 7).padding(.vertical, 3)
            .background(color(for: status).opacity(0.13), in: Capsule())
            .foregroundStyle(color(for: status))
    }

    func statusLabel(_ status: String) -> String {
        switch status {
        case "running": return "EN EJECUCIÓN"
        case "disabled": return "DESACTIVADO"
        case "unavailable": return "NO DISPONIBLE"
        default: return "DETENIDO"
        }
    }

    func icon(for service: StackPilotServiceStatus) -> String {
        if service.kind.lowercased() == "compose" { return "shippingbox.fill" }
        if service.command.lowercased().contains("dotnet") { return "curlybraces" }
        if ["npm", "pnpm", "yarn", "node"].contains(service.command.lowercased()) { return "chevron.left.forwardslash.chevron.right" }
        return "terminal"
    }

    func color(forHealth status: String) -> Color {
        if status == "healthy" { return .green }
        if status == "unhealthy" || status.hasPrefix("http-") { return .red }
        return .secondary
    }

    func color(for status: String) -> Color {
        switch status {
        case "running": return .green
        case "unavailable": return .red
        case "disabled": return .secondary
        default: return .orange
        }
    }
}
