import AVFoundation
import AVKit
import BabyMonitorCore
import CoreMedia
import SwiftUI
import UIKit
import VideoToolbox

/// The phone's picture: H.265 Annex-B → CMSampleBuffer → AVSampleBufferDisplayLayer, decoded on the
/// GPU by VideoToolbox — the same path the Mac uses, because the hardware already knows HEVC.
///
/// LIVE-7, and the whole contract of this file: **nothing here may throw or take audio down with
/// it.** Video is best-effort. A black picture is a disappointment; a dead alarm is the thing this
/// project exists to prevent. Every failure path ends in "log it and wait for the next keyframe".
///
/// The parameter-set caching and keyframe gating are NOT here — they are in core, so every platform's
/// decoder is fed the same way. By the time `configure` is called, core has seen a complete
/// VPS/SPS/PPS set and a keyframe.
final class VideoLayerView: UIView {
    private let displayLayer = AVSampleBufferDisplayLayer()
    private var formatDescription: CMVideoFormatDescription?
    private var pipController: AVPictureInPictureController?

    /// BG-19: whether the picture floats when the parent leaves the app. Toggling it only flips the
    /// controller's auto-start — the audio, the alarm and the watchdog are unaffected either way.
    var pipEnabled = true {
        didSet { pipController?.canStartPictureInPictureAutomaticallyFromInline = pipEnabled }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
        // The picture never handles touches: the tap-to-toggle-chrome gesture (LIVE-11) lives on the
        // SwiftUI layer above, and a UIView that swallowed the touch would force a transparent tap
        // sheet to be stacked over the whole screen — which then competed with the control bar's menus.
        isUserInteractionEnabled = false
        displayLayer.videoGravity = .resizeAspect // show the whole crib — never crop a baby out
        layer.addSublayer(displayLayer)
        setUpPictureInPicture()
    }

    /// BG-18: a system picture-in-picture window over the parent's other work, when the OS supports
    /// it. It rides on the same sample-buffer display layer the feed already draws to, and the same
    /// `.playback` audio session that keeps the monitor alive in the background — so it floats a
    /// picture and changes **nothing** about the audio, the alarm or the watchdog. With
    /// `canStartPictureInPictureAutomaticallyFromInline`, leaving the app while the feed plays hands the
    /// picture to a floating window; a device that does not support PiP simply never gets one.
    private func setUpPictureInPicture() {
        guard AVPictureInPictureController.isPictureInPictureSupported() else {
            // The iOS Simulator returns false here — Apple does not implement PiP there — so on the
            // Simulator the floating window can never appear no matter the code. It works on a device.
            Log.info("video", "picture-in-picture is not supported here (e.g. the Simulator) — no floating window")
            return
        }
        let source = AVPictureInPictureController.ContentSource(
            sampleBufferDisplayLayer: displayLayer,
            playbackDelegate: self
        )
        let controller = AVPictureInPictureController(contentSource: source)
        controller.canStartPictureInPictureAutomaticallyFromInline = pipEnabled // BG-19
        pipController = controller
        Log.info("video", "picture-in-picture set up (BG-18); float-on-leave is \(pipEnabled ? "on" : "off")")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("not used") }

