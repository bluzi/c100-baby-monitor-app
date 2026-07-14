import AVFoundation
import AppKit
import BabyMonitorCore
import CoreMedia
import VideoToolbox

/// The Mac's picture: H.265 Annex-B → CMSampleBuffer → AVSampleBufferDisplayLayer, which decodes
/// on the GPU via VideoToolbox. No FFmpeg, no bundled codec — the hardware already knows HEVC.
///
/// LIVE-7, and it is the whole contract of this file: **nothing here may throw or take audio down
/// with it.** Video is best-effort. A black picture is a disappointment; a dead alarm is the thing
/// this project exists to prevent. Every failure path below ends in "log it and wait for the next
/// keyframe", never in an error that reaches the monitor.
///
/// The parameter-set caching and keyframe gating are NOT here — they are in core, because every
/// platform's decoder needs exactly the same thing and it should not be re-derived per platform.
/// By the time `configure` is called, core has seen a complete VPS/SPS/PPS set and a keyframe.
final class VideoLayerView: NSView {
    private let displayLayer = AVSampleBufferDisplayLayer()
    private var formatDescription: CMVideoFormatDescription?

    /// MACOS-19: the size of the picture the camera is actually sending, reported as soon as it is
    /// known, so the window can take the camera's shape and stop framing it in black bars.
    var onVideoSize: ((CGSize) -> Void)?

