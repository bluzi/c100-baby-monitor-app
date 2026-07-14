import AppKit
import BabyMonitorCore
import Combine
import SwiftUI

/// MACOS-14. **The monitor is one window.**
///
/// It wears two shapes — *full*, an ordinary Mac window you can work in and take full screen, and
/// *mini*, a small tile that floats over everything else — and it changes between them the way a
/// window changes size: in place, without going away and coming back.
///
/// This is not a cosmetic choice. Two windows, each with its own video surface, is what the app
/// had, and it meant two renderers fighting over one feed: whichever was created last got the
/// frames and the other went black. One window has one video layer, which is never torn down, so
/// switching shape cannot black out the picture, cannot drop audio, and cannot make the camera
/// reconnect. The parent sees the tile grow into a window. Nothing restarts (MACOS-14).
///
/// The shapes are also the only place in this app where AppKit is asked to do something unusual, so
/// the reasoning is written down: a *titled* window's title bar would sit on top of the mini shape's
/// controls and would zoom the tile on a stray double-click, so the mini shape is **borderless** —
/// and a borderless window must be told it may become key, must draw its own rounded corners, and
/// must be given back the ⌘W that AppKit only grants to windows with a close button.
final class MonitorWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    /// A borderless window has no close button, so AppKit's `performClose` beeps instead of closing
    /// it. The mini shape is still a window, and ⌘W must still close it — closing which, as ever,
    /// stops nothing (MACOS-7, BG-5).
    override func performClose(_ sender: Any?) {
        if delegate?.windowShouldClose?(self) == false { return }
        close()
    }
}

@MainActor
final class MonitorWindowController: NSObject, NSWindowDelegate {
    private let state: AppState
    private let window: MonitorWindow
    private let display = DisplayWake()
    private var cancellables = Set<AnyCancellable>()

    /// The shape currently on screen. Nil until the first `apply` — which is what makes the initial
    /// layout take the same path as every later one.
    private var currentShape: WindowShape?

    /// A shape change asked for while the window is full screen: macOS will not restyle a window
    /// mid-full-screen, so it is applied when the transition finishes.
    private var pendingShape: WindowShape?

    /// True while the window is being morphed, so the frames AppKit reports on the way through are
    /// not mistaken for a size the user chose (MACOS-14: each shape remembers its own).
    private var morphing = false

    /// Told whenever the window appears or disappears, so the app can decide whether it is still an
    /// app with windows (MACOS-12) or has receded into the menu bar.
    var onVisibilityChanged: () -> Void = {}

    init(state: AppState) {
        self.state = state
        window = MonitorWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1040, height: 650),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        super.init()

        window.title = "Baby Monitor"
        window.isReleasedWhenClosed = false // MACOS-7: closing it is not destroying it
        window.delegate = self
        window.appearance = NSAppearance(named: .darkAqua) // UI-1
        window.acceptsMouseMovedEvents = true // LIVE-11m: the chrome follows the pointer
        window.tabbingMode = .disallowed // one monitor; tabs would be a way to hide it behind itself

        let hosting = NSHostingView(rootView: RootView().environmentObject(state))
        // The window sizes the content, never the other way round. Left to itself, NSHostingView
        // hands SwiftUI's ideal size to the window as a constraint — so the window *grew* when an
        // alarm banner appeared, and would have fought the mini shape for its 384 points. A monitor
        // window that resizes itself the moment something goes wrong is a monitor window that moves
        // the Acknowledge button out from under a half-asleep parent's pointer.
        hosting.sizingOptions = []
        window.contentView = hosting

