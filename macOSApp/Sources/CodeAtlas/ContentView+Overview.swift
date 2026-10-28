import SwiftUI
import AppKit

extension ContentView {
func summaryPage(_ snapshot: ArchitectureSnapshot) -> some View {
        page {
            AtlasPageHeading(eyebrow: "Resumen del workspace", title: "Tu entorno de desarrollo", detail: "Resumen consolidado de repositorios, arquitectura y cambios detectados localmente.", systemImage: "rectangle.grid.2x2.fill")
            summaryCards(snapshot)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 14), GridItem(.flexible(), spacing: 14)], spacing: 14) {
                quickNavigationCard(title: "Mapa de arquitectura", detail: "Servicios, capas Clean Architecture, mensajería e infraestructura.", icon: "square.3.layers.3d", destination: "Arquitectura", tint: .blue)
                quickNavigationCard(title: "Flujos de negocio", detail: "Sigue los caminos detectados desde los endpoints hacia handlers y consumidores.", icon: "point.topleft.down.curvedto.point.bottomright.up", destination: "Flujos", tint: .purple)
                quickNavigationCard(title: "Endpoints HTTP", detail: "Prueba APIs y ejecuta peticiones guardadas en archivos .http y .rest.", icon: "network", destination: "Endpoints HTTP", tint: .teal)
                quickNavigationCard(title: "StackPilot", detail: "Administra servicios locales, perfiles de ejecución y diagnósticos.", icon: "server.rack", destination: "Servicios", tint: .orange)
            }
            gitRepositoriesSection(snapshot)
            changesSection(snapshot)
        }
    }

    func analysisPage(_ snapshot: ArchitectureSnapshot) -> some View {
        page {
            AtlasPageHeading(eyebrow: "Código · Evidencias · Confianza", title: "Análisis y evidencias", detail: "Consulta qué detectó el analizador, qué se infirió y qué necesita confirmación en ejecución.", systemImage: "viewfinder")
            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible())], spacing: 12) {
                MetricCard(title: "Proyectos analizados", value: "\(snapshot.projects.count)", detail: "C# · Next.js · Node.js", icon: "shippingbox")
                MetricCard(title: "Evidencias de código", value: "\(snapshot.projects.reduce(0) { $0 + ($1.codeEvidence?.count ?? 0) })", detail: "Endpoints · DI · mensajería", icon: "doc.text.magnifyingglass")
                MetricCard(title: "Advertencias", value: "\(snapshot.warnings.count)", detail: "Puntos a revisar", icon: "exclamationmark.triangle")
            }
            if snapshot.warnings.isEmpty {
                Label("No hay advertencias generales en este análisis", systemImage: "checkmark.shield")
                    .font(.callout).foregroundStyle(.secondary).padding(16).frame(maxWidth: .infinity, alignment: .leading)
                    .atlasGlassCard(cornerRadius: 14)
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    Label("Notas y límites del análisis", systemImage: "info.circle.fill")
                        .font(.headline).foregroundStyle(.primary)
                    ForEach(snapshot.warnings, id: \.self) { warning in
                        Label(warning, systemImage: "exclamationmark.circle")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(16).frame(maxWidth: .infinity, alignment: .leading)
                .atlasGlassCard(cornerRadius: 16)
            }
            VStack(alignment: .leading, spacing: 12) {
                sectionHeading("Evidencias por proyecto", detail: "Abre un proyecto para revisar sus referencias exactas en el mapa.", icon: "doc.text.magnifyingglass")
                ForEach(snapshot.projects) { project in
                    Button {
                        selectedProjectID = project.id
                        selectedSection = "Arquitectura"
                        architectureMenuExpanded = true
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: icon(for: project.kind))
                                .font(.title3).foregroundStyle(Color.accentColor)
                                .frame(width: 40, height: 40)
                                .background(Color.accentColor.opacity(0.10), in: RoundedRectangle(cornerRadius: 12))
                            VStack(alignment: .leading, spacing: 3) {
                                Text(project.name).font(.callout.weight(.semibold)).foregroundStyle(.primary)
                                Text(project.path).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Spacer()
                            Label("\(project.endpoints.count)", systemImage: "network").font(.caption).foregroundStyle(.secondary)
                            Label("\(project.codeEvidence?.count ?? 0)", systemImage: "doc.text").font(.caption).foregroundStyle(.secondary)
                            Image(systemName: "chevron.right").font(.caption.weight(.semibold)).foregroundStyle(.tertiary)
                        }
                        .padding(11)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(16)
            .atlasGlassCard(cornerRadius: 16)
        }
    }

    func quickNavigationCard(title: String, detail: String, icon: String, destination: String, tint: Color) -> some View {
        Button {
            selectedSection = destination
            if destination == "Flujos" { architectureMenuExpanded = true }
            if destination == "Servicios" { operationsMenuExpanded = true }
            if destination == "Endpoints HTTP" { developmentMenuExpanded = true }
        } label: {
            HStack(alignment: .top, spacing: 13) {
                Image(systemName: icon)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(tint)
                    .frame(width: 42, height: 42)
                    .background(tint.opacity(0.13), in: RoundedRectangle(cornerRadius: 13, style: .continuous))
                VStack(alignment: .leading, spacing: 6) {
                    Text(title).font(.system(.headline, design: .rounded)).foregroundStyle(.primary)
                    Text(detail).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.leading).fixedSize(horizontal: false, vertical: true)
                    HStack(spacing: 5) {
                        Text("Abrir vista").font(.caption.weight(.semibold))
                        Image(systemName: "arrow.up.right").font(.caption2.weight(.semibold))
                    }
                    .foregroundStyle(tint)
                    .padding(.top, 2)
                }
                Spacer(minLength: 0)
            }
            .padding(16)
            .frame(maxWidth: .infinity, minHeight: 110, alignment: .leading)
            .contentShape(RoundedRectangle(cornerRadius: 16))
        }
        .buttonStyle(.plain)
        .atlasGlassCard(cornerRadius: 16, highlighted: selectedSection == destination)
    }

    func sectionHeading(_ title: String, detail: String, icon: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon).foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(.title3, design: .rounded).weight(.bold))
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    var emptyState: some View {
        VStack {
            Spacer(minLength: 28)
            ContentUnavailableView {
                Label("Explora tu arquitectura", systemImage: "square.3.layers.3d")
            } description: {
                Text("Selecciona una o varias carpetas raíz de repositorios. CodeAtlas combinará los proyectos, las capas, las relaciones y los flujos entre servicios.")
            } actions: {
                Button("Seleccionar carpetas…", action: { chooseRepository(addToWorkspace: false) }).buttonStyle(.borderedProminent)
            }
            .padding(28)
            .frame(maxWidth: 760)
            .atlasGlassCard(cornerRadius: 24)
            Spacer(minLength: 28)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(32)
    }

    var statusBar: some View {
        HStack(spacing: 12) {
            HStack(spacing: 7) {
                Circle()
                    .fill(isScanning ? Color.orange : (snapshot == nil ? Color.secondary : Color.green))
                    .frame(width: 7, height: 7)
                Text(isScanning ? "Analizando workspace" : (snapshot == nil ? "Esperando workspace" : "Workspace listo"))
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
            }
            Rectangle().fill(Color.primary.opacity(0.12)).frame(width: 1, height: 13)
            Label("\(repositoryURLs.count) carpetas", systemImage: "folder")
                .font(.caption).foregroundStyle(.secondary)
            if let snapshot {
                Label("\(snapshot.projects.count) proyectos", systemImage: "shippingbox")
                    .font(.caption).foregroundStyle(.secondary)
                Label("\(snapshot.relations.count) relaciones", systemImage: "arrow.left.arrow.right")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            if watchingRepository && !repositoryURLs.isEmpty {
                Label("Auto-refresh", systemImage: "arrow.triangle.2.circlepath")
                    .font(.caption2).foregroundStyle(.secondary)
            }
            Label(vscodeBridge.isRunning ? "VS Code conectado" : "VS Code desconectado", systemImage: "app.connected.to.app.below.fill")
                .font(.caption2).foregroundStyle(vscodeBridge.isRunning ? .green : .secondary)
                .help(vscodeBridge.statusMessage)
            Text("CodeAtlas · 0.23.0")
                .font(.system(size: 10, weight: .medium, design: .monospaced))
                .foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 8)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) { Rectangle().fill(Color.primary.opacity(0.06)).frame(height: 1) }
    }
}
