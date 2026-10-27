import SwiftUI

struct BuildConsoleView: View {
    let workspaceURLs: [URL]
    @ObservedObject var model: BuildConsoleModel
    let onClose: () -> Void
    @State private var selectedRootPath = ""
    @State private var selectedTask: BuildConsoleTask = .dotnetBuild
    @State private var showCommandDetails = false

    private var selectedRoot: URL? {
        workspaceURLs.first(where: { $0.path == selectedRootPath }) ?? workspaceURLs.first
    }
    private var availableTasks: [BuildConsoleTask] {
        guard let selectedRoot else { return [] }
        return BuildConsoleTask.available(for: selectedRoot)
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 9) {
                Label("Build · Tests", systemImage: "hammer.fill")
                    .font(.system(.headline, design: .rounded))
                Picker("Workspace", selection: $selectedRootPath) {
                    ForEach(workspaceURLs, id: \.path) { root in
                        Text(root.lastPathComponent).tag(root.path)
                    }
                }
                .labelsHidden().frame(maxWidth: 210)
                .onChange(of: selectedRootPath) { _, _ in chooseAvailableTask() }
                Picker("Tarea", selection: $selectedTask) {
                    ForEach(availableTasks) { task in Text(task.title).tag(task) }
                }
                .labelsHidden().frame(maxWidth: 180)
                .disabled(availableTasks.isEmpty || model.isRunning)
                Button { runSelectedTask() } label: { Label("Ejecutar", systemImage: "play.fill") }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.isRunning || selectedRoot == nil || !availableTasks.contains(selectedTask))
                    .keyboardShortcut("b", modifiers: [.command, .shift])
                Button { model.cancel() } label: { Image(systemName: "stop.fill") }
                    .buttonStyle(.bordered).disabled(!model.isRunning).help("Cancelar proceso")
                Button { model.clear() } label: { Image(systemName: "trash") }
                    .buttonStyle(.bordered).disabled(model.isRunning).help("Limpiar salida")
                Spacer(minLength: 4)
                HStack(spacing: 6) {
                    Circle().fill(model.isRunning ? .orange : (model.exitCode == 0 ? .green : .secondary)).frame(width: 7, height: 7)
                    Text(model.statusText).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                Button { onClose() } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain).foregroundStyle(.secondary).help("Cerrar panel de compilación")
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            Divider()
            if let selectedRoot, availableTasks.isEmpty {
                ContentUnavailableView("No hay tareas detectadas", systemImage: "hammer",
                    description: Text("No se encontró una solución/proyecto .NET, scripts build/test/lint en package.json ni Package.swift en esta raíz o un nivel inferior."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if workspaceURLs.isEmpty {
                ContentUnavailableView("Abre un workspace", systemImage: "folder.badge.plus",
                    description: Text("La consola detectará tareas de compilación y pruebas en los repositorios abiertos."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        Text(model.outputText.isEmpty ? "Selecciona una tarea y pulsa Ejecutar. La salida y los errores aparecerán aquí." : model.outputText)
                            .font(.system(size: 11, design: .monospaced))
                            .foregroundStyle(.primary)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(12)
                            .id("build-console-output")
                    }
                    .background(Color(nsColor: .textBackgroundColor))
                    .onChange(of: model.outputText) { _, _ in proxy.scrollTo("build-console-output", anchor: .bottom) }
                }
            }
        }
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) { Rectangle().fill(Color.primary.opacity(0.09)).frame(height: 1) }
        .onAppear {
            if selectedRootPath.isEmpty { selectedRootPath = workspaceURLs.first?.path ?? "" }
            chooseAvailableTask()
        }
        .onChange(of: workspaceURLs.map(\.path).joined(separator: "|")) { _, _ in
            if !workspaceURLs.contains(where: { $0.path == selectedRootPath }) { selectedRootPath = workspaceURLs.first?.path ?? "" }
            chooseAvailableTask()
        }
    }

    private func chooseAvailableTask() {
        if !availableTasks.contains(selectedTask), let first = availableTasks.first { selectedTask = first }
    }

    private func runSelectedTask() {
        guard let selectedRoot, availableTasks.contains(selectedTask) else { return }
        model.start(task: selectedTask, root: selectedRoot)
    }
}
