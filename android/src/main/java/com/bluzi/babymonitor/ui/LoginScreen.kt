package com.bluzi.babymonitor.ui

import android.graphics.BitmapFactory
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChildCare
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.PopupProperties
import com.bluzi.babymonitor.log.Log
import com.bluzi.babymonitor.net.JavaMiHttp
import com.bluzi.babymonitor.xiaomi.LoginResult
import com.bluzi.babymonitor.xiaomi.MiCloud
import com.bluzi.babymonitor.xiaomi.REGIONS
import com.bluzi.babymonitor.xiaomi.Session
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// AUTH-1..AUTH-4, AUTH-9, AUTH-11: Mi account sign-in with captcha and 2FA continuations.

private val REGION_NAMES = mapOf(
    "cn" to "China",
    "de" to "Europe (Germany)",
    "us" to "United States",
    "ru" to "Russia",
    "sg" to "Singapore",
    "i2" to "India",
)

private fun regionLabel(code: String): String =
    REGION_NAMES[code]?.let { "$it (${code.uppercase()})" } ?: code.uppercase()

@Composable
fun LoginScreen(notice: String?, onLoggedIn: (Session) -> Unit) {
    val scope = rememberCoroutineScope()
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var showPassword by remember { mutableStateOf(false) }
    var region by remember { mutableStateOf("sg") }
    var regionMenu by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var challenge by remember { mutableStateOf<LoginResult?>(null) }
    var challengeCode by remember { mutableStateOf("") }
    val usernameFocus = remember { FocusRequester() }
    val passwordFocus = remember { FocusRequester() }
    val codeFocus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current

    // AUTH-11: focus a field the moment its step appears. The step change also tears down the
    // previously focused field, and that teardown's keyboard-hide lands *after* a requestFocus
    // issued in the same frame — so wait the churn out, then focus and show the keyboard
    // explicitly.
    suspend fun focusWithKeyboard(target: FocusRequester) {
        delay(150)
        target.requestFocus()
        keyboard?.show()
    }

    fun handleResult(result: LoginResult) {
        when (result) {
            is LoginResult.Ok -> onLoggedIn(result.session)
            is LoginResult.Captcha, is LoginResult.TwoFactor -> {
                challenge = result
                challengeCode = ""
            }
        }
    }

    fun submit(block: suspend () -> LoginResult) {
        busy = true
        error = null
        scope.launch {
            try {
                val result = withContext(Dispatchers.IO) { block() }
                Log.i("ui", "login screen result: ${result::class.simpleName}")
                handleResult(result)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                Log.w("ui", "login screen error: ${e.message}", e)
                error = e.message ?: "Sign-in failed" // AUTH-9
            } finally {
                busy = false
            }
        }
    }

    fun submitCredentials() {
        if (busy || username.isBlank() || password.isBlank()) return
        submit { MiCloud(JavaMiHttp(), region = region).login(username.trim(), password) }
    }

    // During a captcha/2FA step, back returns to the credentials, matching "Start over".
    BackHandler(enabled = challenge != null && !busy) { challenge = null }

    Column(
        Modifier
            .fillMaxSize()
            .safeDrawingPadding()
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        // Fields and primary buttons share one width, capped so landscape doesn't stretch them.
        val fieldWidth = Modifier.widthIn(max = 400.dp).fillMaxWidth()
        Box(
            Modifier.size(72.dp).background(MaterialTheme.colorScheme.surfaceVariant, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                Icons.Filled.ChildCare,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(36.dp),
            )
        }
        Text("Baby Monitor", style = MaterialTheme.typography.headlineMedium)
        Text(
            "Sign in with the Xiaomi account that owns the camera",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        notice?.let { Text(it, color = MaterialTheme.colorScheme.error) }

        when (val c = challenge) {
            null -> {
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    label = { Text("Email / username") },
                    singleLine = true,
                    enabled = !busy,
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Email,
                        autoCorrectEnabled = false,
                        imeAction = ImeAction.Next,
                    ),
                    keyboardActions = KeyboardActions(onNext = { passwordFocus.requestFocus() }),
                    modifier = fieldWidth.focusRequester(usernameFocus),
                )
                // AUTH-11: typing is the only thing to do here — start in the username field.
                LaunchedEffect(Unit) { focusWithKeyboard(usernameFocus) }
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    label = { Text("Password") },
                    singleLine = true,
                    enabled = !busy,
                    visualTransformation = if (showPassword) {
                        VisualTransformation.None
                    } else {
                        PasswordVisualTransformation()
                    },
                    trailingIcon = {
                        IconButton(onClick = { showPassword = !showPassword }) {
                            Icon(
                                if (showPassword) Icons.Filled.VisibilityOff else Icons.Filled.Visibility,
                                contentDescription = if (showPassword) "Hide password" else "Show password",
                            )
                        }
                    },
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Password,
                        imeAction = ImeAction.Go,
                    ),
                    keyboardActions = KeyboardActions(onGo = { submitCredentials() }),
                    modifier = fieldWidth.focusRequester(passwordFocus),
                )
                // The menu lives in a Box with its button: as a bare Column child it would both
                // anchor to the whole column and add a phantom row to spacedBy while open.
                Box {
                    OutlinedButton(onClick = { regionMenu = true }, enabled = !busy) {
                        Text("Server region: ${regionLabel(region)}")
                    }
                    DropdownMenu(
                        expanded = regionMenu,
                        onDismissRequest = { regionMenu = false },
                        // Non-focusable: picking a region mid-typing must not steal focus from
                        // the field and collapse the keyboard. Taps still work without focus.
                        properties = PopupProperties(focusable = false),
                    ) {
                        for (r in REGIONS) {
                            DropdownMenuItem(
                                text = { Text(regionLabel(r)) },
                                onClick = {
                                    region = r
                                    regionMenu = false
                                },
                            )
                        }
                    }
                }
                Text(
                    "Pick the region your Mi Home account uses",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Button(
                    onClick = ::submitCredentials,
                    enabled = !busy && username.isNotBlank() && password.isNotBlank(),
                    modifier = fieldWidth.height(52.dp),
                ) {
                    Text(if (busy) "Signing in…" else "Sign in")
                }
            }

            is LoginResult.Captcha -> { // AUTH-3
                Text("Xiaomi asks for a captcha")
                val bitmap = remember(c) {
                    BitmapFactory.decodeByteArray(c.image, 0, c.image.size)?.asImageBitmap()
                }
                if (bitmap != null) {
                    Image(bitmap, contentDescription = "Captcha", modifier = Modifier.height(80.dp))
                } else {
                    Text("(captcha image failed to render)")
                }
                OutlinedTextField(
                    value = challengeCode,
                    onValueChange = { challengeCode = it },
                    label = { Text("Captcha code") },
                    singleLine = true,
                    enabled = !busy,
                    keyboardOptions = KeyboardOptions(
                        autoCorrectEnabled = false,
                        imeAction = ImeAction.Done,
                    ),
                    keyboardActions = KeyboardActions(onDone = {
                        if (!busy && challengeCode.isNotBlank()) submit { c.submit(challengeCode.trim()) }
                    }),
                    modifier = fieldWidth,
                )
                Button(
                    onClick = { submit { c.submit(challengeCode.trim()) } },
                    enabled = !busy && challengeCode.isNotBlank(),
                    modifier = fieldWidth.height(52.dp),
                ) { Text("Continue") }
                TextButton(onClick = { challenge = null }, enabled = !busy) { Text("Start over") }
            }

            is LoginResult.TwoFactor -> { // AUTH-4
                Text("Verification code sent by ${c.channel} to ${c.maskedTarget}")
                OutlinedTextField(
                    value = challengeCode,
                    onValueChange = { challengeCode = it },
                    label = { Text("Verification code") },
                    singleLine = true,
                    enabled = !busy,
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Number,
                        imeAction = ImeAction.Done,
                    ),
                    keyboardActions = KeyboardActions(onDone = {
                        if (!busy && challengeCode.isNotBlank()) submit { c.submit(challengeCode.trim()) }
                    }),
                    modifier = fieldWidth.focusRequester(codeFocus),
                )
                // AUTH-11: the code is the only thing to type here — put the cursor in it. Keyed
                // on [busy] too: submitting disables the field (dropping focus and keyboard), so
                // a rejected code refocuses for the retry.
                LaunchedEffect(c, busy) { if (!busy) focusWithKeyboard(codeFocus) }
                Button(
                    onClick = { submit { c.submit(challengeCode.trim()) } },
                    enabled = !busy && challengeCode.isNotBlank(),
                    modifier = fieldWidth.height(52.dp),
                ) { Text("Verify") }
                TextButton(onClick = { challenge = null }, enabled = !busy) { Text("Start over") }
            }

            is LoginResult.Ok -> {} // handled synchronously in handleResult
        }

        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        if (busy) {
            Spacer(Modifier.height(4.dp))
            CircularProgressIndicator(Modifier.size(28.dp))
        }
    }
}
