import AVFoundation
import Foundation

/// BG-9i — the whole reason the monitor survives on iOS.
///
/// iOS suspends an idle background app within seconds. What keeps this one alive with the screen off
/// and the app unfocused is that it is *playing audio*: the `audio` background mode (Info.plist) plus
/// an **active `.playback` audio session**. The feed's own audio engine (core's `AppleAudioOutput`)
/// plays through this session — but it is torn down and rebuilt on every reconnect, and between
/// attempts there would be a gap with nothing playing, long enough for iOS to suspend the app and
/// stop the reconnect and the watchdog dead. So this holds an **independent, always-on inaudible
/// keep-alive** for the whole time monitoring runs: the session never goes idle, whatever the feed is
/// doing, and background reconnect (BG-6) and the watchdog (WATCH-2) keep working.
///
/// `.playback` also **ignores the silent switch**, which is what makes the alarm audible on a silenced
/// phone (ALRM-10i).
final class MonitorAudio {
    private let session = AVAudioSession.sharedInstance()
    // var, not let: a media-services reset invalidates the engine, and the only fix is a fresh one.
    private var engine = AVAudioEngine()
    private var player = AVAudioPlayerNode()
    private var silence: AVAudioPCMBuffer?
    private var running = false
    private var onSessionLost: ((Bool) -> Void)?

    /// Activate the session and start the keep-alive. Idempotent. `onSessionLost(true)` fires if an
    /// interruption ends but the session cannot be reclaimed — the monitor has lost its ears, and the
    /// shell must say so (WATCH-11) rather than appear to run.
    func begin(onSessionLost: @escaping (Bool) -> Void) {
        self.onSessionLost = onSessionLost
        guard !running else { return }
        running = true
        registerObservers()
        activateAndPlay()
    }

    func end() {
        guard running else { return }
        running = false
        NotificationCenter.default.removeObserver(self)
        player.stop()
        engine.stop()
        try? session.setActive(false, options: [.notifyOthersOnDeactivation])
        Log.info("audio", "audio session released — monitoring stopped keeping it alive")
    }

    private func activateAndPlay() {
        do {
            // .playback: keeps playing in the background, on the lock screen, and over the silent
            // switch (ALRM-10i). No .mixWithOthers — the monitor is the primary audio at night.
            try session.setCategory(.playback, mode: .default, options: [])
            try session.setActive(true)
        } catch {
            Log.error("audio", "could not activate the audio session: \(error.localizedDescription)")
            onSessionLost?(true)
            return
        }
        startKeepAlive()
        onSessionLost?(false)
        Log.info("audio", "audio session active (.playback) — the monitor will stay alive in the background")
    }

    /// A looping buffer of silence on its own engine, decoupled from the feed's, so a dead feed or a
    /// reconnect gap can never let the session go idle. It must never crash the monitor to start it
    /// (a crash here is a dead monitor), so every step that can fail is guarded and, if the keep-alive
    /// truly cannot run, the session is reported lost (BG-9i) rather than dying in silence.
    private func startKeepAlive() {
        var format = engine.outputNode.inputFormat(forBus: 0)
        if format.channelCount == 0 || format.sampleRate == 0 {
            // The output graph has not been pulled yet and reports a null format; a known-good one
            // lets the keep-alive start rather than crash on a nil buffer.
            format = AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 2)
                ?? engine.mainMixerNode.outputFormat(forBus: 0)
        }
        guard let buffer = silence(matching: format) else {
            Log.error("audio", "keep-alive could not build a silence buffer — reporting the session lost")
            onSessionLost?(true)
            return
        }

