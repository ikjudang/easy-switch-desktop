// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "EasySwitch",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "EasySwitchCore", targets: ["EasySwitchCore"]),
        .library(name: "EasySwitchTransport", targets: ["EasySwitchTransport"]),
        .executable(name: "EasySwitch", targets: ["EasySwitchApp"]),
        .executable(name: "EasySwitchService", targets: ["EasySwitchService"])
    ],
    targets: [
        .target(name: "EasySwitchCore"),
        .target(name: "EasySwitchTransport", linkerSettings: [.linkedFramework("IOKit")]),
        .target(name: "EasySwitchEngine", dependencies: ["EasySwitchCore", "EasySwitchTransport"]),
        .executableTarget(name: "EasySwitchService", dependencies: ["EasySwitchEngine"]),
        .executableTarget(name: "EasySwitchApp", dependencies: ["EasySwitchEngine", "EasySwitchCore", "EasySwitchTransport"],
                          resources: [.copy("Resources")], linkerSettings: [.linkedFramework("AppKit")]),
        .testTarget(name: "EasySwitchCoreTests", dependencies: ["EasySwitchCore"], resources: [.copy("Resources")]),
        .testTarget(name: "EasySwitchTransportTests", dependencies: ["EasySwitchTransport"]),
        .testTarget(name: "EasySwitchEngineTests", dependencies: ["EasySwitchEngine", "EasySwitchCore", "EasySwitchTransport"])
    ]
)
