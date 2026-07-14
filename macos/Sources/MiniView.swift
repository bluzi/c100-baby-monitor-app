import AppKit
import BabyMonitorCore
import SwiftUI

/// MACOS-5/15/16 and BG-7m: the mini shape — the same window, worn small. It floats over other
/// applications, over full-screen apps and across spaces, so a parent working at their Mac can see
/// the baby without going to find anything.
///
/// Three rules make it trustworthy rather than merely cute:
///
///  1. **It says what it is.** The feed state is on it at all times, pointer or no pointer. A tile
///     showing a still picture of a calm baby while the feed died an hour ago is the exact failure
///     this project exists to prevent.
///  2. **It says what clicking will do.** With the pointer on it, its controls appear — mute,
///     close, and an explicit control to make it full again. It is never a mystery tile.
///  3. **It gets out of the way, but never over a warning.** It fades while the pointer is
///     elsewhere (MACOS-16) so the work underneath stays readable — and it *cannot* fade while
///     anything needs attention. That decision is core's (`MacShell.needsAttention`), and is
///     tested, because it is the one that could hide an alarm.
struct MiniChrome: View {
    static let cornerRadius: CGFloat = 14

    @EnvironmentObject private var state: AppState

    private var hovering: Bool { state.pointerInside }
    private var alarming: Bool { state.ui.activeAlarm != nil }

    var body: some View {
        ZStack {
            // A scrim, only while the pointer is here: the controls need to be legible over
            // whatever the camera happens to be showing, and the picture needs to be legible when
            // they are gone.
            LinearGradient(
                colors: [.black.opacity(hovering ? 0.45 : 0.0), .clear, .black.opacity(hovering ? 0.35 : 0.28)],
                startPoint: .top,
                endPoint: .bottom
            )
            .allowsHitTesting(false)

            VStack(spacing: 0) {
                topControls
                Spacer(minLength: 0)
                statusStrip
            }
            .padding(8)
        }
        .animation(state.reduceMotion ? nil : .easeInOut(duration: 0.18), value: hovering)
        .overlay {
            RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
                .strokeBorder(
                    alarming ? Color.red.opacity(0.9) : Color.white.opacity(0.18),
                    lineWidth: alarming ? 2 : 0.5
                )
        }
        .clipShape(RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous))
    }

    /// Close on the left, make-it-full on the right — the arrangement of every floating video tile
    /// on this platform, so nobody has to learn ours.
    private var topControls: some View {
        HStack {
            MiniButton(symbol: "xmark", label: "Close (monitoring carries on)") {
                NSApp.sendAction(#selector(AppDelegate.hideMonitorWindow(_:)), to: nil, from: nil)
            }
            Spacer()
            MiniButton(symbol: "arrow.up.left.and.arrow.down.right", label: "Make it full (⌘⇧M)") {
                state.setShape(.full)
            }
        }
        .opacity(hovering ? 1 : 0)
        .allowsHitTesting(hovering)
    }

    /// The state of the feed and the mute control — **always**, in a pill that hugs its own words
    /// rather than a bar across the picture. Everything else here comes and goes with the pointer;
    /// these two do not.
    ///
    /// LIVE-2 wants "muted" said in words *and* in the control. A tile this size cannot spare the
    /// words, so the control does the whole job: it is on screen at all times, and while muted it is
    /// a filled red well, not merely a different glyph. It is also then one click from sound —
    /// which, on the window a parent actually leaves open, is the point.
    private var statusStrip: some View {
        HStack(spacing: 6) {
            HStack(spacing: 6) {
                StatusDot(status: state.ui.status, running: state.ui.running, alarming: alarming)
                Text(state.ui.statusText)
                    .font(.caption.weight(.medium))
                    .lineLimit(1)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .glassSurface(cornerRadius: 11, elevated: false)

            Spacer(minLength: 4)

            if alarming {
                // Always reachable, pointer or not (LIVE-9/MACOS-5): an alarm you must first find
                // the controls for is an alarm that rings longer than it should. Solid red on the
                // picture, because the picture behind it could be anything.
                Button { state.acknowledge() } label: {
                    Text("Acknowledge")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .background(Capsule().fill(Color.red))
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }

            // Always on the tile — see the note above. Not a hover control (MACOS-5, LIVE-2).
            MiniButton(
                symbol: state.ui.muted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                label: state.ui.muted ? "Muted — the alarm still works. Click for sound" : "Mute the speaker",
                latched: state.ui.muted
            ) { state.toggleMute() }
        }
    }
}

/// Smaller than the full window's controls, because everything here is. Same rules: the engaged
/// state of mute is a filled well, never a swapped glyph (LIVE-2).
struct MiniButton: View {
    let symbol: String
    let label: String
    var latched = false
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(.white)
                .frame(width: 22, height: 22)
                .background {
                    Circle().fill(
                        latched ? AnyShapeStyle(Color.red.opacity(0.9))
                                : AnyShapeStyle(Color.black.opacity(hovering ? 0.65 : 0.45))
                    )
                }
                .overlay(Circle().strokeBorder(.white.opacity(0.2), lineWidth: 0.5))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(label)
        .accessibilityLabel(label)
    }
}
