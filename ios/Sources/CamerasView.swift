import BabyMonitorCore
import SwiftUI

/// CAM-1/5: pick the camera to watch. Asked once, and never again while one is selected (APP-2) — so
/// this screen's job is to be obvious the one time it is seen, and never a dead end when the account
/// or the network lets it down (APP-3).
struct CamerasView: View {
    @EnvironmentObject private var state: AppState
    @State private var cameras: [CameraInfo] = []
    @State private var busy = true
    @State private var error: String?

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            VStack(spacing: 22) {
                Spacer(minLength: 24)
                AppMark(size: 64)
                VStack(spacing: 6) {
                    Text("Choose the camera to watch")
                        .font(.title2.bold())
                    Text("Baby Monitor will watch this one until you switch it. You can change it later from the feed.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                content
                Spacer(minLength: 8)
                Button("Sign Out", role: .destructive) { state.signOut() } // AUTH-10
                    .font(.subheadline)
                Spacer(minLength: 16)
            }
            .padding(.horizontal, 24)
            .frame(maxWidth: 520)
            .frame(maxWidth: .infinity)
        }
        .onAppear {
            OrientationLock.set(.portrait)
            load()
        }
    }

    @ViewBuilder
    private var content: some View {
        if busy {
            VStack(spacing: 12) {
                ProgressView()
                Text("Looking for your cameras…").font(.callout).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 40)
        } else if let error {
            // CAM-5 / APP-3: readable, with a way to retry — never a dead end.
            VStack(spacing: 14) {
                Image(systemName: "exclamationmark.triangle.fill").font(.largeTitle).foregroundStyle(.orange)
                Text(error).font(.callout).multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
                Button("Try Again", action: load).buttonStyle(.glassProminent)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 24)
        } else if cameras.isEmpty {
            VStack(spacing: 14) {
                Image(systemName: "video.slash").font(.largeTitle).foregroundStyle(.secondary)
                Text("This Mi account has no cameras on it.").font(.callout)
                Button("Try Again", action: load).buttonStyle(.glass)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 24)
        } else {
            VStack(spacing: 10) {
                ForEach(cameras, id: \.did) { camera in
                    CameraRow(camera: camera) {
                        BabyMonitor.shared.selectCamera(camera: camera)
                        state.start()
                    }
                }
            }
        }
    }

    private func load() {
        busy = true; error = nil
        guard !Preview.active else {
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
                state.start()
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

    var body: some View {
        Button(action: action) {
            HStack(spacing: 14) {
                Image(systemName: "video.fill")
                    .font(.system(size: 18))
                    .foregroundStyle(.tint)
                    .frame(width: 28)
                VStack(alignment: .leading, spacing: 2) {
                    Text(camera.name.isEmpty ? camera.did : camera.name)
                        .font(.body.weight(.medium))
                        .foregroundStyle(.primary)
                    Text(camera.model)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 16)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .glassSurface(cornerRadius: 14)
        .accessibilityLabel("Watch \(camera.name.isEmpty ? camera.did : camera.name)")
    }
}
