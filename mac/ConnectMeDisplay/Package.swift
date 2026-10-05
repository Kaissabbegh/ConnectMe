// swift-tools-version:5.3
// Builds with the Command Line Tools on macOS 10.15 Catalina (the newest macOS a 2013 iMac officially runs).
import PackageDescription

let package = Package(
    name: "ConnectMeDisplay",
    platforms: [.macOS(.v10_15)],
    targets: [
        .target(
            name: "ConnectMeDisplay",
            path: "Sources/ConnectMeDisplay",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("Network"),
                .linkedFramework("IOKit"),
            ]
        ),
    ]
)