        if !engine.attachedNodes.contains(player) {
            engine.attach(player)
        }
        engine.connect(player, to: engine.mainMixerNode, format: buffer.format)
        engine.mainMixerNode.outputVolume = 0
        do {
            if !engine.isRunning { try engine.start() }
            player.scheduleBuffer(buffer, at: nil, options: [.loops], completionHandler: nil)
            player.play()
        } catch {
            Log.error("audio", "keep-alive engine would not start: \(error.localizedDescription)")
            onSessionLost?(true)
        }
    }

    /// A half-second of silence in the given format, cached and rebuilt if the format changed.
    private func silence(matching format: AVAudioFormat) -> AVAudioPCMBuffer? {
        if let existing = silence, existing.format == format { return existing }
        let frames = AVAudioFrameCount(format.sampleRate * 0.5)
        guard frames > 0, let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return nil }
        buf.frameLength = frames // zero-filled — silence
        silence = buf
        return buf
    }

    // MARK: - Interruptions, resets and route changes (IOS-4 / BG-9i)

    private func registerObservers() {
        let nc = NotificationCenter.default
        nc.removeObserver(self)
        nc.addObserver(self, selector: #selector(handleInterruption(_:)),
                       name: AVAudioSession.interruptionNotification, object: session)
        // Both of these would otherwise kill the keep-alive silently and let iOS suspend the monitor:
        // a media-services reset (which can happen overnight) invalidates the engine, and a route
        // change — a Bluetooth speaker dropping — can stop `.playback`. Rebuild rather than die quietly.
        nc.addObserver(self, selector: #selector(handleMediaReset(_:)),
                       name: AVAudioSession.mediaServicesWereResetNotification, object: nil)
        nc.addObserver(self, selector: #selector(handleRouteChange(_:)),
                       name: AVAudioSession.routeChangeNotification, object: session)
    }

    /// A media-services reset leaves the engine object dead — a fresh one is the only fix — so rebuild
    /// it and reclaim the session. If it will not come back, report it (BG-9i / WATCH-11).
    @objc private func handleMediaReset(_ note: Notification) {
        guard running else { return }
        Log.warn("audio", "media services were reset — rebuilding the audio engine")
        engine = AVAudioEngine()
        player = AVAudioPlayerNode()
        silence = nil
        reclaim(attempt: 0)
    }

    /// A route change that stopped playback (the old device went away) — restart the keep-alive so the
    /// session stays hot. The engine survives a route change; it just needs to be pulled again.
    @objc private func handleRouteChange(_ note: Notification) {
        guard running, !engine.isRunning else { return }
        Log.info("audio", "audio route changed and playback stopped — restarting the keep-alive")
        reclaim(attempt: 0)
    }

    /// A phone call or Siri interrupts the audio; when the interruption ends we reclaim the session so
    /// the monitor resumes on its own. If it will not come back, we report it (BG-9i / WATCH-11).
    @objc private func handleInterruption(_ note: Notification) {
        guard let info = note.userInfo,
              let raw = info[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: raw)
        else { return }

        switch type {
        case .began:
            Log.warn("audio", "audio interrupted — monitoring paused until it ends")
        case .ended:
            guard running else { return }
            let options = (info[AVAudioSessionInterruptionOptionKey] as? UInt).map(AVAudioSession.InterruptionOptions.init)
            Log.info("audio", "audio interruption ended (shouldResume=\(options?.contains(.shouldResume) ?? false)) — reclaiming the session")
            reclaim(attempt: 0)
        @unknown default:
            break
        }
    }

    /// Reclaiming can fail transiently (the interrupting app has not fully let go). Retry a few times
    /// before declaring the session lost, so a two-second call does not read as a dead monitor.
    private func reclaim(attempt: Int) {
        guard running else { return }
        do {
            try session.setActive(true)
            startKeepAlive()
            onSessionLost?(false)
            Log.info("audio", "audio session reclaimed after interruption")
        } catch {
            if attempt < 5 {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.reclaim(attempt: attempt + 1) }
            } else {
                Log.error("audio", "could not reclaim the audio session after the interruption — monitoring is down")
                onSessionLost?(true)
            }
        }
    }
}
