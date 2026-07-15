import AppKit
import BabyMonitorCore
import SwiftUI

/// CAM-1/5: pick the camera to watch. It is asked once, and never again while one is selected
/// (APP-2) — so this screen's job is to be obvious the one time it is seen, and to never be a dead
/// end when the account or the network lets it down (APP-3).
struct CamerasView: View {
    @EnvironmentObject private var state: AppState
    @State private var cameras: [CameraInfo] = []
    @State private var busy = true
    @State private var error: String?

    /// Like sign-in, this is a dialog and not a window (`MonitorWindow.Chrome.dialog`): there is no
    /// camera chosen yet, so there is nothing to fill a window with. It is the same panel, asking the
    /// next question.
    /// The action row spans the whole panel, as it does on the sign-in dialog: Quit belongs on the
    /// panel's own left edge, under the icon, not floating in the middle of the text column.
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 18) {
                AppMark(size: 64)

                VStack(alignment: .leading, spacing: 14) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Choose the camera to watch.")
                            .font(.system(size: 13, weight: .bold))
                        Text("Baby Monitor will watch this one until you switch it. You can change it later from the feed's menu.")
                            .font(.system(size: 12))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }

                    content
                }
                .frame(width: 340)
            }

            HStack(spacing: 10) {
                // Same reason as sign-in: a borderless dialog has no traffic lights, so the way out
                // lives on it. Nothing is being monitored here — no camera has been chosen — so this
                // is only quitting, and it ends no watch (BG-14, DESK-5).
                Button("Quit") { NSApp.terminate(nil) }
                Spacer()
                Button("Sign Out") { state.signOut() } // AUTH-10
                    .buttonStyle(.link)
            }
        }
        .padding(22)
        .fixedSize()
        .panelSurface()
        .onAppear(perform: load)
    }

    @ViewBuilder
    private var content: some View {
        if busy {
            VStack(spacing: 10) {
                ProgressView().controlSize(.small)
                Text("Looking for your cameras…")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 24)
        } else if let error {
            // CAM-5 / APP-3: readable, with a way to retry — never a dead end.
            VStack(spacing: 12) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.title2)
                    .foregroundStyle(.orange)
                Text(error)
                    .font(.callout)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Try Again", action: load)
                    .buttonStyle(.borderedProminent)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 12)
        } else if cameras.isEmpty {
            VStack(spacing: 12) {
                Image(systemName: "video.slash")
                    .font(.title2)
                    .foregroundStyle(.secondary)
                Text("This Mi account has no cameras on it.")
                    .font(.callout)
                Button("Try Again", action: load)
                    .buttonStyle(.bordered)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 12)
        } else {
            VStack(spacing: 0) {
                ForEach(Array(cameras.enumerated()), id: \.element.did) { index, camera in
                    CameraRow(camera: camera) {
                        BabyMonitor.shared.selectCamera(camera: camera)
                        BabyMonitor.shared.start()
                    }
                    if index < cameras.count - 1 {
                        Divider().padding(.leading, 46)
                    }
                }
            }
            .background(Color.white.opacity(0.05), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(Color.white.opacity(0.1), lineWidth: 0.5)
            )
        }
    }

    private func load() {
        busy = true
        error = nil
        guard !Preview.active else {
            // The harness poses the account rather than asking Xiaomi for it (see `Preview`).
            busy = false
            cameras = Preview.cameras
            error = Preview.camerasError
            return
        }
        BabyMonitor.shared.loadCameras { list, message in
            let found = list ?? []
            // CAM-6: one camera, no choice to make — open it straight away, never a list of one.
            if message == nil, CameraSelection.shared.autoSelectsSingle(cameraCount: Int32(found.count)),
               let only = found.first {
                BabyMonitor.shared.selectCamera(camera: only)
                BabyMonitor.shared.start()
                return // stay on the spinner; routing takes us to the viewer
            }
            busy = false
            cameras = found
            error = message
        }
    }
}

private struct CameraRow: View {
    let camera: CameraInfo
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                // `web.camera.fill` reads as a map pin at this size — a symbol nobody can identify is
                // worse than no symbol.
                Image(systemName: "video.fill")
                    .font(.system(size: 15))
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 22)
                VStack(alignment: .leading, spacing: 2) {
                    Text(camera.name.isEmpty ? camera.did : camera.name)
                        .font(.body.weight(.medium))
                    Text(camera.model)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .contentShape(Rectangle())
            .background(Color.white.opacity(hovering ? 0.08 : 0))
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityLabel("Watch \(camera.name.isEmpty ? camera.did : camera.name)")
    }
}
