import Foundation

// See docs/PROTOCOL.md. Must match windows/ConnectMe.Sender/Protocol.cs.
enum Proto {
    static let version = 1
    static let port: UInt16 = 47800
    static let serviceType = "_connectme._tcp"
    static let maxMessage = 16 * 1024 * 1024
    static let flagKeyframe: UInt8 = 0x01
}

enum MsgType: UInt8 {
    case hello = 0x01
    case welcome = 0x02
    case reject = 0x03
    case video = 0x10
    case bye = 0x7F
}

struct HelloMsg: Codable {
    let version: Int
    let name: String
    let code: String
}

struct WelcomeMsg: Codable {
    let version: Int
    let name: String
    let width: Int
    let height: Int
}

struct RejectMsg: Codable {
    let reason: String
}

func frame(_ type: MsgType, _ payload: Data = Data()) -> Data {
    var out = Data(capacity: 5 + payload.count)
    let length = UInt32(payload.count + 1).bigEndian
    withUnsafeBytes(of: length) { out.append(contentsOf: $0) }
    out.append(type.rawValue)
    out.append(payload)
    return out
}

func frameJSON<T: Encodable>(_ type: MsgType, _ msg: T) -> Data {
    return frame(type, (try? JSONEncoder().encode(msg)) ?? Data())
}

extension Data {
    func readUInt32BE(at offset: Int) -> UInt32 {
        var v: UInt32 = 0
        for i in 0..<4 { v = (v << 8) | UInt32(self[self.startIndex + offset + i]) }
        return v
    }

    func readUInt64BE(at offset: Int) -> UInt64 {
        var v: UInt64 = 0
        for i in 0..<8 { v = (v << 8) | UInt64(self[self.startIndex + offset + i]) }
        return v
    }
}
