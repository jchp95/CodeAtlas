import SwiftUI

@main
struct CodeAtlasApp: App {
    @StateObject private var vscodeBridge = VSCodeBridgeService.shared
    @AppStorage("appearanceMode") private var appearanceMode = "system"

    private var preferredScheme: ColorScheme? {
        switch appearanceMode {
        case "light": return .light
        case "dark": return .dark
        default: return nil
        }
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .preferredColorScheme(preferredScheme)
                .onAppear { vscodeBridge.start() }
        }
        .windowStyle(.automatic)
    }
}
