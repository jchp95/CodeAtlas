import SwiftUI

@main
struct CodeAtlasApp: App {
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
        }
        .windowStyle(.automatic)
    }
}
