import SwiftUI

/// The look, in one place: Liquid Glass surfaces, the controls that sit on them, the level bar and
/// the banners. UI-1 says dark, always — this app is read at 3am in a dark room, and a white flash
/// costs a parent their night vision. The picture is the interface; the chrome floats above it in
/// glass, never boxing it in.
///
/// iOS 26 is the minimum, so `.glassEffect` is used unconditionally — no availability gates. Reduce
/// Transparency still wins where it is on (an accessibility setting an app quietly ignores is not a
/// setting): there, solid surfaces stand in for glass.

// MARK: - Surfaces

struct GlassSurface: ViewModifier {
    var cornerRadius: CGFloat = 16
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        return Group {
            if reduceTransparency {
                content
                    .background(shape.fill(Color(white: 0.10).opacity(0.96)))
                    .overlay(shape.strokeBorder(.white.opacity(0.08), lineWidth: 0.5))
            } else {
                content
                    .glassEffect(.regular, in: shape)
                    .overlay(shape.strokeBorder(.white.opacity(0.12), lineWidth: 0.5))
            }
        }
    }
}

extension View {
    func glassSurface(cornerRadius: CGFloat = 16) -> some View {
        modifier(GlassSurface(cornerRadius: cornerRadius))
    }

    /// LIVE-11: chrome that fades with a tap. It is never *removed* from the layout — a control that
    /// moves when it reappears is a control that gets mis-tapped.
    func chromeVisible(_ visible: Bool) -> some View {
        opacity(visible ? 1 : 0)
            .animation(.easeInOut(duration: 0.22), value: visible)
            .allowsHitTesting(visible)
    }
}

// MARK: - Controls

/// **Every glyph on the control bar is drawn by this one view**, so they cannot drift out of size
/// with each other — a row of controls that are not the same size reads as a mistake, not a row.
/// `.symbolVariant(.fill)` keeps them all filled forms, which have a consistent optical weight.
struct ControlGlyph: View {
    let symbol: String

    /// Optical sizing: a crescent is a small shape in a large box while a speaker fills its box, so
    /// the handful that sit small are drawn a touch larger to make the row read as one row.
    private var opticalSize: CGFloat {
        switch symbol {
        case let s where s.hasPrefix("moon"): return 21
        case "ellipsis": return 21
        default: return 20
        }
    }

    var body: some View {
        Image(systemName: symbol)
            .symbolVariant(.fill)
            .symbolRenderingMode(.hierarchical)
            .font(.system(size: opticalSize, weight: .semibold))
            .frame(width: 46, height: 46)
    }
}

/// One icon control on the glass bar. `latched` is not decoration: LIVE-2 requires that "muted" can
/// never be misread as "press to mute", so the engaged state is a filled, tinted well — a changed
/// glyph is never the only clue.
struct ControlButton: View {
    let symbol: String
    let label: String
    var latched = false
    var tint: Color = .white
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            ControlGlyph(symbol: symbol)
                .foregroundStyle(latched ? AnyShapeStyle(.white) : AnyShapeStyle(tint))
                .background(Circle().fill(latched ? AnyShapeStyle(Color.red.opacity(0.9)) : AnyShapeStyle(.clear)))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }
}

/// A control on the bar that opens a menu — night vision, and the overflow. A SwiftUI `Menu` wrapping
/// the same glyph, so it reads identically to the buttons beside it.
struct ControlMenu<Content: View>: View {
    let symbol: String
    let label: String
    var latched = false
    var tint: Color = .white
    @ViewBuilder var content: Content

    var body: some View {
        Menu {
            content
        } label: {
            ControlGlyph(symbol: symbol)
                .foregroundStyle(tint)
                .background(Circle().fill(latched ? AnyShapeStyle(Color.accentColor.opacity(0.9)) : AnyShapeStyle(.clear)))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }
}

/// LIVE-6 / ALRM-12: loudness above the room's own baseline, with the alarm's trigger point marked.
/// Zero means "as loud as this room usually is" — a calm room reads as an empty bar, and that
/// emptiness is what a parent learns to trust.
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
                Capsule().fill(.white.opacity(0.16))
                Capsule()
                    .fill(LinearGradient(
                        colors: past ? [.orange, .red] : [.green.opacity(0.7), .green],
                        startPoint: .leading, endPoint: .trailing
                    ))
                    .frame(width: Swift.max(width * fraction, past ? 8 : 0))
                    .animation(.linear(duration: 0.08), value: fraction)
                if armed {
                    // Where it will ring. The one mark on this screen that decides whether a parent
                    // is woken.
                    Capsule()
                        .fill(.white.opacity(0.9))
                        .frame(width: 2.5, height: 14)
                        .offset(x: width * markFraction - 1.25)
                }
            }
        }
        .frame(height: 8)
        .accessibilityLabel("Room level")
        .accessibilityValue("\(Int(level)) decibels above the room's baseline")
    }
}

/// The state of the feed, as a colour and a word. The dot is the fastest thing on the screen to read
/// from across a room; the word is what makes it unambiguous.
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
            .frame(width: 10, height: 10)
            .overlay(Circle().stroke(color.opacity(0.35), lineWidth: 5).blur(radius: 2))
            .accessibilityHidden(true)
    }
}

/// The button on a coloured banner — hand-drawn, not `.borderedProminent`, for the same reason the
/// Mac's is: a system prominent button greys itself against an unfocused window and can vanish into a
/// red alarm banner. A solid white capsule with the banner's colour as its text cannot be lost.
struct BannerButton: View {
    let title: String
    let tint: Color
    var filled = true
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.callout.weight(.semibold))
                .foregroundStyle(filled ? AnyShapeStyle(tint) : AnyShapeStyle(Color.white))
                .padding(.horizontal, 16)
                .padding(.vertical, 9)
                .background(Capsule().fill(filled ? AnyShapeStyle(Color.white) : AnyShapeStyle(Color.white.opacity(0.18))))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }
}

/// A banner on the glass: the app talking to the parent. Colour carries urgency; the words carry the
/// meaning — never the other way round.
struct Banner<Actions: View>: View {
    let symbol: String
    let title: String
    var tint: Color = .white
    var prominent = false
    @ViewBuilder var actions: Actions

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(prominent ? AnyShapeStyle(.white) : AnyShapeStyle(tint))
            Text(title)
                .font(.callout.weight(.medium))
                .foregroundStyle(prominent ? AnyShapeStyle(.white) : AnyShapeStyle(.primary))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            actions
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background {
            if prominent {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(tint.gradient)
                    .shadow(color: tint.opacity(0.4), radius: 18, y: 6)
            }
        }
        .glassSurfaceIf(!prominent, cornerRadius: 16)
    }
}

extension View {
    @ViewBuilder
    func glassSurfaceIf(_ condition: Bool, cornerRadius: CGFloat) -> some View {
        if condition { glassSurface(cornerRadius: cornerRadius) } else { self }
    }
}

/// The app's own mark, for the places it shows itself: sign-in, and About.
struct AppMark: View {
    var size: CGFloat = 72

    var body: some View {
        Group {
            if let icon = UIImage(named: "AppIcon") {
                Image(uiImage: icon).resizable()
            } else {
                Image(systemName: "waveform")
                    .resizable()
                    .scaledToFit()
                    .padding(size * 0.22)
                    .foregroundStyle(.white)
                    .background(Color.accentColor.gradient, in: RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
        .accessibilityHidden(true)
    }
}
