import SwiftUI
import AppKit

private struct GitHubPullRequest: Decodable, Identifiable {
    let number: Int
    let title: String
    let state: String
    let url: String
    let headRefName: String
    var id: Int { number }
}

struct GitWorkbenchView: View {
    let workspaceURLs: [URL]
    @State private var selectedRootPath = ""
    @State private var currentBranch = ""
    @State private var selectedBranch = ""
    @State private var branches: [String] = []
    @State private var statusLines: [String] = []
    @State private var historyLines: [String] = []
    @State private var diffText = ""
    @State private var githubText = ""
    @State private var pullRequests: [GitHubPullRequest] = []
    @State private var isLoading = false
    @State private var isLoadingGitHub = false
    @State private var errorMessage: String?
    @State private var showCheckoutConfirmation = false
    @State private var pendingCheckoutBranch = ""
    @State private var selectedTab = "Cambios"

    private var selectedRoot: URL? { workspaceURLs.first(where: { $0.path == selectedRootPath }) ?? workspaceURLs.first }
    private var hasChanges: Bool { !statusLines.isEmpty }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if workspaceURLs.isEmpty {
                ContentUnavailableView("Abre un workspace Git", systemImage: "arrow.triangle.branch", description: Text("Añade una o varias raíces de repositorio para inspeccionar ramas, cambios locales y pull requests."))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                HSplitView {
                    repositorySidebar.frame(minWidth: 220, idealWidth: 270, maxWidth: 360)
                    detailPanel.frame(minWidth: 500, maxWidth: .infinity, maxHeight: .infinity)
                }
            }
        }
        .background(.clear)
        .task { if selectedRootPath.isEmpty { selectedRootPath = workspaceURLs.first?.path ?? "" }; await refresh() }
        .onChange(of: workspaceURLs.map(\.path).joined(separator: "|")) { _, _ in
            if !workspaceURLs.contains(where: { $0.path == selectedRootPath }) { selectedRootPath = workspaceURLs.first?.path ?? "" }
            Task { await refresh() }
        }
        .onChange(of: selectedRootPath) { _, _ in Task { await refresh() } }
        .confirmationDialog("Cambiar de rama", isPresented: $showCheckoutConfirmation, titleVisibility: .visible) {
            Button("Cambiar a \(pendingCheckoutBranch)", role: .destructive) { Task { await checkoutBranch(pendingCheckoutBranch) } }
            Button("Cancelar", role: .cancel) { pendingCheckoutBranch = "" }
        } message: { Text(hasChanges ? "Hay cambios locales. Git evitará la operación si puede sobrescribirlos; revisa el estado antes de confirmar." : "Se cambiará la rama activa del repositorio seleccionado.") }
        .alert("Git", isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })) {
            Button("Aceptar", role: .cancel) { errorMessage = nil }
        } message: { Text(errorMessage ?? "Error desconocido") }
    }

    private var header: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Git · Repositorios").font(.system(.title2, design: .rounded).bold())
                Text("Ramas locales, cambios, diff, historial e integración GitHub CLI sin ejecutar comandos mediante shell.")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            if let selectedRoot {
                Picker("Repositorio", selection: $selectedRootPath) {
                    ForEach(workspaceURLs, id: \.path) { Text($0.lastPathComponent).tag($0.path) }
                }.labelsHidden().frame(maxWidth: 210)
                Text(currentBranch.isEmpty ? selectedRoot.lastPathComponent : currentBranch)
                    .font(.system(.caption, design: .monospaced)).padding(.horizontal, 9).padding(.vertical, 5).background(.quaternary, in: Capsule())
            }
            Button { Task { await refresh() } } label: { Label("Actualizar", systemImage: "arrow.clockwise") }
                .buttonStyle(.bordered).disabled(isLoading)
        }.padding(.horizontal, 22).padding(.vertical, 13).background(.ultraThinMaterial)
    }

    private var repositorySidebar: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Ramas", systemImage: "arrow.triangle.branch").font(.headline)
            if branches.isEmpty && !isLoading {
                ContentUnavailableView("Sin ramas", systemImage: "arrow.triangle.branch", description: Text("La carpeta seleccionada puede no ser un repositorio Git.")).frame(minHeight: 120)
            } else {
                List {
                    ForEach(branches, id: \.self) { branch in
                        Button { selectedBranch = branch } label: {
                            HStack(spacing: 7) {
                                Image(systemName: branch == currentBranch ? "checkmark.circle.fill" : (branch == selectedBranch ? "largecircle.fill.circle" : "arrow.triangle.branch"))
                                    .foregroundStyle(branch == currentBranch ? .green : (branch == selectedBranch ? Color.accentColor : .secondary))
                                Text(branch).font(.system(.callout, design: .monospaced)).lineLimit(1)
                                Spacer(minLength: 0)
                            }.contentShape(Rectangle())
                        }.buttonStyle(.plain).help(branch == currentBranch ? "Rama actual" : "Seleccionar rama")
                    }
                }.listStyle(.sidebar)
                Button { requestCheckout() } label: { Label("Cambiar a rama", systemImage: "arrow.turn.up.right") }
                    .buttonStyle(.borderedProminent).frame(maxWidth: .infinity)
                    .disabled(isLoading || selectedBranch.isEmpty || selectedBranch == currentBranch)
            }
            Divider()
            Label("Historial reciente", systemImage: "clock.arrow.circlepath").font(.headline)
            ForEach(historyLines.prefix(12).indices, id: \.self) { index in
                Text(historyLines[index]).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(2).textSelection(.enabled)
            }
            Spacer(minLength: 0)
        }.padding(12).background(.ultraThinMaterial)
    }

    private var detailPanel: some View {
        VStack(alignment: .leading, spacing: 0) {
            Picker("Vista", selection: $selectedTab) {
                Text("Cambios").tag("Cambios")
                Text("Diff").tag("Diff")
                Text("GitHub").tag("GitHub")
            }.pickerStyle(.segmented).padding(12)
            Divider()
            switch selectedTab {
            case "Diff": diffPanel
            case "GitHub": githubPanel
            default: changesPanel
            }
        }
    }

    private var changesPanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Cambios de trabajo").font(.title3.bold())
                Spacer()
                Text("\(statusLines.count) archivo(s)").font(.caption).foregroundStyle(.secondary)
            }
            if statusLines.isEmpty {
                ContentUnavailableView("Árbol de trabajo limpio", systemImage: "checkmark.circle", description: Text("No hay cambios detectados en el repositorio seleccionado.")).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(statusLines.indices, id: \.self) { index in
                        let line = statusLines[index]
                        let status = String(line.prefix(2))
                        let path = String(line.dropFirst(3))
                        HStack(spacing: 10) {
                            Text(status).font(.system(.caption, design: .monospaced).bold()).foregroundStyle(statusColor(status)).frame(width: 30)
                            Image(systemName: iconForFile(path)).foregroundStyle(.secondary)
                            Text(path).font(.system(.callout, design: .monospaced)).lineLimit(1)
                            Spacer()
                            Button { SourceNavigator.open(path, root: selectedRoot?.path, line: 1) } label: { Image(systemName: "arrow.up.forward.square") }
                                .buttonStyle(.plain).help("Abrir archivo en CodeAtlas")
                            Button { Task { await showDiff(for: path) } } label: { Image(systemName: "doc.text.magnifyingglass") }
                                .buttonStyle(.plain).help("Ver diff de este archivo")
                        }.padding(.vertical, 3)
                    }
                }.listStyle(.inset)
            }
            Spacer(minLength: 0)
        }.padding(16)
    }

    private var diffPanel: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack { Text("Diff local y staged").font(.title3.bold()); Spacer(); Button("Actualizar diff") { Task { await loadDiff() } }.buttonStyle(.bordered) }
            ScrollView {
                Text(diffText.isEmpty ? "Selecciona un archivo en Cambios o actualiza el diff para ver las diferencias." : diffText)
                    .font(.system(size: 11, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(12)
            }.background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        }.padding(14)
    }

    private var githubPanel: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack { Text("GitHub").font(.title3.bold()); Spacer(); Button { Task { await loadGitHub() } } label: { Label("Comprobar conexión", systemImage: "arrow.clockwise") }.buttonStyle(.borderedProminent).disabled(isLoadingGitHub) }
            Text(githubText.isEmpty ? "Requiere GitHub CLI (`gh`) instalado y autenticado con `gh auth login`. CodeAtlas no almacena tokens personales." : githubText)
                .font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
            if !pullRequests.isEmpty {
                Text("Pull requests").font(.headline)
                ForEach(pullRequests) { pr in
                    HStack(alignment: .top, spacing: 9) {
                        Image(systemName: pr.state.lowercased() == "open" ? "arrow.triangle.pull" : "checkmark.circle")
                            .foregroundStyle(pr.state.lowercased() == "open" ? .green : .secondary)
                        VStack(alignment: .leading, spacing: 4) {
                            Text("#\(pr.number) · \(pr.title)").font(.callout.weight(.semibold))
                            Text("\(pr.headRefName) · \(pr.state)").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Abrir") { if let url = URL(string: pr.url) { NSWorkspace.shared.open(url) } }.buttonStyle(.bordered)
                    }.padding(10).atlasGlassCard(cornerRadius: 10)
                }
            }
            Spacer(minLength: 0)
        }.padding(16)
    }

    private func refresh() async {
        guard !isLoading, let root = selectedRoot else { return }
        isLoading = true; defer { isLoading = false }
        do {
            let branchOutput = try await EngineClient.runGit(["branch", "--format=%(refname:short)"], at: root)
            branches = branchOutput.split(separator: "\n").map(String.init)
            currentBranch = try await EngineClient.runGit(["branch", "--show-current"], at: root)
            selectedBranch = currentBranch
            let status = try await EngineClient.runGit(["status", "--porcelain=v1", "--untracked-files=all"], at: root)
            statusLines = status.isEmpty ? [] : status.split(separator: "\n").map(String.init)
            let history = try await EngineClient.runGit(["log", "-12", "--oneline", "--decorate"], at: root)
            historyLines = history.isEmpty ? [] : history.split(separator: "\n").map(String.init)
            await loadDiff()
        } catch { branches = []; statusLines = []; historyLines = []; errorMessage = error.localizedDescription }
    }

    private func requestCheckout() {
        guard !selectedBranch.isEmpty, selectedBranch != currentBranch else { return }
        pendingCheckoutBranch = selectedBranch
        showCheckoutConfirmation = true
    }

    private func checkoutBranch(_ branch: String) async {
        guard let root = selectedRoot else { return }
        isLoading = true; defer { isLoading = false }
        do {
            _ = try await EngineClient.runGit(["checkout", branch], at: root)
            errorMessage = nil
            await refreshAfterCheckout()
        } catch { errorMessage = "Git no pudo cambiar a '\(branch)'. No se han descartado cambios por parte de CodeAtlas. \(error.localizedDescription)" }
    }

    private func refreshAfterCheckout() async {
        guard let root = selectedRoot else { return }
        do {
            currentBranch = try await EngineClient.runGit(["branch", "--show-current"], at: root)
            selectedBranch = currentBranch
            let status = try await EngineClient.runGit(["status", "--porcelain=v1", "--untracked-files=all"], at: root)
            statusLines = status.isEmpty ? [] : status.split(separator: "\n").map(String.init)
            historyLines = try await EngineClient.runGit(["log", "-12", "--oneline", "--decorate"], at: root).split(separator: "\n").map(String.init)
            await loadDiff()
        } catch { errorMessage = error.localizedDescription }
    }

    private func loadDiff() async {
        guard let root = selectedRoot else { return }
        let unstaged = (try? await EngineClient.runGit(["diff", "--"], at: root)) ?? ""
        let staged = (try? await EngineClient.runGit(["diff", "--cached", "--"], at: root)) ?? ""
        var sections: [String] = []
        if !staged.isEmpty { sections.append("# STAGED\n" + staged) }
        if !unstaged.isEmpty { sections.append("# UNSTAGED\n" + unstaged) }
        diffText = sections.joined(separator: "\n\n")
    }

    private func showDiff(for path: String) async {
        guard let root = selectedRoot else { return }
        let unstaged = (try? await EngineClient.runGit(["diff", "--", path], at: root)) ?? ""
        let staged = (try? await EngineClient.runGit(["diff", "--cached", "--", path], at: root)) ?? ""
        var sections: [String] = []
        if !staged.isEmpty { sections.append("# STAGED · \(path)\n" + staged) }
        if !unstaged.isEmpty { sections.append("# UNSTAGED · \(path)\n" + unstaged) }
        diffText = sections.joined(separator: "\n\n")
        selectedTab = "Diff"
    }

    private func loadGitHub() async {
        guard let root = selectedRoot else { return }
        isLoadingGitHub = true; defer { isLoadingGitHub = false }
        do {
            _ = try await CommandRunner.run(executable: "gh", arguments: ["auth", "status"], at: root)
            githubText = try await CommandRunner.run(executable: "gh", arguments: ["repo", "view", "--json", "nameWithOwner,url"], at: root)
            let output = try await CommandRunner.run(executable: "gh", arguments: ["pr", "list", "--limit", "30", "--json", "number,title,state,url,headRefName"], at: root)
            pullRequests = (try? JSONDecoder().decode([GitHubPullRequest].self, from: Data(output.utf8))) ?? []
            if pullRequests.isEmpty { githubText += "\n\nNo hay pull requests abiertos que mostrar." }
        } catch {
            pullRequests = []
            githubText = "No se pudo consultar GitHub CLI. Instala `gh`, ejecuta `gh auth login` y confirma que la raíz tiene un remoto GitHub.\n\nDetalle: \(error.localizedDescription)"
        }
    }

    private func statusColor(_ value: String) -> Color {
        if value.contains("A") || value.contains("?") { return .green }
        if value.contains("D") { return .red }
        return .orange
    }
    private func iconForFile(_ path: String) -> String {
        switch URL(fileURLWithPath: path).pathExtension.lowercased() {
        case "cs", "swift", "ts", "tsx", "js", "jsx": return "curlybraces"
        case "json", "yml", "yaml", "toml": return "gearshape"
        case "md": return "doc.text"
        default: return "doc"
        }
    }
}
