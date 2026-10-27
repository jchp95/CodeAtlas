// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodeAtlas",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "CodeAtlas", targets: ["CodeAtlas"])],
    targets: [.executableTarget(name: "CodeAtlas")],
    swiftLanguageModes: [.v5]
)
