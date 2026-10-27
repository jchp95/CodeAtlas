import AppKit
import SwiftUI

struct CodeExplorerView: View {
    let workspaceURLs: [URL]
    @ObservedObject var aiService: AIAssistantService
    @Binding var requestedFilePath: String?
    @Binding var requestedRootPath: String?
    @Binding var requestedLine: Int

    @State private var treeRoots: [CodeFileNode] = []
    @State private var searchText = ""
    @State var selectedFileURL: URL?
    @State private var selectedRootPath: String?
    @State var editorText = ""
    @State private var savedText = ""
    @State var targetLine = 1
    @State private var isLoadingTree = false
    @State private var isLoadingFile = false
    @State var errorMessage: String?
    @State private var showInspector = true
    @State private var pendingOpen: PendingSource?
    @State private var showDiscardConfirmation = false
    @State private var isFormatting = false
    @State private var formatStatus: String?
    @State var caretOffset = 0
    @State var showAISuggestion = false
    @State var isGeneratingSuggestion = false
    @State var aiPrompt = "Completa el código a partir del cursor respetando los patrones del proyecto."
    @State var aiSuggestion = ""
    @State private var inspectorTab = "Archivo"
    @State private var outlineSearch = ""
    @State private var outlineSymbols: [CodeOutlineSymbol] = []
    @StateObject private var languageServices = LanguageServiceManager()
    @State private var showCompletionPicker = false

    private struct PendingSource {
        let url: URL
        let rootPath: String
        let line: Int
    }

    private var isDirty: Bool { editorText != savedText }
    private var currentFileName: String { selectedFileURL?.lastPathComponent ?? "Sin archivo abierto" }
    private var fileExtension: String { selectedFileURL?.pathExtension.lowercased() ?? "" }
    var languageLabel: String {
        switch fileExtension {
        case "cs": return "C#"
        case "swift": return "Swift"
        case "ts", "tsx": return "TypeScript"
        case "js", "jsx", "mjs", "cjs": return "JavaScript"
        case "json": return "JSON"
        case "md", "markdown": return "Markdown"
        case "xml", "csproj", "props", "targets": return "XML"
        case "yml", "yaml": return "YAML"
        case "sh", "bash": return "Shell"
        case "http", "rest": return "HTTP Client"
        default: return fileExtension.isEmpty ? "Texto" : fileExtension.uppercased()
        }
    }
    private var filteredTree: [CodeFileNode] {
        treeRoots.compactMap { $0.filtered(by: searchText) }
    }

    var body: some View {
        HSplitView {
            explorerSidebar
                .frame(minWidth: 205, idealWidth: 250, maxWidth: 330)
            editorPanel
                .frame(minWidth: 420, maxWidth: .infinity, maxHeight: .infinity)
            if showInspector {
                inspectorPanel
                    .frame(minWidth: 190, idealWidth: 235, maxWidth: 310)
            }
        }
        .background(.clear)
        .task(id: workspaceURLs.map(\.path).joined(separator: "|")) { await refreshTreeAndSelection() }
        .task(id: editorText) {
            let source = editorText
            let ext = fileExtension
            try? await Task.sleep(nanoseconds: 220_000_000)
            guard !Task.isCancelled else { return }
            languageServices.documentChanged(text: source)
            guard !source.isEmpty else { outlineSymbols = []; return }
            let indexed = await Task.detached(priority: .utility) {
                CodeSymbolIndex.scan(source, fileExtension: ext)
            }.value
            guard !Task.isCancelled else { return }
            outlineSymbols = indexed
        }
        .onChange(of: "\(requestedRootPath ?? "")|\(requestedFilePath ?? "")") { _, _ in
            Task { await loadRequestedSource() }
        }
        .onChange(of: requestedLine) { _, newLine in
            targetLine = max(1, newLine)
        }
        .sheet(isPresented: $showAISuggestion) { aiSuggestionSheet }
        .onDisappear { languageServices.stop() }
        .confirmationDialog("Hay cambios sin guardar", isPresented: $showDiscardConfirmation, titleVisibility: .visible) {
            Button("Descartar cambios y abrir", role: .destructive) {
                guard let pendingOpen else { return }
                Task { await loadFile(pendingOpen.url, rootPath: pendingOpen.rootPath, line: pendingOpen.line) }
                self.pendingOpen = nil
            }
            Button("Seguir editando", role: .cancel) { pendingOpen = nil }
        } message: {
            Text("Guarda el archivo actual antes de abrir otro, o descarta los cambios locales.")
        }
        .alert("No se pudo abrir el archivo", isPresented: Binding(
            get: { errorMessage != nil },
            set: { if !$0 { errorMessage = nil } }
        )) {
            Button("Aceptar", role: .cancel) { errorMessage = nil }
        } message: { Text(errorMessage ?? "Error desconocido") }
    }

