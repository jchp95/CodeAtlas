import Foundation

extension Notification.Name {
    static let codeAtlasOpenSource = Notification.Name("CodeAtlas.OpenSource")
    static let codeAtlasOpenWorkspace = Notification.Name("CodeAtlas.OpenWorkspace")
    static let codeAtlasNavigateSection = Notification.Name("CodeAtlas.NavigateSection")
}

enum SourceNavigator {
    static func open(_ path: String, root: String? = nil, line: Int = 1) {
        NotificationCenter.default.post(
            name: .codeAtlasOpenSource,
            object: nil,
            userInfo: ["path": path, "root": root ?? "", "line": max(1, line)]
        )
    }
}
