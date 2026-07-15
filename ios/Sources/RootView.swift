import BabyMonitorCore
import SwiftUI

/// APP-1: routing is a pure function of what the app already knows — and it is core's function
/// (`route`, surfaced through `UiState.screen`), not a second copy of the rule living here.
struct RootView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        Group {
            switch state.ui.screen {
            case "login": LoginView()
            case "devices": CamerasView()
            default: ViewerView()
            }
        }
        .preferredColorScheme(.dark) // UI-1
    }
}
