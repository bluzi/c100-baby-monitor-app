package com.bluzi.babymonitor.monitor

// The engine's platform edge. Everything above this is the monitor; everything below it is a
// speaker, a screen and a buzzer. Each app implements these and gets the whole monitor —
// protocol, reconnect, watchdog, cry detection, alarm schedule — unchanged.
//
// The contracts here are not incidental. Each one encodes something the monitor depends on being
// true, and an implementation that breaks it breaks the monitor silently.

enum class AlarmKind { BABY_NOISE, FEED_DOWN }

/**
 * Opus in, speaker out, with an analysis tap.
 *
 * LIVE-3, and it is load-bearing: **mute silences the speaker only**. Decoding and the analysis
 * callback must keep running while muted, because the level meter and the crying alarm feed off
 * them. An implementation that mutes by pausing the decoder has quietly disabled the alarm — the
 * app would look like it was monitoring, and it would not be.
 *
 * [start] may throw: the engine treats that as a dead connection, reconnects and rebuilds.
 * A write that fails must throw too, rather than play silence — a parent must never mistake a
 * broken audio path for a quiet room.
 */
interface AudioOutput {
    var muted: Boolean

    fun start()

    fun push(packet: ByteArray, ptsMs: Long)

    fun release()
}

/**
 * H.265 Annex-B access units in, picture out.
 *
 * LIVE-7: best-effort by design. [push] must NEVER throw — video trouble must never take audio
 * monitoring down with it. Swallow, log, and recover at the next keyframe.
 */
interface VideoOutput {
    fun push(annexB: ByteArray, ptsMs: Long)

    fun release()
}

/**
 * The last link between a crying baby and a sleeping parent. Implementations must be paranoid:
 * never throw, and cut through whatever the platform does to "quiet" audio.
 *
 * [ring] returns false when another alarm is already sounding. The caller must then retry later
 * rather than treat this alarm as delivered (WATCH-6) — an unheard alarm is not an alarm.
 */
interface Ringer {
    fun ring(kind: AlarmKind, cameraName: String): Boolean

    fun acknowledge()
}

/** The platform's media stack, handed to the engine at construction. */
interface MediaFactory {
    /** [onPcmWindow] receives every decoded window, muted or not — that is what LIVE-3 means. */
    fun audio(onPcmWindow: (pcm: ShortArray, sampleRate: Int) -> Unit): AudioOutput

    fun video(): VideoOutput
}
