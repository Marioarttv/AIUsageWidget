// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "AIUsageWidget",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "AIUsageWidget", targets: ["AIUsageWidget"])],
    targets: [
        .target(name: "UsageCore"),
        .executableTarget(name: "AIUsageWidget", dependencies: ["UsageCore"]),
        .testTarget(name: "UsageCoreTests", dependencies: ["UsageCore"])
    ]
)
