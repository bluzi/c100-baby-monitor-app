import AppKit
import BabyMonitorCore
import Combine
import SwiftUI

/// DESK-9. **The monitor is one window.**
///
/// It wears two shapes — *full*, an ordinary Mac window you can work in and take full screen, and
/// *mini*, a small tile that floats over everything else — and it changes between them the way a
/// window changes size: in place, without going away and coming back.
///
/// This is not a cosmetic choice. Two windows, each with its own video surface, is what the app
/// had, and it meant two renderers fighting over one feed: whichever was created last got the
/// frames and the other went black. One window has one video layer, which is never torn down, so
/// switching shape cannot black out the picture, cannot drop audio, and cannot make the camera
/// reconnect. The parent sees the tile grow into a window. Nothing restarts (DESK-9).
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
    /// stops nothing (DESK-13, BG-5).
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

    /// What the window is dressed as. The *shape* (full or mini) is the user's choice; the **chrome**
    /// is what that shape has to be given what is inside it, and there is a third case the shape
    /// enum has no name for:
    ///
    ///  - `.video` — the feed, full: a real window with traffic lights, black behind the picture.
    ///  - `.mini`  — the feed, small: a floating borderless tile.
    ///  - `.dialog` — **no feed at all**: sign-in, or the camera picker. There is nothing to fill a
    ///    window with, and the app is asking the parent for something. So the window stops being a
    ///    window and becomes what a Mac uses to ask: a borderless panel, sized to its own content,
    ///    floating on its own shadow. It is what the system's own password prompt is, because this
    ///    is the one screen in the app that asks for a password.
    private enum Chrome { case video, mini, dialog }

    private var chrome: Chrome {
        if state.shape == .mini { return .mini }
        return state.ui.screen == "viewer" ? .video : .dialog
    }

    private let hosting: NSHostingView<AnyView>
    private var currentChrome: Chrome?

    /// The shape currently on screen. Nil until the first `refresh` — which is what makes the initial
    /// layout take the same path as every later one.
    private var currentShape: WindowShape?

    /// A shape change asked for while the window is full screen: macOS will not restyle a window
    /// mid-full-screen, so it is applied when the transition finishes.
    private var pendingShape: WindowShape?

    /// True while the window is being morphed, so the frames AppKit reports on the way through are
    /// not mistaken for a size the user chose (DESK-9: each shape remembers its own).
    private var morphing = false

    /// Told whenever the window appears or disappears, so the app can decide whether it is still an
    /// app with windows (DESK-14) or has receded into the menu bar.
    var onVisibilityChanged: () -> Void = {}

    init(state: AppState) {
        self.state = state
        window = MonitorWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1040, height: 650),
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        hosting = NSHostingView(rootView: AnyView(RootView().environmentObject(state)))
        super.init()

        window.title = "Baby Monitor"
        window.isReleasedWhenClosed = false // DESK-13: closing it is not destroying it
        window.delegate = self
        window.appearance = NSAppearance(named: .darkAqua) // UI-1
        window.acceptsMouseMovedEvents = true // LIVE-17: the chrome follows the pointer
        window.tabbingMode = .disallowed // one monitor; tabs would be a way to hide it behind itself

        // Who sizes whom, and it changes with the chrome (see `Chrome`):
        //
        //  - the feed: **the window sizes the content**. Left to itself NSHostingView hands SwiftUI's
        //    ideal size to the window as a constraint, and the window *grew* when an alarm banner
        //    appeared — a monitor window that resizes itself the moment something goes wrong is one
        //    that moves the Acknowledge button out from under a half-asleep parent's pointer.
        //  - the dialog: **the content sizes the window**, because a dialog is exactly as big as what
        //    it has to say, and what it has to say changes (a captcha appears, an error appears).
        //
        // So this is set per chrome, in `configure`, and not once here.
        window.contentView = hosting

        observe()
        refresh(animated: false)
    }

    var isVisible: Bool { window.isVisible }

    /// Only the visual harness needs this (see `Preview`); nothing in the app reaches into the
    /// window from outside the controller.
    var nsWindow: NSWindow { window }

    // MARK: - Showing and hiding

    /// DESK-14: a window you cannot Cmd-Tab to is a window you have to go hunting for, and at 3am
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

    // MARK: - The two shapes (DESK-8, DESK-7, DESK-9)

    private func refresh(animated: Bool) {
        let shape = state.shape
        let chrome = self.chrome
        guard currentShape != shape || currentChrome != chrome else { return }

        // Full screen is a shape of its own as far as AppKit is concerned: leave it first, and pick
        // the change back up in windowDidExitFullScreen.
        if window.styleMask.contains(.fullScreen) {
            pendingShape = shape
            window.toggleFullScreen(nil)
            return
        }

        // A dialog's size and place are its own business, not a shape the user chose — so it is
        // never remembered as one.
        if let old = currentShape, currentChrome != .dialog {
            Prefs.setFrame(window.frame, for: old) // each shape remembers its own (DESK-9)
        }
        let wasDialog = currentChrome == .dialog
        currentShape = shape
        currentChrome = chrome

        switch chrome {
        case .video: configureVideo()
        case .mini: configureMini()
        case .dialog: return configureDialog(animated: animated && !wasDialog)
        }

        let target = frame(for: shape)
        morphing = true
        let settle = { [weak self] in
            guard let self else { return }
            self.morphing = false
            self.window.invalidateShadow() // the shape changed under the shadow
            self.applyAspectPolicy() // DESK-12: and it settles into the camera's shape
            // The window has just moved out from under the pointer — ask where the pointer actually
            // is rather than trusting an "entered" that will never be matched by an "exited".
            self.state.pointerMayHaveLeft(windowFrame: self.window.frame, visible: self.window.isVisible)
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

    private func configureVideo() {
        hosting.sizingOptions = [] // the window sizes the picture, never the other way round
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
        window.titlebarAppearsTransparent = true // the video runs under the traffic lights (LIVE-16)
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = false
        window.level = .normal
        window.collectionBehavior = [.fullScreenPrimary] // DESK-7: it can go full screen
        window.backgroundColor = .black
        window.isOpaque = true
        window.hasShadow = true
        window.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        window.alphaValue = 1
        applyAspectPolicy()
    }

    /// **The sign-in panel is not a window with a form in it. It is a dialog.** (AUTH-1, DESK-16.)
    ///
    /// Before a camera is chosen there is no picture, so a window full of black around a little card
    /// is a window pretending to have something in it. The system does not do that: when macOS wants
    /// a password it puts up a borderless panel, sized to what it has to say, floating on its own
    /// shadow. This is the one screen in the app that asks a parent for a password, so it is the one
    /// screen that should look exactly like every other thing on their Mac that has ever asked.
    ///
    /// The content sizes the window here — a captcha appears, an error appears, the panel grows, and
    /// AppKit follows it. That is the opposite of the rule for the feed, and deliberately so.
    private func configureDialog(animated: Bool) {
        hosting.sizingOptions = [.intrinsicContentSize, .minSize, .maxSize]
        window.styleMask = [.borderless, .fullSizeContentView]
        window.isMovableByWindowBackground = true // no title bar to drag it by, so drag it by itself
        window.level = .normal
        window.collectionBehavior = [.fullScreenAuxiliary]
        window.backgroundColor = .clear
        window.isOpaque = false
        window.hasShadow = true // AppKit draws it around the panel's own rounded shape
        window.contentResizeIncrements = NSSize(width: 1, height: 1) // no aspect lock on a form
        window.minSize = .zero
        window.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        window.alphaValue = 1

        // The panel has just been given its own size by SwiftUI; centre whatever that turned out to
        // be, on the next turn of the run loop when the size is real.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.currentChrome == .dialog else { return }
            self.window.setContentSize(self.hosting.fittingSize)
            self.window.center()
            self.window.invalidateShadow()
            self.morphing = false
        }
        morphing = animated // a dialog never animates into place; it simply is where it is
    }

    // MARK: - The camera's shape (DESK-12)

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
    /// sign-in form and the camera picker live in this same window (DESK-9) and are not pictures —
    /// locking a form to 16:9 would be absurd — so there the window is free again.
    ///
    /// Split in two on purpose. The **constraints** must be in place *before* a morph moves the
    /// window, or AppKit clamps the tile back to the full window's minimum size. The **fit** must
    /// happen *after* it, or the window snaps to its new height and then animates from there, which
    /// looks exactly as broken as it sounds.
    private func applyAspectConstraints() {
        guard let shape = currentShape,
              currentChrome != .dialog, // a sign-in form has no aspect ratio, and no business having one
              !window.styleMask.contains(.fullScreen)
        else {
            return
        }

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
              currentChrome != .dialog,
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
        window.level = .floating // DESK-8: above ordinary windows
        // Over full-screen apps and across spaces (BG-17): the glance must never require leaving
        // what you are doing — and OUT of Mission Control and the window cycles (DESK-15).
        //
        // `.transient` is the one that does it: it tells the window server this window is not part
        // of the user's document space. It is already floating on top of everything; listing it
        // among the windows someone opened Mission Control to see *past* is clutter twice over.
        // (`.transient`, `.managed` and `.stationary` are alternatives — setting two is undefined.)
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        window.backgroundColor = .clear // its corners are rounded, so its background cannot be opaque
        window.isOpaque = false
        window.hasShadow = true
        window.hidesOnDeactivate = false
        applyAspectPolicy() // DESK-12: the tile is the camera's shape, not a guess at it
    }

    // MARK: - Fading (DESK-11)

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

    // MARK: - Frames (DESK-9: each shape remembers its own)

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
            // DESK-12: born the camera's shape, so the very first frame lands without a jump.
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
            // Both inputs land here: the shape the user asked for, and the screen that decides
            // whether this is a window at all or a dialog (see `Chrome`).
            self.refresh(animated: true)
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

        // DESK-12: the first picture from a camera — or a different picture from a different
        // camera — reshapes the window to fit it.
        state.$videoAspect
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.applyAspectPolicy() }
            .store(in: &cancellables)
    }

    // MARK: - NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        // DESK-13 / BG-5: the window is closing. The monitor is not.
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.updateDisplayWake()
            self.state.pointerMayHaveLeft(windowFrame: self.window.frame, visible: false)
            self.onVisibilityChanged()
        }
    }

    func windowDidMove(_ notification: Notification) {
        rememberFrame()
        state.pointerMayHaveLeft(windowFrame: window.frame, visible: window.isVisible)
    }

    func windowDidResize(_ notification: Notification) {
        rememberFrame()
        state.pointerMayHaveLeft(windowFrame: window.frame, visible: window.isVisible)
    }

    private func rememberFrame() {
        // A dialog centres itself and is gone again; its frame is not a size the user chose for the
        // monitor, and remembering it as one would give them a 460-point video window later.
        guard currentChrome != .dialog else { return }
        guard !morphing, let shape = currentShape, window.isVisible else { return }
        guard !window.styleMask.contains(.fullScreen) else { return }
        Prefs.setFrame(window.frame, for: shape)
    }

    func windowDidExitFullScreen(_ notification: Notification) {
        guard let pending = pendingShape else { return }
        pendingShape = nil
        refresh(animated: true)
    }
}
