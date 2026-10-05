import AppKit
import IOKit.pwr_mgt

final class AppDelegate: NSObject, NSApplicationDelegate, ServerDelegate {
    private var window: NSWindow!
    private var view: DisplayView!
    private var renderer: H264Renderer!
    private var server: Server!
    private var sleepAssertion: IOPMAssertionID = 0
    private var streaming = false
    private var statusText = "Starting..."
    private var framesThisSecond = 0
    private var statsTimer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()

        let screen = NSScreen.main ?? NSScreen.screens[0]
        let scale = screen.backingScaleFactor
        let pixelWidth = Int(screen.frame.width * scale)
        let pixelHeight = Int(screen.frame.height * scale)

        view = DisplayView(frame: screen.frame)
        renderer = H264Renderer(layer: view.videoLayer)
        window = NSWindow(contentRect: screen.frame, styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = "ConnectMe Display"
        window.backgroundColor = .black
        window.contentView = view
        window.collectionBehavior = [.fullScreenPrimary]
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        window.toggleFullScreen(nil)

        let name = Host.current().localizedName ?? "iMac"
        server = Server(computerName: name, screenWidth: pixelWidth, screenHeight: pixelHeight)
        server.delegate = self
        server.start()
        refreshIdle()

        // Re-read IP addresses now and then (Wi-Fi may reconnect with a new address).
        statsTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            if self.streaming {
                self.view.setStatus("\(self.framesThisSecond) fps")
                self.framesThisSecond = 0
            } else {
                self.refreshIdle()
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        server?.disconnectActive()
        allowDisplaySleep()
    }

    // MARK: - ServerDelegate

    func serverDidStartSession(laptopName: String) {
        streaming = true
        renderer.reset()
        view.showVideo()
        preventDisplaySleep()
        NSCursor.setHiddenUntilMouseMoves(true)
        NSLog("ConnectMe: streaming from \(laptopName)")
    }

    func serverDidReceive(accessUnit: Data, keyframe: Bool) {
        guard streaming else { return }
        framesThisSecond += 1
        renderer.enqueue(accessUnit: accessUnit)
    }

    func serverDidEndSession(reason: String) {
        streaming = false
        renderer.reset()
        allowDisplaySleep()
        statusText = reason
        refreshIdle()
    }

    func serverStatus(_ text: String) {
        statusText = text
        if !streaming { refreshIdle() }
    }

    // MARK: - Helpers

    private func refreshIdle() {
        view.showIdle(name: server.computerName, pairingCode: server.pairingCode,
                      addresses: AppDelegate.ipv4Addresses(), status: statusText)
    }

    private func preventDisplaySleep() {
        guard sleepAssertion == 0 else { return }
        IOPMAssertionCreateWithName(kIOPMAssertionTypeNoDisplaySleep as CFString,
                                    IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                    "ConnectMe is showing a laptop screen" as CFString,
                                    &sleepAssertion)
    }

    private func allowDisplaySleep() {
        guard sleepAssertion != 0 else { return }
        IOPMAssertionRelease(sleepAssertion)
        sleepAssertion = 0
    }

    @objc private func disconnect(_ sender: Any?) {
        server.disconnectActive()
    }

    private func buildMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Disconnect Laptop", action: #selector(disconnect(_:)), keyEquivalent: "d")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: "Quit ConnectMe Display", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let viewItem = NSMenuItem()
        main.addItem(viewItem)
        let viewMenu = NSMenu(title: "View")
        let fs = NSMenuItem(title: "Toggle Full Screen", action: #selector(NSWindow.toggleFullScreen(_:)), keyEquivalent: "f")
        fs.keyEquivalentModifierMask = [.command, .control]
        viewMenu.addItem(fs)
        viewItem.submenu = viewMenu
        NSApp.mainMenu = main
    }

    static func ipv4Addresses() -> [String] {
        var result = [String]()
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return result }
        defer { freeifaddrs(ifaddr) }
        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let p = ptr {
            let flags = Int32(p.pointee.ifa_flags)
            if let addr = p.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
               flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 {
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count),
                               nil, 0, NI_NUMERICHOST) == 0 {
                    let ip = String(cString: host)
                    if !ip.hasPrefix("169.254.") && !result.contains(ip) { result.append(ip) }
                }
            }
            ptr = p.pointee.ifa_next
        }
        return result
    }
}
