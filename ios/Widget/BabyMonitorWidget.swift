import ActivityKit
import SwiftUI
import WidgetKit

/// BG-2i / IOS-3: the monitoring Live Activity — the lock-screen card and the Dynamic Island. Like
/// the Mac's menu bar icon (MACOS-1) it is quiet while all is well and unmistakable when something is
/// wrong: a ringing alarm turns it red, a monitor that is not live turns it amber, so a glance across
/// a dark room tells the truth. It draws state and offers Stop (BG-3i) — nothing else; the monitor
/// itself is the app's.
@main
struct BabyMonitorWidgetBundle: WidgetBundle {
    var body: some Widget {
        MonitorLiveActivity()
    }
}

struct MonitorLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: MonitorActivityAttributes.self) { context in
            LockScreenView(camera: context.attributes.cameraName, state: context.state)
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    HStack(spacing: 8) {
                        StatusGlyph(state: context.state)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(context.attributes.cameraName).font(.headline).lineLimit(1)
                            Text(statusWord(context.state)).font(.caption).foregroundStyle(statusColor(context.state))
                        }
                    }
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Button(intent: StopMonitoringIntent()) {
                        Label("Stop", systemImage: "stop.fill")
                    }
                    .tint(.red)
                    .buttonStyle(.borderedProminent)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    if context.state.muted {
                        Label("Muted — the alarm still works", systemImage: "speaker.slash.fill")
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                }
            } compactLeading: {
                StatusGlyph(state: context.state)
            } compactTrailing: {
                Text(statusWord(context.state)).font(.caption2).foregroundStyle(statusColor(context.state))
            } minimal: {
                StatusGlyph(state: context.state)
            }
            .keylineTint(statusColor(context.state))
        }
    }
}

/// The lock-screen card: camera, state in words and colour, mute, and Stop.
struct LockScreenView: View {
    let camera: String
    let state: MonitorActivityAttributes.ContentState

    var body: some View {
        HStack(spacing: 14) {
            StatusGlyph(state: state)
                .font(.title2)
            VStack(alignment: .leading, spacing: 2) {
                Text(camera).font(.headline).lineLimit(1)
                HStack(spacing: 6) {
                    Text(statusWord(state)).font(.subheadline).foregroundStyle(statusColor(state))
                    if state.muted {
                        Image(systemName: "speaker.slash.fill").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            Spacer()
            Button(intent: StopMonitoringIntent()) {
                Label("Stop", systemImage: "stop.fill").labelStyle(.iconOnly).font(.title3)
            }
            .tint(.red)
            .buttonStyle(.bordered)
        }
        .padding()
    }
}

/// The one glyph that says whether the monitor is fine: a waveform while live, a bell while alarming,
/// a warning triangle when the feed is not live — coloured to match.
struct StatusGlyph: View {
    let state: MonitorActivityAttributes.ContentState

    var body: some View {
        Image(systemName: symbol(state))
            .symbolRenderingMode(.hierarchical)
            .foregroundStyle(statusColor(state))
    }
}

private func symbol(_ state: MonitorActivityAttributes.ContentState) -> String {
    if state.alarming { return "bell.badge.fill" }
    switch state.status {
    case "live": return "waveform"
    case "session-expired", "unsupported-camera", "monitor-failed": return "exclamationmark.triangle.fill"
    default: return "arrow.triangle.2.circlepath"
    }
}

private func statusColor(_ state: MonitorActivityAttributes.ContentState) -> Color {
    if state.alarming { return .red }
    switch state.status {
    case "live": return .green
    case "session-expired", "unsupported-camera", "monitor-failed": return .orange
    default: return .yellow
    }
}

private func statusWord(_ state: MonitorActivityAttributes.ContentState) -> String {
    if state.alarming { return "Alarm" }
    switch state.status {
    case "live": return "Live"
    case "connecting": return "Connecting"
    case "reconnecting": return "Reconnecting"
    case "session-expired": return "Session expired"
    case "monitor-failed": return "Stopped working"
    case "unsupported-camera": return "Unsupported"
    default: return "Not live"
    }
}
