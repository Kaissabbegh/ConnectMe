import Foundation
import Network

protocol ServerDelegate: AnyObject {
    func serverDidStartSession(laptopName: String)
    func serverDidReceive(accessUnit: Data, keyframe: Bool)
    func serverDidEndSession(reason: String)
    func serverStatus(_ text: String)
}

/// Listens on TCP 47800, advertises _connectme._tcp over Bonjour, and runs one session at a time.
/// All delegate calls arrive on the main queue.
final class Server {
    weak var delegate: ServerDelegate?
    var pairingCode: String
    let computerName: String
    let screenWidth: Int
    let screenHeight: Int

    private var listener: NWListener?
    private var active: Session?
    private var wrongAttempts = 0
    private let queue = DispatchQueue(label: "connectme.server")

    init(computerName: String, screenWidth: Int, screenHeight: Int) {
        self.computerName = computerName
        self.screenWidth = screenWidth
        self.screenHeight = screenHeight
        self.pairingCode = Server.newCode()
    }

    static func newCode() -> String {
        return String(format: "%04d", Int.random(in: 0...9999))
    }

    func start() {
        do {
            let params = NWParameters.tcp
            params.allowLocalEndpointReuse = true
            let listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: Proto.port)!)
            let txt = NetService.data(fromTXTRecord: [
                "v": Data("\(Proto.version)".utf8),
                "name": Data(computerName.utf8),
                "w": Data("\(screenWidth)".utf8),
                "h": Data("\(screenHeight)".utf8),
            ])
            listener.service = NWListener.Service(name: computerName, type: Proto.serviceType, domain: nil, txtRecord: txt)
            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    self?.status("Ready. Waiting for a laptop...")
                case .failed(let error):
                    self?.status("Network error: \(error). Retrying...")
                    self?.listener?.cancel()
                    self?.queue.asyncAfter(deadline: .now() + 3) { self?.start() }
                default:
                    break
                }
            }
            listener.newConnectionHandler = { [weak self] conn in self?.accept(conn) }
            listener.start(queue: queue)
            self.listener = listener
        } catch {
            status("Could not open port \(Proto.port): \(error)")
        }
    }

    private func accept(_ conn: NWConnection) {
        if let current = active, !current.ended {
            // Busy: politely refuse the second laptop.
            conn.start(queue: queue)
            conn.send(content: frameJSON(.reject, RejectMsg(reason: "This iMac is already in use")),
                      completion: .contentProcessed { _ in conn.cancel() })
            return
        }
        let session = Session(connection: conn, server: self, queue: queue)
        active = session
        session.start()
    }

    // Called by Session on `queue`.
    fileprivate func checkCode(_ code: String) -> Bool {
        if code == pairingCode {
            wrongAttempts = 0
            return true
        }
        // A 4-digit code is guessable, so rotate it after a few misses.
        wrongAttempts += 1
        if wrongAttempts >= 5 {
            wrongAttempts = 0
            pairingCode = Server.newCode()
        }
        return false
    }

    fileprivate func sessionStarted(_ name: String) {
        DispatchQueue.main.async { self.delegate?.serverDidStartSession(laptopName: name) }
    }

    fileprivate func sessionVideo(_ au: Data, _ key: Bool) {
        DispatchQueue.main.async { self.delegate?.serverDidReceive(accessUnit: au, keyframe: key) }
    }

    fileprivate func sessionEnded(_ session: Session, reason: String, wasPaired: Bool) {
        if active === session { active = nil }
        if wasPaired { pairingCode = Server.newCode() }
        DispatchQueue.main.async { self.delegate?.serverDidEndSession(reason: reason) }
    }

    func disconnectActive() {
        queue.async { self.active?.close(reason: "Disconnected on the iMac", lastMessage: frame(.bye)) }
    }

    private func status(_ text: String) {
        DispatchQueue.main.async { self.delegate?.serverStatus(text) }
    }
}

private final class Session {
    let connection: NWConnection
    unowned let server: Server
    let queue: DispatchQueue
    private(set) var ended = false
    private var paired = false
    private var lastData = Date()
    private var timer: DispatchSourceTimer?

    init(connection: NWConnection, server: Server, queue: DispatchQueue) {
        self.connection = connection
        self.server = server
        self.queue = queue
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state { self?.close(reason: "Connection failed: \(error)", lastMessage: nil) }
        }
        connection.start(queue: queue)
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 1, repeating: 1)
        t.setEventHandler { [weak self] in
            guard let self = self else { return }
            if Date().timeIntervalSince(self.lastData) > 5 {
                self.close(reason: self.paired ? "The laptop stopped sending" : "Pairing timed out", lastMessage: frame(.bye))
            }
        }
        t.resume()
        timer = t
        readHeader()
    }

    private func readHeader() {
        connection.receive(minimumIncompleteLength: 5, maximumLength: 5) { [weak self] data, _, isComplete, error in
            guard let self = self, !self.ended else { return }
            guard let header = data, header.count == 5 else {
                self.close(reason: error.map { "Connection lost: \($0)" } ?? "The laptop disconnected", lastMessage: nil)
                return
            }
            let length = Int(header.readUInt32BE(at: 0))
            guard length >= 1, length <= Proto.maxMessage else {
                self.close(reason: "Bad data from laptop", lastMessage: nil)
                return
            }
            let type = header[header.startIndex + 4]
            if length == 1 {
                self.handle(type: type, payload: Data())
                if !isComplete { self.readHeader() }
                return
            }
            self.readPayload(type: type, length: length - 1)
        }
    }

    private func readPayload(type: UInt8, length: Int) {
        connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] data, _, _, error in
            guard let self = self, !self.ended else { return }
            guard let payload = data, payload.count == length else {
                self.close(reason: error.map { "Connection lost: \($0)" } ?? "The laptop disconnected", lastMessage: nil)
                return
            }
            self.handle(type: type, payload: payload)
            if !self.ended { self.readHeader() }
        }
    }

    private func handle(type: UInt8, payload: Data) {
        lastData = Date()
        guard let msg = MsgType(rawValue: type) else { return }
        switch msg {
        case .hello:
            guard !paired, let hello = try? JSONDecoder().decode(HelloMsg.self, from: payload) else { return }
            guard server.checkCode(hello.code) else {
                close(reason: "\(hello.name) used a wrong code",
                      lastMessage: frameJSON(.reject, RejectMsg(reason: "Wrong pairing code")))
                return
            }
            paired = true
            let welcome = WelcomeMsg(version: Proto.version, name: server.computerName,
                                     width: server.screenWidth, height: server.screenHeight)
            connection.send(content: frameJSON(.welcome, welcome), completion: .contentProcessed { _ in })
            server.sessionStarted(hello.name)
        case .video:
            guard paired, payload.count > 9 else { return }
            let key = payload[payload.startIndex + 8] & Proto.flagKeyframe != 0
            server.sessionVideo(payload.subdata(in: (payload.startIndex + 9)..<payload.endIndex), key)
        case .bye:
            close(reason: "The laptop disconnected", lastMessage: nil)
        default:
            break
        }
    }

    /// Ends the session, optionally sending one last message (BYE or REJECT) before closing.
    func close(reason: String, lastMessage: Data?) {
        if ended { return }
        ended = true
        timer?.cancel()
        if let last = lastMessage {
            connection.send(content: last, completion: .contentProcessed { [connection] _ in connection.cancel() })
        } else {
            connection.cancel()
        }
        server.sessionEnded(self, reason: reason, wasPaired: paired)
    }
}
