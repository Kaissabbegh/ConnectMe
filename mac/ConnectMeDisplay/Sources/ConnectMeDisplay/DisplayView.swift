import AppKit
import AVFoundation

/// Black full-window view: the video layer underneath, the pairing info on top when idle.
final class DisplayView: NSView {
    let videoLayer = AVSampleBufferDisplayLayer()
    private let title = DisplayView.label(size: 44, weight: .semibold)
    private let code = DisplayView.label(size: 120, weight: .bold, monospaced: true)
    private let details = DisplayView.label(size: 22, weight: .regular)
    private let status = DisplayView.label(size: 18, weight: .regular)
    private let stack = NSStackView()

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        videoLayer.videoGravity = .resizeAspect
        videoLayer.backgroundColor = NSColor.black.cgColor
        layer?.addSublayer(videoLayer)

        code.textColor = NSColor(calibratedRed: 0.35, green: 0.75, blue: 1.0, alpha: 1)
        details.textColor = NSColor(white: 0.75, alpha: 1)
        status.textColor = NSColor(white: 0.55, alpha: 1)

        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 18
        for v in [title, code, details, status] { stack.addArrangedSubview(v) }
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            stack.widthAnchor.constraint(lessThanOrEqualTo: widthAnchor, constant: -80),
        ])
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        videoLayer.frame = bounds
        CATransaction.commit()
    }

    func showIdle(name: String, pairingCode: String, addresses: [String], status statusText: String) {
        title.stringValue = "ConnectMe — \(name)"
        code.stringValue = pairingCode.map { String($0) }.joined(separator: " ")
        let ip = addresses.isEmpty ? "No network connection" : "IP address: " + addresses.joined(separator: "  ·  ")
        details.stringValue = "Open ConnectMe on your laptop, pick this iMac (or type the IP), and enter the code.\n\(ip)"
        status.stringValue = statusText
        stack.isHidden = false
    }

    func showVideo() {
        stack.isHidden = true
    }

    func setStatus(_ text: String) {
        status.stringValue = text
    }

    private static func label(size: CGFloat, weight: NSFont.Weight, monospaced: Bool = false) -> NSTextField {
        let l = NSTextField(labelWithString: "")
        l.font = monospaced ? NSFont.monospacedDigitSystemFont(ofSize: size, weight: weight)
                            : NSFont.systemFont(ofSize: size, weight: weight)
        l.textColor = .white
        l.alignment = .center
        l.maximumNumberOfLines = 0
        l.lineBreakMode = .byWordWrapping
        return l
    }
}
