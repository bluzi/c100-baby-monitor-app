package com.bluzi.babymonitor.ui

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluzi.babymonitor.data.Stores
import com.bluzi.babymonitor.monitor.MonitorService

// APP-1: pure routing over what the app already knows (see Router.kt + RouterAndBackoffTest).

@Composable
fun App() {
    val context = LocalContext.current
    val store = remember { Stores.app(context) }
    var session by remember { mutableStateOf(store.loadSession()) }
    var device by remember { mutableStateOf(store.loadDevice()) }
    var loginNotice by remember { mutableStateOf<String?>(null) }

    fun signOut() { // AUTH-10
        MonitorService.stop(context)
        store.signOut()
        session = null
        device = null
    }

    fun sessionExpired(message: String) { // AUTH-8
        MonitorService.stop(context)
        store.signOut()
        session = null
        device = null
        loginNotice = message
    }

    BabyMonitorTheme {
        Surface(Modifier.fillMaxSize()) {
            when (route(hasSession = session != null, hasDevice = device != null)) {
                Screen.Login -> LoginScreen(
                    notice = loginNotice,
                    onLoggedIn = { s ->
                        // AUTH-13: authentication succeeded, but a session we cannot store is not a
                        // sign-in. If the Keystore refuses to seal it, say so and stay on the sign-in
                        // screen — never proceed as if signed in and then lose it on the next launch.
                        if (store.saveSession(s)) {
                            session = s
                            loginNotice = null
                        } else {
                            loginNotice =
                                "Couldn't save your session securely on this device. Please try again."
                        }
                    },
                )

                Screen.Devices -> DevicesScreen(
                    store = store,
                    onSelect = { d ->
                        store.saveDevice(d) // CAM-2
                        device = d
                    },
                    onSignOut = ::signOut,
                    onSessionExpired = ::sessionExpired,
                )

                Screen.Viewer -> ViewerScreen(
                    store = store,
                    device = device!!,
                    onDeviceChanged = { d -> device = d }, // CAM-4
                    onSignOut = ::signOut,
                    onSessionExpired = ::sessionExpired, // AUTH-8
                )
            }
        }
    }
}
