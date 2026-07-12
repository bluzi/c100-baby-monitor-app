package com.bluzi.babymonitor.log

/**
 * Pure logging facade — no `android.*` imports, so the protocol layer (xiaomi/, net/, dsp/) can
 * log while staying platform-free (see CLAUDE.md architecture rule). The Android app installs a
 * sink backed by `android.util.Log` at startup; unit tests leave the default no-op sink.
 *
 * One `tag` per subsystem ("login", "cloud", "cs2", "miss", "engine", "ui", "audio", "video").
 * Filter everything on device with: `adb logcat -s BabyMonitor`.
 *
 * Never log secrets — passwords, passToken, serviceToken, ssecurity. Log ids, ips, statuses,
 * and error messages.
 */
object Log {
    enum class Level { DEBUG, INFO, WARN, ERROR }

    fun interface Sink {
        fun log(level: Level, tag: String, message: String, error: Throwable?)
    }

    @Volatile
    private var sink: Sink = Sink { _, _, _, _ -> } // no-op until installed

    fun install(sink: Sink) {
        this.sink = sink
    }

    fun d(tag: String, message: String) = sink.log(Level.DEBUG, tag, message, null)
    fun i(tag: String, message: String) = sink.log(Level.INFO, tag, message, null)
    fun w(tag: String, message: String, error: Throwable? = null) = sink.log(Level.WARN, tag, message, error)
    fun e(tag: String, message: String, error: Throwable? = null) = sink.log(Level.ERROR, tag, message, error)
}