    private var explorerSidebar: some View {
        VStack(spacing: 0) {
            HStack {
                Label("Explorador", systemImage: "folder.tree")
                    .font(.system(.headline, design: .rounded))
                Spacer()
                if isLoadingTree { ProgressView().controlSize(.small) }
                Button { Task { await refreshTreeAndSelection() } } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.plain)
                .help("Actualizar árbol de archivos")
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            Divider()
            HStack(spacing: 7) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Buscar archivo o carpeta…", text: $searchText)
                    .textFieldStyle(.plain)
                if !searchText.isEmpty {
                    Button { searchText = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                        .buttonStyle(.plain)
                }
            }
            .padding(9)
            .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 8))
            .padding(9)
            if workspaceURLs.isEmpty {
                ContentUnavailableView("Abre un workspace", systemImage: "folder.badge.plus",
                                       description: Text("El árbol de archivos aparecerá aquí."))
            } else if treeRoots.isEmpty && !isLoadingTree {
                ContentUnavailableView("No hay archivos visibles", systemImage: "doc.questionmark",
                                       description: Text("Revisa la carpeta seleccionada o actualiza el explorador."))
            } else {
                List {
                    OutlineGroup(filteredTree, children: \.children) { node in
                        if node.isDirectory {
                            Label(node.name, systemImage: "folder")
                                .font(.system(size: 12, weight: .medium))
                                .help(node.path)
                        } else {
                            Button { requestOpen(node.path, root: node.rootPath, line: 1) } label: {
                                HStack(spacing: 6) {
                                    Image(systemName: iconForFile(node.name))
                                        .foregroundStyle(colorForFile(node.name))
                                        .frame(width: 15)
                                    Text(node.name).lineLimit(1)
                                    Spacer(minLength: 0)
                                }
                                .font(.system(size: 12))
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .help(node.path)
                        }
                    }
                }
                .listStyle(.sidebar)
                .scrollContentBackground(.hidden)
            }
            Divider()
            HStack(spacing: 6) {
                Image(systemName: "folder.badge.gearshape").foregroundStyle(.secondary)
                Text("\(workspaceURLs.count) raíz(ces) · árbol con límite de seguridad").font(.caption2).foregroundStyle(.secondary)
                Spacer(minLength: 0)
            }
            .padding(9)
        }
        .background(.ultraThinMaterial)
    }

    private var editorPanel: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: iconForFile(currentFileName))
                    .font(.title3)
                    .foregroundStyle(selectedFileURL == nil ? .secondary : Color.accentColor)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 7) {
                        Text(currentFileName).font(.headline).lineLimit(1)
                        if isDirty { Circle().fill(.orange).frame(width: 7, height: 7).help("Cambios sin guardar") }
                    }
                    Text(selectedFileURL?.path ?? "Selecciona un archivo en el explorador")
                        .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 8)
                if isLoadingFile { ProgressView().controlSize(.small) }
                HStack(spacing: 5) {
                    Circle().fill(languageServices.status.state == .ready ? Color.green : languageServices.status.state == .failed ? Color.red : Color.secondary).frame(width: 7, height: 7)
                    Text(languageServices.status.state == .ready ? "LSP activo" : "LSP opcional")
                        .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                .help(languageServices.status.message)
                Button {
                    Task {
                        await languageServices.requestCompletions(text: editorText, utf16Offset: caretOffset)
                        showCompletionPicker = true
                    }
                } label: {
                    if languageServices.isRequestingCompletion { ProgressView().controlSize(.small) }
                    else { Label("IntelliSense", systemImage: "text.magnifyingglass") }
                }
                .buttonStyle(.bordered)
                .disabled(selectedFileURL == nil || !languageServices.canUseSemanticFeatures || languageServices.isRequestingCompletion)
                .help(languageServices.status.message)
                Button { Task { await navigateToDefinition() } } label: { Label("Definición", systemImage: "arrow.turn.down.right") }
                    .buttonStyle(.bordered)
                    .disabled(selectedFileURL == nil || !languageServices.canUseSemanticFeatures)
                    .help("Ir a la definición del símbolo bajo el cursor con el servidor de lenguaje")
                Button { showAISuggestion = true } label: { Label("Completar con IA", systemImage: "sparkles") }
                    .buttonStyle(.bordered)
                    .disabled(selectedFileURL == nil || isGeneratingSuggestion)
                    .help("Generar una sugerencia revisable en la posición del cursor con el modelo configurado")
                Button { Task { await formatCurrentFile() } } label: {
                    if isFormatting { ProgressView().controlSize(.small) } else { Label("Formatear", systemImage: "text.alignleft") }
                }
                .buttonStyle(.bordered)
                .disabled(selectedFileURL == nil || isDirty || isFormatting)
                .help(isDirty ? "Guarda los cambios antes de formatear el archivo" : "Aplicar el formateador correspondiente a la extensión")
                Button { saveCurrentFile() } label: { Label("Guardar", systemImage: "square.and.arrow.down") }
                    .buttonStyle(.borderedProminent)
                    .disabled(!isDirty || selectedFileURL == nil)
                    .keyboardShortcut("s", modifiers: .command)
                Button { reloadCurrentFile() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.bordered)
                    .disabled(selectedFileURL == nil || isLoadingFile)
                    .help("Descartar cambios y volver a cargar del disco")
                Button { showInspector.toggle() } label: {
                    Image(systemName: showInspector ? "sidebar.right" : "sidebar.right")
                }
                .buttonStyle(.bordered)
                .help(showInspector ? "Ocultar inspector derecho" : "Mostrar inspector derecho")
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            Divider()
            if selectedFileURL == nil {
                ContentUnavailableView {
                    Label("Código del workspace", systemImage: "chevron.left.forwardslash.chevron.right")
                } description: {
                    Text("Abre archivos desde el explorador o pulsa un enlace de código de Endpoints HTTP, arquitectura o flujos de negocio.")
                } actions: {
                    Text("La edición se realiza sobre el archivo local seleccionado.").font(.caption).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                NativeCodeEditor(text: $editorText, caretOffset: $caretOffset, targetLine: targetLine, language: languageLabel)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color(nsColor: .textBackgroundColor))
            }
            Divider()
            HStack(spacing: 12) {
                Label(languageLabel, systemImage: "curlybraces").font(.caption2).foregroundStyle(.secondary)
                Text(isDirty ? "Modificado" : "Guardado").font(.caption2).foregroundStyle(isDirty ? .orange : .secondary)
                Spacer()
                Text("Línea \(targetLine)").font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary)
                if let selectedFileURL {
                    Text("UTF-8 · \(selectedFileURL.pathExtension.uppercased())")
                        .font(.system(size: 10, design: .monospaced)).foregroundStyle(.tertiary)
                }
                if let formatStatus {
                    Text(formatStatus).font(.caption2).foregroundStyle(.green).lineLimit(1)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
        }
        .background(Color(nsColor: .textBackgroundColor).opacity(0.92))
        .sheet(isPresented: $showCompletionPicker) {
            LanguageCompletionPickerView(
                status: languageServices.status,
                items: languageServices.completionItems,
                isLoading: languageServices.isRequestingCompletion,
                message: languageServices.lastMessage,
                onSelect: insertLanguageCompletion
            )
        }
    }

    private var inspectorPanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("Inspector", systemImage: "sidebar.right").font(.system(.headline, design: .rounded))
                Spacer()
                Button { showInspector = false } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
            }
            Divider()
            if selectedFileURL == nil {
                ContentUnavailableView("Sin selección", systemImage: "info.circle",
                                       description: Text("Selecciona un archivo para ver sus detalles y estructura."))
            } else {
                Picker("Inspector", selection: $inspectorTab) {
                    Text("Archivo").tag("Archivo")
                    Text("Símbolos · \(outlineSymbols.count)").tag("Símbolos")
                    Text("Problemas · \(languageServices.diagnostics.count)").tag("Diagnósticos")
                }
                .pickerStyle(.segmented)

                if inspectorTab == "Archivo", let selectedFileURL {
                    fileInspectorContent(selectedFileURL)
                } else if inspectorTab == "Símbolos" {
                    outlineInspectorContent
                } else {
                    languageDiagnosticsContent
                }
            }
            Spacer(minLength: 0)
        }
        .padding(13)
        .background(.ultraThinMaterial)
    }

    private func fileInspectorContent(_ selectedFileURL: URL) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text("ARCHIVO").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
                Text(selectedFileURL.lastPathComponent).font(.callout.weight(.semibold)).textSelection(.enabled)
                Text(selectedFileURL.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
            }
            inspectorValue("Lenguaje", languageLabel)
            inspectorValue("Extensión", fileExtension.isEmpty ? "(sin extensión)" : ".\(fileExtension)")
            inspectorValue("Ubicación", selectedRootPath ?? "Workspace")
            inspectorValue("Línea objetivo", "\(targetLine)")
            if let attributes = try? FileManager.default.attributesOfItem(atPath: selectedFileURL.path) {
                let bytes = (attributes[.size] as? NSNumber)?.int64Value ?? 0
                inspectorValue("Tamaño", ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file))
                if let modified = attributes[.modificationDate] as? Date {
                    inspectorValue("Modificado", modified.formatted(date: .abbreviated, time: .shortened))
                }
            }
            Divider()
            Text("ACCIONES").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
            Button { revealCurrentFile() } label: { Label("Mostrar en Finder", systemImage: "finder") }
                .buttonStyle(.bordered).frame(maxWidth: .infinity, alignment: .leading)
            Button { copyCurrentPath() } label: { Label("Copiar ruta", systemImage: "doc.on.doc") }
                .buttonStyle(.bordered).frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var outlineInspectorContent: some View {
        CodeOutlineInspectorView(symbols: outlineSymbols, searchText: $outlineSearch, targetLine: targetLine) { symbol in
            targetLine = symbol.line
            caretOffset = lineOffset(in: editorText, line: symbol.line)
        }
    }

    private var languageDiagnosticsContent: some View {
        LanguageDiagnosticsInspectorView(status: languageServices.status, diagnostics: languageServices.diagnostics) { diagnostic in
            targetLine = diagnostic.line
            caretOffset = lineOffset(in: editorText, line: diagnostic.line)
        }
    }

    private func inspectorValue(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label.uppercased()).font(.system(size: 9, weight: .bold)).foregroundStyle(.secondary)
            Text(value).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func refreshTreeAndSelection() async {
        isLoadingTree = true
        let roots = workspaceURLs
        let result = await Task.detached(priority: .utility) {
            CodeFileNode.loadTree(roots: roots)
        }.value
        treeRoots = result
        isLoadingTree = false
        if let requestedFilePath, !requestedFilePath.isEmpty { await loadRequestedSource() }
    }

    private func loadRequestedSource() async {
        guard let requestedFilePath, !requestedFilePath.isEmpty else { return }
        guard let resolved = resolve(path: requestedFilePath, rootPath: requestedRootPath) else {
            errorMessage = "No se encontró el archivo '\(requestedFilePath)' en las raíces del workspace."
            return
        }
        openResolvedSource(resolved.url, rootPath: resolved.root, line: requestedLine)
    }

    private func openResolvedSource(_ url: URL, rootPath: String?, line: Int) {
        if let selectedFileURL, selectedFileURL.standardizedFileURL.path == url.standardizedFileURL.path {
            targetLine = max(1, line)
            if let rootPath { selectedRootPath = rootPath }
            return
        }
        if isDirty {
            pendingOpen = PendingSource(url: url, rootPath: rootPath ?? url.deletingLastPathComponent().path, line: max(1, line))
            showDiscardConfirmation = true
            return
        }
        Task { await loadFile(url, rootPath: rootPath, line: line) }
    }

    private func resolve(path: String, rootPath: String?) -> (url: URL, root: String)? {
        if path.hasPrefix("/") {
            let absolute = URL(fileURLWithPath: path).standardizedFileURL
            if FileManager.default.fileExists(atPath: absolute.path) {
                let root = rootPath ?? workspaceURLs.first(where: { absolute.path.hasPrefix($0.path + "/") })?.path ?? absolute.deletingLastPathComponent().path
                return (absolute, root)
            }
        }
        let candidateRoots: [URL]
        if let rootPath {
            candidateRoots = [URL(fileURLWithPath: rootPath, isDirectory: true)] + workspaceURLs.filter { $0.path != rootPath }
        } else { candidateRoots = workspaceURLs }
        for root in candidateRoots {
            let direct = root.appendingPathComponent(path).standardizedFileURL
            if FileManager.default.fileExists(atPath: direct.path) { return (direct, root.path) }
            let components = path.split(separator: "/")
            if components.count > 1, WorkspaceSnapshotMerger.label(for: root).caseInsensitiveCompare(String(components[0])) == .orderedSame {
                let stripped = root.appendingPathComponent(components.dropFirst().joined(separator: "/")).standardizedFileURL
                if FileManager.default.fileExists(atPath: stripped.path) { return (stripped, root.path) }
            }
        }
        return nil
    }

    private func loadFile(_ url: URL, rootPath: String?, line: Int) async {
        isLoadingFile = true
        let path = url.path
        let result = await Task.detached(priority: .userInitiated) { () -> Result<String, Error> in
            do {
                let attributes = try FileManager.default.attributesOfItem(atPath: path)
                let bytes = (attributes[.size] as? NSNumber)?.int64Value ?? 0
                guard bytes <= 5 * 1024 * 1024 else {
                    throw NSError(domain: "CodeAtlas.Editor", code: 1, userInfo: [NSLocalizedDescriptionKey: "El archivo supera el límite de 5 MiB para esta primera versión del editor."])
                }
                return .success(try String(contentsOfFile: path, encoding: .utf8))
            } catch { return .failure(error) }
        }.value
        isLoadingFile = false
        switch result {
        case .success(let content):
            selectedFileURL = url
            selectedRootPath = rootPath
            editorText = content
            savedText = content
            languageServices.openDocument(
                url: url,
                rootURL: URL(fileURLWithPath: rootPath ?? url.deletingLastPathComponent().path, isDirectory: true),
                text: content
            )
            targetLine = max(1, line)
            caretOffset = lineOffset(in: content, line: targetLine)
            errorMessage = nil
        case .failure(let error):
            errorMessage = "No se pudo leer el archivo: \(error.localizedDescription)"
        }
    }

    private func requestOpen(_ path: String, root: String, line: Int) {
        guard resolve(path: path, rootPath: root) != nil else {
            errorMessage = "No se encontró el archivo '\(path)' en la raíz '\(root)'."
            return
        }
        requestedRootPath = root
        requestedLine = max(1, line)
        requestedFilePath = path
    }

    private func navigateToDefinition() async {
        guard selectedFileURL != nil else { return }
        guard let location = await languageServices.goToDefinition(text: editorText, utf16Offset: caretOffset) else {
            if let message = languageServices.lastMessage { errorMessage = message }
            return
        }
        let root = workspaceURLs.first(where: { location.url.standardizedFileURL.path.hasPrefix($0.standardizedFileURL.path + "/") })
            ?? URL(fileURLWithPath: selectedRootPath ?? location.url.deletingLastPathComponent().path, isDirectory: true)
        openResolvedSource(location.url, rootPath: root.path, line: location.line)
    }

    private func lineOffset(in source: String, line: Int) -> Int {
        let lines = (source as NSString).components(separatedBy: "\n")
        let bounded = min(max(1, line), max(1, lines.count))
        guard bounded > 1 else { return 0 }
        return (0..<(bounded - 1)).reduce(0) { $0 + (lines[$1] as NSString).length + 1 }
    }

    func lineNumber(in source: String, offset: Int) -> Int {
        let bounded = min(max(0, offset), (source as NSString).length)
        return max(1, ((source as NSString).substring(to: bounded).filter { $0 == "\n" }.count) + 1)
    }

    private func saveCurrentFile() {
        guard let selectedFileURL else { return }
        do {
            try editorText.write(to: selectedFileURL, atomically: true, encoding: .utf8)
            savedText = editorText
            languageServices.documentSaved()
        } catch {
            errorMessage = "No se pudo guardar el archivo: \(error.localizedDescription)"
        }
    }

    private func reloadCurrentFile() {
        guard let selectedFileURL else { return }
        Task { await loadFile(selectedFileURL, rootPath: selectedRootPath, line: targetLine) }
    }

    private func formatCurrentFile() async {
        guard !isDirty, let selectedFileURL else { return }
        let root = URL(fileURLWithPath: selectedRootPath ?? selectedFileURL.deletingLastPathComponent().path, isDirectory: true)
        isFormatting = true
        formatStatus = nil
        defer { isFormatting = false }
        do {
            let command = try CodeFormatter.command(for: selectedFileURL, workspaceRoot: root)
            _ = try await CommandRunner.run(executable: command.executable, arguments: command.arguments, at: command.workingDirectory)
            await loadFile(selectedFileURL, rootPath: selectedRootPath, line: targetLine)
            formatStatus = "Formato aplicado · \(command.executable)"
        } catch {
            errorMessage = "No se pudo formatear el archivo. Instala/configura el formateador requerido y comprueba que el archivo esté guardado. \(error.localizedDescription)"
        }
    }

    private func revealCurrentFile() {
        guard let selectedFileURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([selectedFileURL])
    }

    private func copyCurrentPath() {
        guard let selectedFileURL else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(selectedFileURL.path, forType: .string)
    }

    private func iconForFile(_ name: String) -> String {
        switch URL(fileURLWithPath: name).pathExtension.lowercased() {
        case "cs", "swift", "ts", "tsx", "js", "jsx": return "curlybraces"
        case "json", "jsonc": return "curlybraces.square"
        case "md", "markdown": return "doc.text"
        case "yml", "yaml", "toml", "ini": return "gearshape"
        case "http", "rest": return "network"
        case "csproj", "sln", "slnx", "props", "targets": return "shippingbox"
        case "png", "jpg", "jpeg", "gif", "svg", "webp": return "photo"
        default: return "doc"
        }
    }

    private func colorForFile(_ name: String) -> Color {
        switch URL(fileURLWithPath: name).pathExtension.lowercased() {
        case "cs", "swift", "ts", "tsx", "js", "jsx": return .orange
        case "json", "jsonc", "yml", "yaml", "toml": return .purple
        case "md", "markdown": return .blue
        case "http", "rest": return .green
        default: return .secondary
        }
    }
}
