import SwiftUI
import AppKit

struct ContentView: View {
    @State var snapshot: ArchitectureSnapshot?
    @State var selectedProjectID: String?
    @State var repositoryURL: URL?
    @State var repositoryURLs: [URL] = []
    @StateObject var aiService = AIAssistantService()
    @State var isScanning = false
    @State var isCancellingScan = false
    @State var scanProgress: WorkspaceScanProgress?
    @State var activeScanID: UUID?
    @State var workspaceScanTask: Task<Void, Never>?
    @State var errorMessage: String?
    @State var selectedSection = "Resumen"
    @State var architectureMenuExpanded = true
    @State var developmentMenuExpanded = true
    @State var operationsMenuExpanded = false
    @State var intelligenceMenuExpanded = true
    @AppStorage("appearanceMode") var appearanceMode = "system"
    @State var watchingRepository = true
    @State var lastFingerprint: String?
    @State var httpCatalog: HTTPTestingCatalog?
    @State var isLoadingHTTPCatalog = false
    @State var codeFilePath: String?
    @State var codeRootPath: String?
    @State var codeLine = 1
    @State var showBuildConsole = false
    @StateObject var buildConsoleModel = BuildConsoleModel()
    @ObservedObject var vscodeBridge = VSCodeBridgeService.shared
    let engine = EngineClient()

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            VStack(spacing: 0) {
                topBar
                Rectangle()
                    .fill(Color.primary.opacity(0.055))
                    .frame(height: 1)
                if isScanning, let scanProgress {
                    HStack(spacing: 12) {
                        if scanProgress.phase == "scanning" && scanProgress.totalRoots == 1 {
                            ProgressView().controlSize(.small)
                        } else {
                            ProgressView(value: scanProgress.fraction).frame(maxWidth: 170)
                        }
                        Text(scanProgress.message)
                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        Spacer(minLength: 0)
                        Button(isCancellingScan ? "Cancelando…" : "Cancelar") { cancelWorkspaceScan() }
                            .buttonStyle(.borderless)
                            .font(.caption.weight(.medium))
                            .disabled(isCancellingScan)
                    }
                    .padding(.horizontal, 26)
                    .padding(.vertical, 8)
                    .background(.ultraThinMaterial)
                }
                if selectedSection == "Git" {
                    GitWorkbenchView(workspaceURLs: repositoryURLs)
                } else if selectedSection == "Código" {
                    CodeExplorerView(workspaceURLs: repositoryURLs, aiService: aiService,
                                     requestedFilePath: $codeFilePath,
                                     requestedRootPath: $codeRootPath,
                                     requestedLine: $codeLine)
                } else if selectedSection == "Servicios" {
                    StackPilotDashboard(section: selectedSection, workspaceURLs: repositoryURLs)
                } else if selectedSection == "Perfiles" {
                    StackPilotProfilesView(workspaceURLs: repositoryURLs)
                } else if selectedSection == "Diagnóstico" {
                    StackPilotDiagnosticsView(workspaceURLs: repositoryURLs)
                } else if selectedSection == "Configuración IA" {
                    AISettingsView(service: aiService)
                } else if selectedSection == "Asistente IA" {
                    AIAssistantView(service: aiService, snapshot: snapshot, onOpenSettings: { selectedSection = "Configuración IA"; intelligenceMenuExpanded = true })
                } else if let snapshot {
                    selectedContent(snapshot)
                } else {
                    emptyState
                }
                if showBuildConsole {
                    BuildConsoleView(workspaceURLs: repositoryURLs, model: buildConsoleModel, onClose: { showBuildConsole = false })
                        .frame(height: 270)
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                }
                statusBar
            }
            .background { AtlasBackdrop() }
        }
        .background { AtlasBackdrop() }
        .frame(minWidth: 1180, minHeight: 760)
        .task { await watchRepositoryLoop() }
        .alert("No se pudo analizar el repositorio", isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })) {
            Button("Aceptar", role: .cancel) { errorMessage = nil }
        } message: { Text(errorMessage ?? "Error desconocido") }
        .onReceive(NotificationCenter.default.publisher(for: .codeAtlasNavigateSection)) { notification in
            guard let section = notification.object as? String else { return }
            selectedSection = section
            if section == "Perfiles" || section == "Diagnóstico" { operationsMenuExpanded = true }
            if section == "Código" { developmentMenuExpanded = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: .codeAtlasOpenSource)) { notification in
            guard let path = notification.userInfo?["path"] as? String else { return }
            let root = notification.userInfo?["root"] as? String ?? ""
            let line = notification.userInfo?["line"] as? Int ?? 1
            if !root.isEmpty, !repositoryURLs.contains(where: { $0.standardizedFileURL.path == URL(fileURLWithPath: root).standardizedFileURL.path }) {
                repositoryURLs = normalizedRepositoryRoots(repositoryURLs + [URL(fileURLWithPath: root, isDirectory: true)])
                repositoryURL = repositoryURLs.first
            }
            codeRootPath = root.isEmpty ? repositoryURL?.path : root
            codeLine = max(1, line)
            codeFilePath = path
            selectedSection = "Código"
            developmentMenuExpanded = true
        }
        .onReceive(NotificationCenter.default.publisher(for: .codeAtlasOpenWorkspace)) { notification in
            guard let path = notification.userInfo?["path"] as? String else { return }
            let root = URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL
            repositoryURLs = normalizedRepositoryRoots(repositoryURLs + [root])
            repositoryURL = repositoryURLs.first
            snapshot = nil
            httpCatalog = nil
            lastFingerprint = nil
            selectedProjectID = nil
            selectedSection = "Resumen"
            startRescan()
        }
    }

    var sidebar: some View {
        List(selection: $selectedSection) {
            Section("INICIO") {
                navigationRow("Resumen", section: "Resumen", icon: "rectangle.grid.2x2.fill")
                navigationRow("Cambios recientes", section: "Cambios", icon: "arrow.triangle.2.circlepath")
                navigationRow("Control Git", section: "Git", icon: "arrow.triangle.branch")
            }

            Section("EXPLORAR") {
                DisclosureGroup(isExpanded: $architectureMenuExpanded) {
                    navigationRow("Mapa de arquitectura", section: "Arquitectura", icon: "square.3.layers.3d")
                    navigationRow("Relaciones y dependencias", section: "Relaciones", icon: "arrow.left.arrow.right")
                    navigationRow("Flujos de negocio", section: "Flujos", icon: "point.topleft.down.curvedto.point.bottomright.up")
                    navigationRow("Análisis y evidencias", section: "Análisis", icon: "viewfinder")
                } label: {
                    Label("Arquitectura", systemImage: "square.3.layers.3d")
                        .font(.system(.body, design: .rounded).weight(.semibold))
                }

                DisclosureGroup(isExpanded: $developmentMenuExpanded) {
                    navigationRow("Explorador de código", section: "Código", icon: "chevron.left.forwardslash.chevron.right")
                    navigationRow("Endpoints HTTP", section: "Endpoints HTTP", icon: "network")
                } label: {
                    Label("Desarrollo y pruebas", systemImage: "curlybraces")
                        .font(.system(.body, design: .rounded).weight(.semibold))
                }

                DisclosureGroup(isExpanded: $operationsMenuExpanded) {
                    navigationRow("Servicios locales", section: "Servicios", icon: "server.rack")
                    navigationRow("Perfiles de ejecución", section: "Perfiles", icon: "square.stack.3d.up")
                    navigationRow("Diagnóstico", section: "Diagnóstico", icon: "stethoscope")
                } label: {
                    Label("Entorno local", systemImage: "terminal")
                        .font(.system(.body, design: .rounded).weight(.semibold))
                }

                DisclosureGroup(isExpanded: $intelligenceMenuExpanded) {
                    navigationRow("Asistente IA", section: "Asistente IA", icon: "brain.head.profile")
                    navigationRow("Configuración de IA", section: "Configuración IA", icon: "slider.horizontal.3")
                } label: {
                    Label("Inteligencia", systemImage: "sparkles")
                        .font(.system(.body, design: .rounded).weight(.semibold))
                }
            }

            Section("WORKSPACE · \(repositoryURLs.count)") {
                if repositoryURLs.isEmpty {
                    Label("Ninguna carpeta seleccionada", systemImage: "folder.badge.questionmark")
                        .font(.caption).foregroundStyle(.secondary)
                }
                ForEach(repositoryURLs, id: \.path) { root in
                    HStack(spacing: 7) {
                        Image(systemName: "folder.fill")
                            .font(.callout)
                            .foregroundStyle(Color.accentColor)
                            .frame(width: 22)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(WorkspaceSnapshotMerger.label(for: root))
                                .font(.callout.weight(.medium)).lineLimit(1)
                            Text(root.path)
                                .font(.system(size: 9, design: .monospaced))
                                .foregroundStyle(.tertiary).lineLimit(1)
                        }
                        Spacer(minLength: 0)
                        Button { removeRepository(root) } label: {
                            Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                        }
                        .buttonStyle(.plain)
                        .help("Quitar carpeta del workspace")
                    }
                    .padding(.vertical, 3)
                }
                Button { chooseRepository(addToWorkspace: true) } label: {
                    Label("Añadir carpetas…", systemImage: "folder.badge.plus")
                }
                .buttonStyle(.plain)
            }

            if let snapshot {
                Section("PROYECTOS · \(snapshot.projects.count)") {
                    ForEach(snapshot.projects) { project in
                        Button {
                            selectedProjectID = project.id
                            selectedSection = "Arquitectura"
                            architectureMenuExpanded = true
                        } label: {
                            Label(project.name, systemImage: icon(for: project.kind))
                                .lineLimit(1)
                        }
                        .buttonStyle(.plain)
                        .help(project.path)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .scrollContentBackground(.hidden)
        .background(.ultraThinMaterial)
        .navigationTitle("CodeAtlas")
        .navigationSplitViewColumnWidth(min: 245, ideal: 278, max: 340)
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(alignment: .leading, spacing: 10) {
                Divider()
                HStack(spacing: 7) {
                    Image(systemName: "circle.fill").font(.system(size: 7)).foregroundStyle(.green)
                    Text("Análisis local · IA opcional").font(.caption2).foregroundStyle(.secondary)
                    Spacer()
                    AtlasStatusPill(title: "0.15.0", color: .secondary)
                }
                HStack(spacing: 8) {
                    Label("Apariencia", systemImage: appearanceMode == "dark" ? "moon.fill" : appearanceMode == "light" ? "sun.max.fill" : "circle.lefthalf.filled")
                        .font(.caption.weight(.medium))
                        .foregroundStyle(.secondary)
                    Spacer(minLength: 2)
                    Picker("Apariencia", selection: $appearanceMode) {
                        Image(systemName: "desktopcomputer").tag("system")
                        Image(systemName: "sun.max.fill").tag("light")
                        Image(systemName: "moon.fill").tag("dark")
                    }
                    .labelsHidden()
                    .pickerStyle(.segmented)
                    .controlSize(.small)
                    .frame(width: 112)
                    .help("Cambiar entre apariencia del sistema, clara y oscura")
                }
            }
            .padding(.horizontal, 14)
            .padding(.top, 8)
            .padding(.bottom, 10)
            .background(.ultraThinMaterial)
        }
        .toolbar {
            ToolbarItem {
                Button { chooseRepository(addToWorkspace: true) } label: {
                    Image(systemName: "folder.badge.plus")
                }
                .help("Añadir una o varias carpetas al workspace")
            }
        }
    }

    func navigationRow(_ title: String, section: String, icon: String) -> some View {
        Label(title, systemImage: icon)
            .tag(section)
            .accessibilityLabel(title)
    }

    var topBar: some View {
        HStack(spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(LinearGradient(colors: [Color.accentColor.opacity(0.9), Color.purple.opacity(0.72)], startPoint: .topLeading, endPoint: .bottomTrailing))
                Image(systemName: sectionIcon(selectedSection))
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .frame(width: 42, height: 42)
            .shadow(color: Color.accentColor.opacity(0.16), radius: 10, x: 0, y: 4)

            VStack(alignment: .leading, spacing: 3) {
                Text(sectionTitle(selectedSection))
                    .font(.system(.title2, design: .rounded).weight(.bold))
                    .foregroundStyle(.primary)
                Text(snapshot.map { "\($0.repositoryName) · \($0.branch) · \(repositoryURLs.count) carpetas · \($0.scannedAt)" } ?? "Workspace de arquitectura y operaciones locales")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 12)
            if isScanning {
                ProgressView().controlSize(.small)
                Text("Analizando…").font(.callout).foregroundStyle(.secondary)
            }
            Button { chooseRepository(addToWorkspace: false) } label: {
                Label("Abrir workspace", systemImage: "folder")
            }
            .buttonStyle(.bordered)
            .controlSize(.regular)
            Button { withAnimation(.snappy(duration: 0.2)) { showBuildConsole.toggle() } } label: {
                Label("Build", systemImage: "hammer")
            }
            .prominentButtonStyle(showBuildConsole)
            .controlSize(.regular)
            .help("Abrir consola de compilación y pruebas · ⌘⇧B")

            if !["Servicios", "Perfiles", "Diagnóstico"].contains(selectedSection) {
                Toggle(isOn: $watchingRepository) {
                    Image(systemName: "arrow.triangle.2.circlepath")
                }
                .toggleStyle(.switch)
                .labelsHidden()
                .help("Actualizar automáticamente al detectar cambios en los repositorios")

                Button { startRescan() } label: {
                    Label("Actualizar", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.regular)
                .disabled(repositoryURLs.isEmpty || isScanning)
            }
        }
        .padding(.horizontal, 26)
        .padding(.vertical, 15)
        .background(.ultraThinMaterial)
    }
}

extension Notification.Name {
    /// Posted with the destination section title as `object`; ContentView switches to that section.
    static let codeAtlasNavigateSection = Notification.Name("codeAtlasNavigateSection")
}

extension View {
    /// `.buttonStyle(cond ? .borderedProminent : .bordered)` does not type-check (two different style types), so the choice is made on the view.
    @ViewBuilder func prominentButtonStyle(_ prominent: Bool) -> some View {
        if prominent { buttonStyle(.borderedProminent) } else { buttonStyle(.bordered) }
    }
}
