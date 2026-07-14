import AppKit
import SwiftUI

/// The look, in one place: glass surfaces, the controls that sit on them, and the pointer tracking
/// that decides when they are on screen (LIVE-11m, MACOS-15/16).
///
/// UI-1 says dark, always — this app is read at 3am in a dark room, and a white flash costs a
/// parent their night vision. Everything below assumes that and leans into it: the picture is the
/// interface, and the chrome floats above it in glass, never boxing it in.

// MARK: - Surfaces

/// The app's one surface material. Liquid Glass where the OS has it, a vibrancy material before
/// that — and a **solid** panel when the user has asked for Reduce Transparency (MACOS-18), because
/// an accessibility setting that an app quietly ignores is not a setting.
struct GlassSurface: ViewModifier {
    var cornerRadius: CGFloat = 16
    var elevated = true

    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        return content
            .background {
                if reduceTransparency {
                    shape.fill(Color(white: 0.10).opacity(0.96))
                } else if #available(macOS 26.0, *) {
                    shape.fill(.clear).glassEffect(.regular, in: shape)
                } else {
                    shape.fill(.ultraThinMaterial)
                }
            }
            .overlay {
                // The hairline is what keeps glass from dissolving into a bright picture.
                shape.strokeBorder(Color.white.opacity(reduceTransparency ? 0.08 : 0.12), lineWidth: 0.5)
            }
            .shadow(color: .black.opacity(elevated ? 0.35 : 0), radius: 20, y: 8)
    }
}

extension View {
    func glassSurface(cornerRadius: CGFloat = 16, elevated: Bool = true) -> some View {
        modifier(GlassSurface(cornerRadius: cornerRadius, elevated: elevated))
    }

    /// LIVE-11m: chrome that fades with the pointer. It is never *removed* from the layout — a
    /// control that moves when it reappears is a control that gets mis-clicked.
    func chromeVisible(_ visible: Bool, reduceMotion: Bool) -> some View {
        opacity(visible ? 1 : 0)
            .animation(reduceMotion ? nil : .easeInOut(duration: 0.22), value: visible)
            .allowsHitTesting(visible)
    }
}

// MARK: - Controls

/// **Every glyph on the control bar is drawn by this one view.**
///
/// It exists because they drifted: the buttons drew their symbol one way and the two *menus* —
/// night vision and the overflow — drew theirs another, and the moon came out visibly smaller and
/// thinner than the speaker beside it. A row of controls that are not the same size does not read
/// as a row of controls; it reads as a mistake. One glyph, one size, everywhere.
///
/// `.symbolVariant(.fill)` is part of that: SF Symbols' outline and filled forms have different
/// optical weights, and a bar that mixes them looks mixed however carefully each one is sized.
struct ControlGlyph: View {
    let symbol: String

    var body: some View {
        Image(systemName: symbol)
            .symbolVariant(.fill)
            .symbolRenderingMode(.hierarchical)
            .font(.system(size: 15, weight: .semibold))
            .imageScale(.medium)
            .frame(width: 34, height: 34)
    }
}

/// One icon control on the glass bar. `latched` is not decoration: LIVE-2 requires that "muted"
/// can never be misread as "press to mute", so the engaged state is a filled, tinted well — a
/// changed glyph is never the only clue.
struct ControlButton: View {
    let symbol: String
    let label: String
    var latched = false
    var tint: Color = .white
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            ControlGlyph(symbol: symbol)
                .foregroundStyle(latched ? AnyShapeStyle(.white) : AnyShapeStyle(tint))
                .background {
                    Circle()
                        .fill(latched ? AnyShapeStyle(Color.red.opacity(0.9))
                                      : AnyShapeStyle(Color.white.opacity(hovering ? 0.16 : 0)))
                }
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(label)
        .accessibilityLabel(label)
    }
}

/// The same glyph, on a menu instead of a button — night vision and the overflow. A menu is still
/// a control on that bar, and must look like one.
struct ControlMenu<Content: View>: View {
    let symbol: String
    let label: String
    @ViewBuilder var content: Content

    @State private var hovering = false

    var body: some View {
        Menu {
            content
        } label: {
            ControlGlyph(symbol: symbol)
                .background(Circle().fill(Color.white.opacity(hovering ? 0.16 : 0)))
                .contentShape(Circle())
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .buttonStyle(.plain)
        .frame(width: 34, height: 34)
        .onHover { hovering = $0 }
        .help(label)
        .accessibilityLabel(label)
    }
}

/// LIVE-6 / ALRM-12: loudness above the room's own baseline, with the alarm's trigger point marked.
/// Zero means "as loud as this room usually is" — so a calm room reads as an empty bar, and that
/// emptiness is the thing a parent learns to trust.
struct LevelBar: View {
    let level: Float
    let max: Float
    let threshold: Float
    let armed: Bool