    override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true) // a resize must not animate a live feed
        displayLayer.frame = bounds
        CATransaction.commit()
    }

    // MARK: - Decoding

    func configure(vps: Data, sps: Data, pps: Data) {
        // LIVE-7: never crash. An empty parameter set yields a nil base address below; core is meant to
        // hand a complete set, but a local guard makes the promise the file's own rather than upstream's.
        guard !vps.isEmpty, !sps.isEmpty, !pps.isEmpty else {
            Log.warn("video", "empty VPS/SPS/PPS — waiting for a complete parameter set")
            return
        }
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
        displayLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil) // a fresh stream: throw away anything still held

        let dimensions = CMVideoFormatDescriptionGetDimensions(description)
        Log.info("video", "HEVC format described — \(dimensions.width)×\(dimensions.height), decoding")
    }

    func decode(annexB: Data, ptsMs: Int64) {
        guard let formatDescription else { return } // core configures before it ever sends a frame
        guard let avcc = Self.annexBToLengthPrefixed(annexB) else { return }

        // CoreMedia allocates and owns the bytes, and we copy the frame in. Pointing a block buffer at
        // our own Data with kCFAllocatorNull would hand the decoder memory freed the moment we return.
        let length = avcc.count
        var blockBuffer: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: length,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil,
            offsetToData: 0, dataLength: length,
            flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &blockBuffer
        ) == kCMBlockBufferNoErr, let blockBuffer else { return }

        let copyStatus = avcc.withUnsafeBytes { bytes -> OSStatus in
            guard let base = bytes.baseAddress else { return -1 }
            return CMBlockBufferReplaceDataBytes(with: base, blockBuffer: blockBuffer, offsetIntoDestination: 0, dataLength: length)
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
            allocator: kCFAllocatorDefault, dataBuffer: blockBuffer, formatDescription: formatDescription,
            sampleCount: 1, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
            sampleSizeEntryCount: 1, sampleSizeArray: &sampleSize, sampleBufferOut: &sampleBuffer
        ) == noErr, let sampleBuffer else { return }

        // Live view: render as fast as it arrives. The feed already drops its own backlog upstream
        // (LIVE-8), so there is nothing here to catch up on.
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

        if displayLayer.sampleBufferRenderer.status == .failed {
            Log.warn("video", "display layer failed — restarting it (audio unaffected)")
            displayLayer.sampleBufferRenderer.flush()
        }
        displayLayer.sampleBufferRenderer.enqueue(sampleBuffer)
    }

    func reset() {
        formatDescription = nil
        displayLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
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
                if bytes[i + 2] == 1 { nalStarts.append(i + 3); i += 3; continue }
                if i + 3 < bytes.count, bytes[i + 2] == 0, bytes[i + 3] == 1 { nalStarts.append(i + 4); i += 4; continue }
            }
            i += 1
        }
        guard !nalStarts.isEmpty else { return nil }
        for (index, start) in nalStarts.enumerated() {
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

/// BG-18: a live feed's answers to PiP's playback questions. There is no timeline to scrub, nothing
/// to pause and nothing to skip — a baby monitor plays, always, and PiP must not put a pause button or
/// a scrubber on it.
extension VideoLayerView: AVPictureInPictureSampleBufferPlaybackDelegate {
    func pictureInPictureController(_ controller: AVPictureInPictureController, setPlaying playing: Bool) {}

    func pictureInPictureControllerTimeRangeForPlayback(_ controller: AVPictureInPictureController) -> CMTimeRange {
        // An unbounded range reads as "live": PiP hides the scrubber and the play/pause affordances
        // that would only mislead on a feed with no timeline.
        CMTimeRange(start: .zero, duration: .positiveInfinity)
    }

    func pictureInPictureControllerIsPlaybackPaused(_ controller: AVPictureInPictureController) -> Bool {
        false // the feed is never paused; pausing is not a thing a baby monitor can do
    }

    func pictureInPictureController(
        _ controller: AVPictureInPictureController,
        didTransitionToRenderSize newRenderSize: CMVideoDimensions
    ) {}

    func pictureInPictureController(
        _ controller: AVPictureInPictureController,
        skipByInterval skipInterval: CMTime,
        completion completionHandler: @escaping () -> Void
    ) {
        completionHandler() // nothing to skip on a live feed
    }

    func pictureInPictureControllerShouldProhibitBackgroundAudioPlayback(
        _ controller: AVPictureInPictureController
    ) -> Bool {
        // The monitor's audio is the whole point — PiP must never silence it. Our audio does not come
        // from these video sample buffers; it comes from the feed's own engine and must keep playing in
        // the background exactly as it does without PiP (BG-3/9i). So: never prohibit it.
        false
    }
}

/// The Kotlin side of the bridge. Core owns the bitstream logic and calls these three methods.
final class VideoRendererBridge: NSObject, VideoRenderer {
    private weak var view: VideoLayerView?
    init(view: VideoLayerView) { self.view = view }

    func configure(vps: Data, sps: Data, pps: Data) {
        DispatchQueue.main.async { [weak self] in self?.view?.configure(vps: vps, sps: sps, pps: pps) }
    }
    func decode(annexB: Data, ptsMs: Int64) {
        DispatchQueue.main.async { [weak self] in self?.view?.decode(annexB: annexB, ptsMs: ptsMs) }
    }
    func tearDown() {
        DispatchQueue.main.async { [weak self] in self?.view?.reset() }
    }
}

/// The picture as a SwiftUI view. Core pushes frames into the renderer; this owns the layer.
struct VideoSurface: UIViewRepresentable {
    /// BG-19: whether leaving the app floats the picture. The view applies it to the PiP controller.
    let pipEnabled: Bool

    func makeUIView(context: Context) -> VideoLayerView {
        let view = VideoLayerView(frame: .zero)
        view.pipEnabled = pipEnabled
        let bridge = VideoRendererBridge(view: view)
        context.coordinator.bridge = bridge
        AppleVideo.shared.renderer = bridge
        Log.info("video", "video surface created")
        return view
    }

    func updateUIView(_ view: VideoLayerView, context: Context) {
        view.pipEnabled = pipEnabled // BG-19: reflect a settings change onto the live controller
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    static func dismantleUIView(_ view: VideoLayerView, coordinator: Coordinator) {
        // LIVE-7: the picture goes away, audio does not. Core simply stops having anywhere to draw.
        if AppleVideo.shared.renderer === coordinator.bridge {
            AppleVideo.shared.renderer = nil
        }
        Log.info("video", "video surface torn down")
    }

    final class Coordinator { var bridge: VideoRendererBridge? }
}
