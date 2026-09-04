// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "PimTray",
    platforms: [.macOS(.v26)],
    products: [
        .library(name: "PimTrayCore", targets: ["PimTrayCore"]),
    ],
    targets: [
        .target(
            name: "PimTrayCore",
            path: "Sources/PimTrayCore",
            resources: [.process("Resources")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "PimTrayCoreTests",
            dependencies: ["PimTrayCore"],
            path: "Tests/PimTrayCoreTests",
            resources: [.copy("Fixtures")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
