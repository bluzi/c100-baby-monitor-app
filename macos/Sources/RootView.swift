import BabyMonitorCore
import SwiftUI

/// APP-1: routing is a pure function of what the app already knows — and it is core's function,
/// not a second copy of the rule living here.
struct RootView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Group {
            switch state.ui.screen {
            case "login": LoginView()
            case "devices": CamerasView()
            default: MonitorView()
            }
        }
        .background(state.shape == .mini ? Color.clear : Color.black)
        .preferredColorScheme(.dark) // UI-1: dark, for a dark room, always
    }
}

/// The monitor, in whichever shape the window is wearing (MACOS-14).
///
/// **The video surface is one view, in one place in the tree, for both shapes.** That is not a
/// tidiness point — it is the whole reason the shapes can be the same window: SwiftUI keeps the
/// same `NSView` (and therefore the same decoder, the same layer and the same pushed frames) as
/// long as it stays put, so switching shape cannot black the picture out or make the feed
/// reconnect. Move `VideoStage` inside either branch below and that promise is gone.
struct MonitorView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        ZStack {
            VideoStage()

            // LIVE-11m / MACOS-15/16: where the pointer is. Invisible, and never in the way.
            PointerTracker(
                onMove: { state.pointerMoved() },
                onExit: { state.pointerExited() }
            )

            if state.shape == .mini {
                MiniChrome()
            } else {
                ViewerChrome()
            }
        }
        // BG-5: reopening the window while monitoring shows the ongoing feed; reopening after Stop
        // starts monitoring again.
        .onAppear { if !state.ui.running { state.start() } }
    }
}

/// The picture. Core pushes frames into the renderer; this view just owns the layer — and keeps it
/// alive across a change of shape.
struct VideoStage: View {
    @EnvironmentObject private var state: AppState

    private var radius: CGFloat { state.shape == .mini ? MiniChrome.cornerRadius : 0 }

    var body: some View {
        ZStack {
            // The corner radius lives on the layer rather than in a SwiftUI `clipShape`: an
            // AVSampleBufferDisplayLayer is an AppKit citizen, and asking SwiftUI to mask it is
            // asking for a square black corner on a rounded floating tile.
            //
            // The real surface is in the tree even under the visual harness, and the fake picture
            // is drawn *over* it — so a preview run exercises the same view identities as a real
            // one, and can therefore prove that a change of shape does not rebuild them (MACOS-14).
            VideoSurface(cornerRadius: radius) { size in
                state.videoSizeChanged(size) // MACOS-19: the window takes the camera's shape
            }
            if Preview.active {
                Preview.backdrop
                    .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
                    .allowsHitTesting(false)
            }
        }
        .ignoresSafeArea()
    }
}

/// The picture, as an AppKit view.
struct VideoSurface: NSViewRepresentable {
    var cornerRadius: CGFloat = 0
    var onVideoSize: (CGSize) -> Void = { _ in }

    func makeNSView(context: Context) -> VideoLayerView {
        let view = VideoLayerView(frame: .zero)
        let bridge = VideoRendererBridge(view: view)
        context.coordinator.bridge = bridge
        AppleVideo.shared.renderer = bridge
        view.cornerRadius = cornerRadius
        view.onVideoSize = onVideoSize
        // MACOS-14: this must be logged exactly ONCE per window, however many times the window
        // changes shape. A second line here means the video surface was rebuilt — which means the
        // picture blacked out and the decoder lost its parameter sets, and the promise that the
        // shapes are one window is broken.
        Log.info("video", "video surface created")
        return view
    }

    func updateNSView(_ nsView: VideoLayerView, context: Context) {
        nsView.cornerRadius = cornerRadius
        nsView.onVideoSize = onVideoSize
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    static func dismantleNSView(_ nsView: VideoLayerView, coordinator: Coordinator) {
        // LIVE-7: the picture goes away, audio does not. Core simply stops having anywhere to draw.
        if AppleVideo.shared.renderer === coordinator.bridge {
            AppleVideo.shared.renderer = nil
        }
        Log.info("video", "video surface torn down")
    }

    final class Coordinator {
        var bridge: VideoRendererBridge?
    }
}
