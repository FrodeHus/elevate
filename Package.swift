// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "Elevate",
    platforms: [.macOS(.v26)],
    products: [
        .library(name: "ElevateCore", targets: ["ElevateCore"]),
    ],
    targets: [
        .target(
            name: "ElevateCore",
            path: "Sources/ElevateCore",
            resources: [.process("Resources")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "ElevateCoreTests",
            dependencies: ["ElevateCore"],
            path: "Tests/ElevateCoreTests",
            resources: [.copy("Fixtures")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
