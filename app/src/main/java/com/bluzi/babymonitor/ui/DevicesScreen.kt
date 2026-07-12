package com.bluzi.babymonitor.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.JavaMiHttp
import com.bluzi.babymonitor.xiaomi.AuthExpiredException
import com.bluzi.babymonitor.xiaomi.Device
import com.bluzi.babymonitor.xiaomi.MiCloud
import com.bluzi.babymonitor.xiaomi.isCamera
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// CAM-1/CAM-5: pick a camera from the account's devices.

private sealed class LoadState {
    data object Loading : LoadState()
    data class Failed(val message: String, val authExpired: Boolean) : LoadState()
    data class Ready(val cameras: List<Device>) : LoadState()
}

@Composable
fun CameraList(
    store: AppStore,
    currentDid: String?,
    onSelect: (Device) -> Unit,
    onSessionExpired: (String) -> Unit,
) {
    var state by remember { mutableStateOf<LoadState>(LoadState.Loading) }
    var loadCount by remember { mutableIntStateOf(0) }

    LaunchedEffect(loadCount) {
        state = LoadState.Loading
        state = try {
            val session = store.loadSession() ?: throw IllegalStateException("not signed in")
            val devices = withContext(Dispatchers.IO) {
                val cloud = MiCloud(JavaMiHttp(), session = session)
                cloud.onSessionRefreshed = { store.saveSession(it) } // AUTH-7
                cloud.deviceList()
            }
            LoadState.Ready(devices.filter { isCamera(it.model) }) // CAM-1
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.w("ui", "device list load failed: ${e.message}", e)
            // AUTH-8: the type says it, not the wording — a transient network error must never
            // masquerade as an expired session and send the user off to sign in again.
            val authExpired = e is AuthExpiredException || e is IllegalStateException
            // CAM-5/APP-3: readable words on screen; the raw error lives in the log above.
            val message = if (authExpired) {
                "Your session expired — please sign in again."
            } else {
                "Couldn't load your cameras — check the connection and try again."
            }
            LoadState.Failed(message, authExpired)
        }
    }

    when (val s = state) {
        is LoadState.Loading -> Column(
            Modifier.fillMaxWidth().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) { CircularProgressIndicator() }

        is LoadState.Failed -> Column(
            Modifier.fillMaxWidth().padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(s.message, color = MaterialTheme.colorScheme.error) // CAM-5
            if (s.authExpired) {
                Button(onClick = { onSessionExpired("Your session expired — please sign in again.") }) {
                    Text("Sign in again") // AUTH-8
                }
            }
            Button(onClick = { loadCount++ }) { Text("Retry") }
        }

        is LoadState.Ready -> if (s.cameras.isEmpty()) {
            Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text("No cameras found on this account.") // CAM-5
                TextButton(onClick = { loadCount++ }) { Text("Reload") }
            }
        } else {
            LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(s.cameras, key = { it.did }) { cam ->
                    Card(Modifier.fillMaxWidth().clickable { onSelect(cam) }) {
                        Row(
                            Modifier.fillMaxWidth().padding(16.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column {
                                Text(cam.name.ifEmpty { cam.did }, style = MaterialTheme.typography.titleMedium)
                                Text(
                                    cam.model,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            if (cam.did == currentDid) {
                                Icon(
                                    Icons.Filled.Check,
                                    contentDescription = "Current camera",
                                    tint = MaterialTheme.colorScheme.primary,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
fun DevicesScreen(
    store: AppStore,
    onSelect: (Device) -> Unit,
    onSignOut: () -> Unit,
    onSessionExpired: (String) -> Unit,
) {
    var confirmSignOut by remember { mutableStateOf(false) }
    Column(
        Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text("Choose your camera", style = MaterialTheme.typography.headlineSmall)
        Column(Modifier.weight(1f)) {
            CameraList(store, currentDid = null, onSelect = onSelect, onSessionExpired = onSessionExpired)
        }
        TextButton(onClick = { confirmSignOut = true }) { Text("Sign out") }
    }
    if (confirmSignOut) {
        ConfirmSignOutDialog(
            onConfirm = {
                confirmSignOut = false
                onSignOut() // AUTH-10
            },
            onDismiss = { confirmSignOut = false },
        )
    }
}
