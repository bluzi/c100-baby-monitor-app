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

    var body: some View {
        VStack(spacing: 18) {
            VStack(spacing: 6) {
                Text("Choose a camera")
                    .font(.system(size: 22, weight: .semibold))
                Text("The camera you pick is the one this Mac will watch.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            content
                .frame(width: 380)

            Button("Sign Out") { state.signOut() } // AUTH-10
                .buttonStyle(.link)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(40)
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
            .padding(.vertical, 36)
            .glassSurface(cornerRadius: 18)
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
            .padding(24)
            .glassSurface(cornerRadius: 18)
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
            .padding(24)
            .glassSurface(cornerRadius: 18)
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
            .glassSurface(cornerRadius: 18)
        }
    }

    private func load() {
        busy = true
        error = nil
        BabyMonitor.shared.loadCameras { list, message in
            busy = false
            cameras = list ?? []
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
                Image(systemName: "web.camera.fill")
                    .font(.system(size: 16))
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