        observe()
        apply(shape: state.shape, animated: false)
    }

    var isVisible: Bool { window.isVisible }

    /// Only the visual harness needs this (see `Preview`); nothing in the app reaches into the
    /// window from outside the controller.
    var nsWindow: NSWindow { window }

    // MARK: - Showing and hiding

    /// MACOS-12: a window you cannot Cmd-Tab to is a window you have to go hunting for, and at 3am
    /// that is the difference between glancing at the baby and giving up. An `.accessory` app is
    /// excluded from the switcher, so the app is only `.accessory` when it has nothing to switch
    /// *to* — the policy follows the windows, and the monitor is untouched by any of it (BG-5).
    func show() {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
        // The alpha the fade rule asks for *now* — not whatever it was when the window was last
        // closed. A window that comes back half-faded because of where the pointer used to be would
        // be a window that looks broken.
        applyAlpha(animated: false)
        onVisibilityChanged()
        updateDisplayWake()
    }

    func hide() {
        window.close()
    }

    // MARK: - The two shapes (MACOS-5, MACOS-6, MACOS-14)

    private func apply(shape: WindowShape, animated: Bool) {
        guard currentShape != shape else { return }

        // Full screen is a shape of its own as far as AppKit is concerned: leave it first, and pick
        // the change back up in windowDidExitFullScreen.
        if window.styleMask.contains(.fullScreen) {
            pendingShape = shape
            window.toggleFullScreen(nil)
            return
        }

        if let old = currentShape {
            Prefs.setFrame(window.frame, for: old) // each shape remembers its own (MACOS-14)
        }
        currentShape = shape

        switch shape {
        case .full: configureFull()
        case .mini: configureMini()
        }

        let target = frame(for: shape)
        morphing = true
        let settle = { [weak self] in
            guard let self else { return }
            self.morphing = false
            self.window.invalidateShadow() // the shape changed under the shadow
            self.applyAspectPolicy() // MACOS-19: and it settles into the camera's shape
            self.applyAlpha(animated: true)
        }

        if animated, window.isVisible, !state.reduceMotion {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.3
                context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                window.animator().setFrame(target, display: true)
            } completionHandler: {
                settle()
            }
        } else {
            window.setFrame(target, display: true)
            settle()
        }
        Log.info("ui", "window shape → \(shape.rawValue)")
    }

    private func configureFull() {
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
        window.titlebarAppearsTransparent = true // the video runs under the traffic lights (LIVE-9m)
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = false
        window.level = .normal
        window.collectionBehavior = [.fullScreenPrimary] // MACOS-6: it can go full screen
        window.backgroundColor = .black
        window.isOpaque = true
        window.hasShadow = true
        window.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        window.alphaValue = 1
        applyAspectPolicy()
    }

    // MARK: - The camera's shape (MACOS-19)

    /// The window wears the video's aspect ratio, so the picture fills it and the black bars stop
    /// existing. This is what QuickTime and every other video window on this platform does, and it
    /// is better than the alternatives: cropping to fill would quietly cut off the top and bottom of
    /// the cot, and a blurred filler behind the bars would mean decoding every frame twice, all
    /// night, to draw something nobody is looking at.
    ///
    /// Full screen is the one place it cannot hold — the shape there is the screen's — and there the
    /// unused area is plain black, as it is everywhere else on the platform.
    private var aspect: CGFloat {
        state.videoAspect > 0 ? state.videoAspect : 16.0 / 9.0
    }

    /// Who gets to be the camera's shape, and who does not: the *feed* does, in either shape. The
    /// sign-in form and the camera picker live in this same window (MACOS-14) and are not pictures —
    /// locking a form to 16:9 would be absurd — so there the window is free again.
    ///
    /// Split in two on purpose. The **constraints** must be in place *before* a morph moves the
    /// window, or AppKit clamps the tile back to the full window's minimum size. The **fit** must
    /// happen *after* it, or the window snaps to its new height and then animates from there, which
    /// looks exactly as broken as it sounds.
    private func applyAspectConstraints() {
        guard let shape = currentShape, !window.styleMask.contains(.fullScreen) else { return }

        switch shape {
        case .mini:
            window.contentAspectRatio = NSSize(width: aspect, height: 1)
            window.minSize = NSSize(width: 240, height: (240 / aspect).rounded())
            window.maxSize = NSSize(width: 960, height: (960 / aspect).rounded())
        case .full where state.ui.screen == "viewer":
            window.contentAspectRatio = NSSize(width: aspect, height: 1)
            window.minSize = NSSize(width: 640, height: (640 / aspect).rounded())
        case .full:
            // Clearing an aspect-ratio lock is done by setting a resize increment; there is no
            // other way to say "no ratio" to AppKit.
            window.contentResizeIncrements = NSSize(width: 1, height: 1)
            window.minSize = NSSize(width: 640, height: 460)
        }
    }

    private func applyAspectPolicy() {
        applyAspectConstraints()
        guard currentShape != nil,
              !window.styleMask.contains(.fullScreen),
              state.ui.screen == "viewer" || currentShape == .mini
        else {
            return
        }
        fitFrameToAspect()
    }

    /// Reshape the window *now*, rather than waiting for the user to drag an edge — and keep its
    /// top-left corner where it is, because a window that jumps across the screen when the feed
    /// comes up is a window that has moved out from under the pointer reaching for it.
    private func fitFrameToAspect() {
        guard let content = window.contentView, content.bounds.width > 0 else { return }
        let chromeHeight = window.frame.height - content.bounds.height
        let wantedContentHeight = (content.bounds.width / aspect).rounded()
        guard abs(wantedContentHeight - content.bounds.height) > 1 else { return }

        var frame = window.frame
        let wantedFrameHeight = wantedContentHeight + chromeHeight
        frame.origin.y = frame.maxY - wantedFrameHeight
        frame.size.height = wantedFrameHeight

        morphing = true // this is not a size the user chose; do not remember it as one mid-flight
        window.setFrame(frame, display: true)
        morphing = false
        rememberFrame()
        window.invalidateShadow()
    }

    private func configureMini() {
        window.styleMask = [.borderless, .resizable]
        window.isMovableByWindowBackground = true // drag it anywhere by its picture
        window.level = .floating // MACOS-5: above ordinary windows
        // Over full-screen apps and across spaces (BG-7m): the glance must never require leaving
        // what you are doing.
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.backgroundColor = .clear // its corners are rounded, so its background cannot be opaque
        window.isOpaque = false
        window.hasShadow = true
        window.hidesOnDeactivate = false
        applyAspectPolicy() // MACOS-19: the tile is the camera's shape, not a guess at it
    }

    // MARK: - Fading (MACOS-16)

    /// The window's own alpha, from core's rule. Not "how transparent do we feel like being" — the
    /// decision that a monitor may recede at all belongs to `MacShell.miniOpacity`, which knows when
    /// something needs attention and refuses.
    private func applyAlpha(animated: Bool) {
        let target = currentShape == .mini ? CGFloat(state.miniAlpha) : 1.0
        guard abs(window.alphaValue - target) > 0.005 else { return }
        if animated, !state.reduceMotion {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.2
                window.animator().alphaValue = target
            }
        } else {
            window.alphaValue = target
        }
    }

    // MARK: - Frames (MACOS-14: each shape remembers its own)

    private func frame(for shape: WindowShape) -> NSRect {
        if let saved = Prefs.frame(shape), NSScreen.screens.contains(where: { $0.visibleFrame.intersects(saved) }) {
            return saved
        }
        return defaultFrame(for: shape)
    }

    private func defaultFrame(for shape: WindowShape) -> NSRect {
        let visible = (window.screen ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        switch shape {
        case .full:
            let width = min(1040, visible.width - 80)
            // MACOS-19: born the camera's shape, so the very first frame lands without a jump.
            let height = state.ui.screen == "viewer"
                ? min((width / aspect).rounded(), visible.height - 80)
                : min(650, visible.height - 80)
            return NSRect(
                x: visible.midX - width / 2,
                y: visible.midY - height / 2,
                width: width,
                height: height
            )
        case .mini:
            // Bottom right, out of the way of a menu bar and of most work — and where every other
            // floating video tile on this platform puts itself.
            let width: CGFloat = 384
            let height = (width / aspect).rounded()
            return NSRect(
                x: visible.maxX - width - 24,
                y: visible.minY + 24,
                width: width,
                height: height
            )
        }
    }

    // MARK: - LIVE-14: the display stays awake, but only while there is something to look at

    private func updateDisplayWake() {
        display.set(window.isVisible && state.ui.status == "live")
    }

    // MARK: - Observation

    private func observe() {
        // The shape is a function of what the user asked for and what is on screen — core decides
        // (sign-in is never shown in a tile), so both inputs are watched.
        Publishers.CombineLatest(
            state.$ui.map(\.screen).removeDuplicates(),
            state.$preferredShape.removeDuplicates()
        )
        .receive(on: RunLoop.main)
        .sink { [weak self] _, _ in
            guard let self else { return }
            self.apply(shape: self.state.shape, animated: true)
            // Signing in is not a picture and the feed is: the window is free for one and takes the
            // camera's shape for the other, and moving between them changes which (MACOS-19).
            self.applyAspectPolicy()
        }
        .store(in: &cancellables)

        state.$miniAlpha
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.applyAlpha(animated: true) }
            .store(in: &cancellables)

        state.$ui
            .map(\.status)
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateDisplayWake() }
            .store(in: &cancellables)

        // MACOS-19: the first picture from a camera — or a different picture from a different
        // camera — reshapes the window to fit it.
        state.$videoAspect
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.applyAspectPolicy() }
            .store(in: &cancellables)
    }

    // MARK: - NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        // MACOS-7 / BG-5: the window is closing. The monitor is not.
        DispatchQueue.main.async { [weak self] in
            self?.updateDisplayWake()
            self?.onVisibilityChanged()
        }
    }

    func windowDidMove(_ notification: Notification) { rememberFrame() }

    func windowDidResize(_ notification: Notification) { rememberFrame() }

    private func rememberFrame() {
        guard !morphing, let shape = currentShape, window.isVisible else { return }
        guard !window.styleMask.contains(.fullScreen) else { return }
        Prefs.setFrame(window.frame, for: shape)
    }

    func windowDidExitFullScreen(_ notification: Notification) {
        guard let pending = pendingShape else { return }
        pendingShape = nil
        apply(shape: pending, animated: true)
    }
}
