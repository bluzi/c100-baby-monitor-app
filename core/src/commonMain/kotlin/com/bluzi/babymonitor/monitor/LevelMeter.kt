package com.bluzi.babymonitor.monitor

import kotlin.math.exp
import kotlin.math.floor
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min

// LIVE-6: loudness relative to an adaptive room baseline. Port of the noise-floor tracker in
// c100/src/player.ts — a low quantile of recent loudness is the floor; the reported level is a
// fast-attack / slower-release envelope of (signal - floor).
//
// Two robustness choices, both measured on a real camera in a quiet room:
//
//  - The floor is a LOW quantile of recent windows, not the median, and that is load-bearing:
//    during real crying (bursts with breaths between them) the quiet breaths are what the
//    quantile picks, so the floor stays anchored to the ROOM and an ongoing cry is never
//    absorbed into "usual" — it can still re-alarm after an acknowledgment (ALRM-5).
//  - The level judges the MEDIAN of the last few windows, because a quiet room's per-window
//    loudness jitters across ±5 dB (sensor self-noise plus opus coding noise near silence). One
//    hot window is jitter; a cry holds every window hot. Without this, the fast-attack envelope
//    rides the tops of the jitter and a silent nursery reads a permanently bouncing 1-5 dB
//    "louder than usual" — which both looks broken and quietly eats the alarm's loudness margin.

/** Below this, a reading is the last shreds of ambient flutter, not activity — display as quiet. */
private const val LEVEL_DISPLAY_SQUELCH_DB = 2.0

/**
 * LIVE-6: what the UI shows for a level. Display only — the detector and the room-level log keep
 * the unrounded value, so the calmer display costs no alarm sensitivity and hides no diagnostics.
 */
fun displayLevelDb(levelDb: Double): Double =
    if (levelDb < LEVEL_DISPLAY_SQUELCH_DB) 0.0 else levelDb

class LevelMeter {
    companion object {
        const val LEVEL_MAX = 24.0 // dB above floor shown/used by the alarm scale

        private const val FLOOR_WINDOW_SAMPLES = 120
        private const val FLOOR_QUANTILE = 0.3
        private const val FLOOR_INIT_DB = -72.0
        private const val FLOOR_WARMUP_RISE_DB_PER_SEC = 14.0
        private const val FLOOR_RISE_DB_PER_SEC = 0.45
        private const val FLOOR_FALL_DB_PER_SEC = 18.0
        private const val LEVEL_ATTACK_TAU_MS = 30.0
        private const val LEVEL_RELEASE_TAU_MS = 190.0
        // Sized to the measured gap between a quiet room's median and the low-quantile floor —
        // absorbs the ambient spread so a steady room reads 0, without hiding a real event.
        private const val LEVEL_DEADBAND_DB = 2.0
        private const val LEVEL_MEDIAN_WINDOWS = 5
        private const val SIGNAL_PEAK_BLEND = 0.18
        private const val SILENCE_DB = -80.0
    }

    private var noiseFloorDb = FLOOR_INIT_DB
    private var levelEnvelopeDb = 0.0
    private var lastSampleAt = 0L
    private val recentSignalDb = DoubleArray(LEVEL_MEDIAN_WINDOWS) { SILENCE_DB }
    private var recentSignalCount = 0
    private var recentSignalIndex = 0

    /** The current absolute noise floor (dBFS) — for the room-level log line only. */
    val floorDb: Double get() = noiseFloorDb
    private val floorHistoryDb = DoubleArray(FLOOR_WINDOW_SAMPLES) { FLOOR_INIT_DB }
    private var floorHistoryCount = 0
    private var floorHistoryIndex = 0

    private fun amplitudeToDb(value: Double): Double =
        if (value > 0) 20 * log10(value) else SILENCE_DB

    private fun floorTargetDb(): Double {
        if (floorHistoryCount == 0) return FLOOR_INIT_DB
        val ordered = floorHistoryDb.copyOfRange(0, floorHistoryCount).sortedArray()
        val idx = min(ordered.size - 1, max(0, floor((ordered.size - 1) * FLOOR_QUANTILE).toInt()))
        return ordered[idx]
    }

    /**
     * Push one measurement (rms and peak amplitude in 0..1) and get the level in dB above
     * the adaptive room floor. 0 = ambient; positive = louder than the room.
     */
    fun process(rms: Double, peak: Double, nowMs: Long): Double {
        val dtMs = if (lastSampleAt == 0L) 100.0 else min(1000.0, max(16.0, (nowMs - lastSampleAt).toDouble()))
        lastSampleAt = nowMs

        val blended = rms * (1 - SIGNAL_PEAK_BLEND) + peak * SIGNAL_PEAK_BLEND
        val signalDb = amplitudeToDb(blended)

        // The floor tracks the raw windows (its quantile is already robust); the level judges
        // the median of the last few, so single-window jitter never reads as "the room got louder".
        floorHistoryDb[floorHistoryIndex] = signalDb
        floorHistoryIndex = (floorHistoryIndex + 1) % FLOOR_WINDOW_SAMPLES
        if (floorHistoryCount < FLOOR_WINDOW_SAMPLES) floorHistoryCount++

        recentSignalDb[recentSignalIndex] = signalDb
        recentSignalIndex = (recentSignalIndex + 1) % LEVEL_MEDIAN_WINDOWS
        if (recentSignalCount < LEVEL_MEDIAN_WINDOWS) recentSignalCount++
        val filteredDb = recentSignalDb.copyOfRange(0, recentSignalCount).sortedArray()[recentSignalCount / 2]

        val target = floorTargetDb()
        val riseRate = if (floorHistoryCount < 20) FLOOR_WARMUP_RISE_DB_PER_SEC else FLOOR_RISE_DB_PER_SEC
        val maxRise = riseRate * dtMs / 1000
        val maxFall = FLOOR_FALL_DB_PER_SEC * dtMs / 1000
        noiseFloorDb = if (target > noiseFloorDb) {
            min(target, noiseFloorDb + maxRise)
        } else {
            max(target, noiseFloorDb - maxFall)
        }

        val targetLevelDb = max(0.0, filteredDb - noiseFloorDb - LEVEL_DEADBAND_DB)
        val tau = if (targetLevelDb > levelEnvelopeDb) LEVEL_ATTACK_TAU_MS else LEVEL_RELEASE_TAU_MS
        val alpha = 1 - exp(-dtMs / tau)
        levelEnvelopeDb += (targetLevelDb - levelEnvelopeDb) * alpha
        return max(0.0, levelEnvelopeDb)
    }
}
