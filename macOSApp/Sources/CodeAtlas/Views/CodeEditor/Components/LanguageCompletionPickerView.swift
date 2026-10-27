import SwiftUI

struct LanguageCompletionPickerView: View {
    let status: LanguageServerStatus
    let items: [LanguageCompletionItem]
    let isLoading: Bool
    let message: String?
    let onSelect: (LanguageCompletionItem) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var filterText = ""

    private var filteredItems: [LanguageCompletionItem] {
        guard !filterText.isEmpty else { return items }
        return items.filter { $0.label.localizedCaseInsensitiveContains(filterText) || ($0.detail?.localizedCaseInsensitiveContains(filterText) ?? false) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Label("IntelliSense · Language Server", systemImage: "text.magnifyingglass").font(.title3.bold())
                Spacer()
                if isLoading { ProgressView().controlSize(.small) }
                Text(status.serverName).font(.caption).foregroundStyle(.secondary)
            }
            HStack(spacing: 7) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Filtrar sugerencias…", text: $filterText).textFieldStyle(.plain)
            }
            .padding(9).background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 8))
            if filteredItems.isEmpty {
                ContentUnavailableView(isLoading ? "Solicitando sugerencias…" : "Sin sugerencias",
                    systemImage: isLoading ? "ellipsis.circle" : "text.magnifyingglass",
                    description: Text(message ?? status.message))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(filteredItems) { item in
                    Button {
                        onSelect(item)
                        dismiss()
                    } label: {
                        HStack(spacing: 10) {
                            Image(systemName: icon(for: item.kind)).foregroundStyle(.tint).frame(width: 17)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.label).font(.system(size: 12, weight: .semibold, design: .monospaced))
                                if let detail = item.detail, !detail.isEmpty {
                                    Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                                }
                            }
                            Spacer()
                            Image(systemName: "return").font(.caption2).foregroundStyle(.tertiary)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
                .listStyle(.inset)
            }
            HStack {
                Text(status.message).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                Spacer()
                Button("Cerrar") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(minWidth: 560, minHeight: 430)
    }

    private func icon(for kind: Int?) -> String {
        switch kind {
        case 2: return "function"
        case 3, 4, 5, 6: return "curlybraces"
        case 7: return "character.cursor.ibeam"
        case 9, 10: return "text.alignleft"
        case 14: return "folder"
        case 15: return "file"
        case 17: return "constant"
        default: return "text.cursor"
        }
    }
}
