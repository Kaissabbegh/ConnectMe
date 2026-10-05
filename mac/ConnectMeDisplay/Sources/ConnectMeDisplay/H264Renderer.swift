import AVFoundation
import CoreMedia

/// Turns Annex-B H.264 access units into CMSampleBuffers and shows them on an
/// AVSampleBufferDisplayLayer, which decodes in hardware (VideoToolbox) and displays immediately.
final class H264Renderer {
    let layer: AVSampleBufferDisplayLayer
    private var formatDescription: CMVideoFormatDescription?
    private var sps: [UInt8] = []
    private var pps: [UInt8] = []
    private var waitingForKeyframe = true

    init(layer: AVSampleBufferDisplayLayer) {
        self.layer = layer
    }

    /// Call on the main thread.
    func reset() {
        layer.flushAndRemoveImage()
        formatDescription = nil
        sps = []
        pps = []
        waitingForKeyframe = true
    }

    /// Call on the main thread with one complete access unit.
    func enqueue(accessUnit: Data) {
        var avcc = [UInt8]()
        avcc.reserveCapacity(accessUnit.count + 16)
        var hasIDR = false
        var newSPS: [UInt8]? = nil
        var newPPS: [UInt8]? = nil

        for nal in H264Renderer.splitNALUnits(accessUnit) {
            guard let header = nal.first else { continue }
            switch header & 0x1F {
            case 7: newSPS = nal
            case 8: newPPS = nal
            case 9: continue // access unit delimiter
            default:
                if header & 0x1F == 5 { hasIDR = true }
                let len = UInt32(nal.count)
                avcc.append(UInt8(len >> 24)); avcc.append(UInt8((len >> 16) & 0xFF))
                avcc.append(UInt8((len >> 8) & 0xFF)); avcc.append(UInt8(len & 0xFF))
                avcc.append(contentsOf: nal)
            }
        }

        if let s = newSPS, let p = newPPS, s != sps || p != pps {
            sps = s
            pps = p
            formatDescription = H264Renderer.makeFormat(sps: s, pps: p)
            layer.flush()
            waitingForKeyframe = true
        }

        if layer.status == .failed {
            NSLog("ConnectMe: display layer failed: \(String(describing: layer.error)); waiting for next keyframe")
            layer.flush()
            waitingForKeyframe = true
        }
        if waitingForKeyframe {
            if !hasIDR { return }
            waitingForKeyframe = false
        }
        guard let format = formatDescription, !avcc.isEmpty,
              let sample = H264Renderer.makeSample(avcc: avcc, format: format) else { return }
        layer.enqueue(sample)
    }

    // MARK: - Helpers

    static func splitNALUnits(_ data: Data) -> [[UInt8]] {
        let bytes = [UInt8](data)
        var units = [[UInt8]]()
        var starts = [(codeStart: Int, nalStart: Int)]()
        var i = 0
        while i + 2 < bytes.count {
            if bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1 {
                let codeStart = (i > 0 && bytes[i - 1] == 0) ? i - 1 : i
                starts.append((codeStart, i + 3))
                i += 3
            } else {
                i += 1
            }
        }
        for (idx, s) in starts.enumerated() {
            let end = idx + 1 < starts.count ? starts[idx + 1].codeStart : bytes.count
            if end > s.nalStart { units.append(Array(bytes[s.nalStart..<end])) }
        }
        return units
    }

    static func makeFormat(sps: [UInt8], pps: [UInt8]) -> CMVideoFormatDescription? {
        var format: CMVideoFormatDescription?
        // Explicit closure types: Swift 5.3 (Catalina) can't infer multi-statement closure results.
        let status: OSStatus = sps.withUnsafeBufferPointer { (spsPtr: UnsafeBufferPointer<UInt8>) -> OSStatus in
            return pps.withUnsafeBufferPointer { (ppsPtr: UnsafeBufferPointer<UInt8>) -> OSStatus in
                let pointers: [UnsafePointer<UInt8>] = [spsPtr.baseAddress!, ppsPtr.baseAddress!]
                let sizes: [Int] = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: pointers,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &format)
            }
        }
        if status != noErr {
            NSLog("ConnectMe: could not create H.264 format (\(status))")
            return nil
        }
        return format
    }

    static func makeSample(avcc: [UInt8], format: CMVideoFormatDescription) -> CMSampleBuffer? {
        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: avcc.count,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
            dataLength: avcc.count, flags: 0, blockBufferOut: &block)
        guard status == kCMBlockBufferNoErr, let blockBuffer = block else { return nil }
        status = avcc.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> OSStatus in
            return CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: blockBuffer,
                                          offsetIntoDestination: 0, dataLength: avcc.count)
        }
        guard status == kCMBlockBufferNoErr else { return nil }

        var sample: CMSampleBuffer?
        var size = avcc.count
        status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: blockBuffer, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
            sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample)
        guard status == noErr, let sampleBuffer = sample else { return nil }

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict,
                                 Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        return sampleBuffer
    }
}
