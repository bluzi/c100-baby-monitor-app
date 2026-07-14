package com.bluzi.babymonitor.ui

// APP-1: routing is a pure function of what the app already knows.

enum class Screen { Login, Devices, Viewer }

fun route(hasSession: Boolean, hasDevice: Boolean): Screen = when {
    !hasSession -> Screen.Login
    !hasDevice -> Screen.Devices
    else -> Screen.Viewer
}
