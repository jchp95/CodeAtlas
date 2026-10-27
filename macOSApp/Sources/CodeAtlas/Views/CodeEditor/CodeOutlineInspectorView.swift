import SwiftUI

struct CodeOutlineInspectorView: View {
    let symbols: [CodeOutlineSymbol]
    @Binding var searchText: String
    let targetLine: Int
    let onSelect: (CodeOutlineSymbol) -> Void

    private var filteredSymbols: [CodeOutlineSymbol] {
        symbols.filter {
            searchText.isEmpty || $0.name.localizedCaseInsensitiveContains(searchText) || $0.kind.localizedCaseInsensitiveContains(searchText)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "list.bullet.indent").foregroundStyle(.secondary)
                TextField("Buscar símbolo…", text: $searchText).textFieldStyle(.plain)
            }
            .padding(8).background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 7))
            if symbols.isEmpty {
                ContentUnavailableView("Sin símbolos detectados", systemImage: "list.bullet.indent",
                    description: Text("El outline heurístico cubre declaraciones comunes. Para semántica completa hace falta un language server."))
                    .frame(minHeight: 160)
            } else if filteredSymbols.isEmpty {
                ContentUnavailableView.search(text: searchText)
                    .frame(minHeight: 120)
            } else {
                Text("OUTLINE · HEURÍSTICO").font(.system(size: 9, weight: .bold)).foregroundStyle(.secondary)
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(filteredSymbols) { symbol in
                            Button { onSelect(symbol) } label: {
                                HStack(spacing: 7) {
                                    Image(systemName: icon(for: symbol.kind))
                                        .foregroundStyle(symbol.kind == "type" ? Color.purple : Color.accentColor)
                                        .frame(width: 15)
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(symbol.name).font(.system(size: 11, weight: .medium, design: .monospaced)).lineLimit(1)
                                        Text(symbol.kind.uppercased()).font(.system(size: 8, weight: .bold)).foregroundStyle(.secondary)
                                    }
                                    Spacer(minLength: 4)
                                    Text("\(symbol.line)").font(.system(size: 9, design: .monospaced)).foregroundStyle(.tertiary)
                                }
                                .padding(.horizontal, 7).padding(.vertical, 6).contentShape(Rectangle())
                                .background(targetLine == symbol.line ? Color.accentColor.opacity(0.09) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
                .scrollIndicators(.visible)
            }
            Text("Los símbolos se calculan fuera del hilo principal y se actualizan al editar.")
                .font(.caption2).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func icon(for kind: String) -> String {
        switch kind {
        case "type": return "cube.transparent"
        case "heading": return "text.alignleft"
        case "key": return "key.horizontal"
        default: return "function"
        }
    }
}