    /// MACOS-5: the mini shape is a rounded, borderless tile, and its corners have to be rounded
    /// *here* — a video layer is an AppKit citizen and a SwiftUI `clipShape` around it leaves four
    /// square black corners poking out of the tile and out of its shadow.
    var cornerRadius: CGFloat = 0 {
        didSet {
            guard cornerRadius != oldValue else { return }
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            layer?.cornerRadius = cornerRadius
            layer?.masksToBounds = cornerRadius > 0
            displayLayer.cornerRadius = cornerRadius
            displayLayer.masksToBounds = cornerRadius > 0
            CATransaction.commit()
        }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer = CALayer()
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.cornerCurve = .continuous // the squircle, not a rounded rectangle
        displayLayer.videoGravity = .resizeAspect
        displayLayer.cornerCurve = .continuous
        layer?.addSublayer(displayLayer)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("not used") }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true) // a resize must not animate a live feed
        displayLayer.frame = bounds
        CATransaction.commit()
    }

    // MARK: - Decoding

    func configure(vps: Data, sps: Data, pps: Data) {
        var description: CMVideoFormatDescription?
        let status = vps.withUnsafeBytes { vpsBytes in
            sps.withUnsafeBytes { spsBytes in
                pps.withUnsafeBytes { ppsBytes in
                    let pointers: [UnsafePointer<UInt8>] = [
                        vpsBytes.bindMemory(to: UInt8.self).baseAddress!,
                        spsBytes.bindMemory(to: UInt8.self).baseAddress!,
                        ppsBytes.bindMemory(to: UInt8.self).baseAddress!,
                    ]
                    let sizes = [vps.count, sps.count, pps.count]
                    return CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        allocator: kCFAllocatorDefault,
                        parameterSetCount: 3,
                        parameterSetPointers: pointers,
                        parameterSetSizes: sizes,
                        nalUnitHeaderLength: 4,
                        extensions: nil,
                        formatDescriptionOut: &description
                    )
                }
            }
        }
        guard status == noErr, let description else {
            Log.warn("video", "could not build a format description from VPS/SPS/PPS (\(status))")
            return
        }
        formatDescription = description
        // A fresh stream: throw away anything the layer was still holding.
        displayLayer.flushAndRemoveImage()

        // MACOS-19: the camera's own shape, straight from the bitstream's parameter sets. The window
        // takes this shape, and the letterbox bars simply stop existing.
        let dimensions = CMVideoFormatDescriptionGetDimensions(description)
        if dimensions.width > 0, dimensions.height > 0 {
            let size = CGSize(width: CGFloat(dimensions.width), height: CGFloat(dimensions.height))
            Log.info("video", "HEVC format described — \(dimensions.width)×\(dimensions.height), decoding")
            onVideoSize?(size)
        } else {
            Log.info("video", "HEVC format described — decoding")
        }
    }

    func decode(annexB: Data, ptsMs: Int64) {
        guard let formatDescription else { return } // core configures before it ever sends a frame

        // VideoToolbox wants length-prefixed NALs, not Annex-B start codes.
        guard let avcc = Self.annexBToLengthPrefixed(annexB) else { return }

        // CoreMedia allocates and owns the bytes, and we copy the frame in. The obvious shortcut —
        // pointing a block buffer at our own Data with kCFAllocatorNull — hands the decoder memory
        // that is freed the moment this function returns.
        let length = avcc.count
        var blockBuffer: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: length,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: length,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &blockBuffer
        ) == kCMBlockBufferNoErr, let blockBuffer else { return }

        let copyStatus = avcc.withUnsafeBytes { bytes -> OSStatus in
            guard let base = bytes.baseAddress else { return -1 }
            return CMBlockBufferReplaceDataBytes(
                with: base,
                blockBuffer: blockBuffer,
                offsetIntoDestination: 0,
                dataLength: length
            )
        }
        guard copyStatus == kCMBlockBufferNoErr else { return }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: ptsMs, timescale: 1000),
            decodeTimeStamp: .invalid
        )
        var sampleSize = length
        var sampleBuffer: CMSampleBuffer?
        guard CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        ) == noErr, let sampleBuffer else { return }

        // Live view: render as fast as it arrives rather than scheduling against a clock. The feed
        // already drops its own backlog upstream (LIVE-8), so there is nothing here to catch up on.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0
        {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(
                dict,
                Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                Unmanaged.passUnretained(kCFBooleanTrue).toOpaque()
            )
        }

        if displayLayer.status == .failed {
            Log.warn("video", "display layer failed — restarting it (audio unaffected)")
            displayLayer.flush()
        }
        displayLayer.enqueue(sampleBuffer)
    }

    func reset() {
        formatDescription = nil
        displayLayer.flushAndRemoveImage()
    }

    /// Annex-B (00 00 01 / 00 00 00 01 start codes) → 4-byte big-endian length prefixes.
    private static func annexBToLengthPrefixed(_ input: Data) -> Data? {
        var output = Data()
        output.reserveCapacity(input.count + 8)

        let bytes = [UInt8](input)
        var nalStarts: [Int] = []
        var i = 0
        while i + 2 < bytes.count {
            if bytes[i] == 0, bytes[i + 1] == 0 {
                if bytes[i + 2] == 1 {
                    nalStarts.append(i + 3)
                    i += 3
                    continue
                }
                if i + 3 < bytes.count, bytes[i + 2] == 0, bytes[i + 3] == 1 {
                    nalStarts.append(i + 4)
                    i += 4
                    continue
                }
            }
            i += 1
        }
        guard !nalStarts.isEmpty else { return nil }

        for (index, start) in nalStarts.enumerated() {
            // A NAL runs to the start code of the next one — minus that start code itself.
            let rawEnd = index + 1 < nalStarts.count ? nalStarts[index + 1] : bytes.count
            var end = rawEnd
            if index + 1 < nalStarts.count {
                end = rawEnd - 3
                if end > start, end - 1 >= 0, bytes[end - 1] == 0 { end -= 1 } // 4-byte start code
            }
            guard end > start else { continue }
            let length = UInt32(end - start).bigEndian
            withUnsafeBytes(of: length) { output.append(contentsOf: $0) }
            output.append(contentsOf: bytes[start..<end])
        }
        return output.isEmpty ? nil : output
    }
}

/// The Kotlin side of the bridge. Core owns the bitstream logic and calls these two methods.
final class VideoRendererBridge: NSObject, VideoRenderer {
    private weak var view: VideoLayerView?

    init(view: VideoLayerView) {
        self.view = view
    }

    func configure(vps: Data, sps: Data, pps: Data) {
        DispatchQueue.main.async { [weak self] in
            self?.view?.configure(vps: vps, sps: sps, pps: pps)
        }
    }

    func decode(annexB: Data, ptsMs: Int64) {
        DispatchQueue.main.async { [weak self] in
            self?.view?.decode(annexB: annexB, ptsMs: ptsMs)
        }
    }

    func tearDown() {
        DispatchQueue.main.async { [weak self] in
            self?.view?.reset()
        }
    }
}