    private var fraction: CGFloat { CGFloat(min(level / Swift.max(max, 1), 1)) }
    private var markFraction: CGFloat { CGFloat(min(threshold / Swift.max(max, 1), 1)) }
    private var past: Bool { armed && level >= threshold }

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.14))
                Capsule()
                    .fill(
                        LinearGradient(
                            colors: past ? [.orange, .red] : [.green.opacity(0.7), .green],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )
                    .frame(width: Swift.max(width * fraction, past ? 6 : 0))
                    .animation(.linear(duration: 0.08), value: fraction)
                if armed {
                    // Where it will ring. The one number on this screen that decides whether a
                    // parent is woken.
                    Capsule()
                        .fill(Color.white.opacity(0.9))
                        .frame(width: 2, height: 10)
                        .offset(x: width * markFraction - 1)
                }
            }
        }
        .frame(height: 6)
        .accessibilityLabel("Room level")
        .accessibilityValue("\(Int(level)) decibels above the room's baseline")
    }
}

/// The state of the feed, as a colour and a word. The dot is the fastest thing on the screen to
/// read from across a room; the word is what makes it unambiguous.
struct StatusDot: View {
    let status: String
    let running: Bool
    let alarming: Bool

    var color: Color {
        if alarming { return .red }
        if !running { return .secondary }
        switch status {
        case "live": return .green
        case "session-expired", "unsupported-camera", "monitor-failed": return .orange
        default: return .yellow
        }
    }

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 8, height: 8)
            .overlay(Circle().stroke(color.opacity(0.35), lineWidth: 4).blur(radius: 2))
            .accessibilityHidden(true)
    }
}

/// The button on a coloured banner — and the reason it is hand-drawn rather than `.borderedProminent`.
///
/// A system prominent button takes its fill from the window's *accent* and greys itself out while
/// the window is not key. On a red alarm banner that produced red-on-red: the Acknowledge control —
/// the single most important control in this app, the one that stops the ringing — was all but
/// invisible in a window the parent had not clicked yet. Which is every window, at 3am.
///
/// So: a solid white capsule with the banner's colour as its text. It cannot be lost against its
/// background, and it does not care whether the window is focused.
struct BannerButton: View {
    let title: String
    let tint: Color
    var filled = true
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.callout.weight(.semibold))
                .foregroundStyle(filled ? AnyShapeStyle(tint) : AnyShapeStyle(Color.white))
                .padding(.horizontal, 14)
                .padding(.vertical, 6)
                .background {
                    Capsule().fill(
                        filled
                            ? AnyShapeStyle(Color.white.opacity(hovering ? 0.88 : 1))
                            : AnyShapeStyle(Color.white.opacity(hovering ? 0.28 : 0.18))
                    )
                }
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// A banner on the glass: the app talking to the parent. Colour carries urgency; the words carry
/// the meaning — never the other way round (a parent glancing at a red bar must still be told what
/// it is about).
struct Banner<Actions: View>: View {
    let symbol: String
    let title: String
    var tint: Color = .white
    var prominent = false
    @ViewBuilder var actions: Actions

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(prominent ? AnyShapeStyle(.white) : AnyShapeStyle(tint))
            Text(title)
                .font(.callout.weight(.medium))
                .foregroundStyle(prominent ? AnyShapeStyle(.white) : AnyShapeStyle(.primary))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            actions
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background {
            if prominent {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(tint.gradient)
                    .shadow(color: tint.opacity(0.4), radius: 18, y: 6)
            }
        }
        .glassSurfaceIf(!prominent, cornerRadius: 14)
    }
}

extension View {
    @ViewBuilder
    func glassSurfaceIf(_ condition: Bool, cornerRadius: CGFloat) -> some View {
        if condition { glassSurface(cornerRadius: cornerRadius) } else { self }
    }
}

// MARK: - Pointer tracking (LIVE-11m, MACOS-15/16)

/// Where the pointer is, reported even while the app is *not* the active one — which is the whole
/// point on a Mac: the mini window fades and un-fades while the parent is working in something
/// else, and a tracker that only ran while our app was frontmost would never fire.
///
/// `.activeAlways` is what buys that. `hitTest` returning nil is what keeps this overlay from
/// swallowing the clicks meant for the controls underneath it.
struct PointerTracker: NSViewRepresentable {
    var onMove: () -> Void
    var onExit: () -> Void

    func makeNSView(context: Context) -> TrackingView {
        let view = TrackingView()
        view.onMove = onMove
        view.onExit = onExit
        return view
    }

    func updateNSView(_ view: TrackingView, context: Context) {
        view.onMove = onMove
        view.onExit = onExit
    }

    final class TrackingView: NSView {
        var onMove: () -> Void = {}
        var onExit: () -> Void = {}

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            trackingAreas.forEach(removeTrackingArea)
            addTrackingArea(
                NSTrackingArea(
                    rect: .zero,
                    options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect],
                    owner: self
                )
            )
        }

        override func mouseEntered(with event: NSEvent) { onMove() }
        override func mouseMoved(with event: NSEvent) { onMove() }
        override func mouseExited(with event: NSEvent) { onExit() }

        /// Invisible to clicks: tracking areas fire regardless of hit testing, so this can lie over
        /// the whole window without ever stealing a button press.
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}
