package com.bluzi.babymonitor.dsp

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

// Pure-Kotlin DSP behind the crying alarm (ALRM-3/13).
//
// The whole difficulty is telling a baby crying in the room from the things that sound loud but
// are not a baby: parents talking next door, a TV through a wall, traffic, a fan, a door slamming.
// Loudness alone cannot do it — at a high sensitivity, muffled conversation is louder than a
// sleeping baby. Three physical facts separate them:
//
//   1. PITCH. An infant cry has a fundamental around 300–600 Hz. An adult voice is 85–180 Hz
//      (male) or 165–255 Hz (female) — an octave or more below. Pitch alone rejects adult speech,
//      whichever room it comes from.
//   2. TONALITY. A cry is strongly periodic — a clear, sustained pitch. Rumble, fans, white noise
//      and bangs have no stable pitch at all.
//   3. BRIGHTNESS. Walls and doors are low-pass filters: speech from the next room arrives with
//      its high frequencies stripped away. A cry *in the room* keeps strong energy above 1 kHz.
//      This is what makes "loud voice through the door" fundamentally different from "baby here".
//
// Any one of these can be fooled; together they are hard to fool by accident.

/** In-place iterative radix-2 FFT. re/im length must be a power of two. */
fun fft(re: DoubleArray, im: DoubleArray) {
    val n = re.size
    require(n == im.size && n and (n - 1) == 0) { "fft: size must be a power of two" }
    var j = 0
    for (i in 0 until n - 1) {
        if (i < j) {
            var t = re[i]; re[i] = re[j]; re[j] = t
            t = im[i]; im[i] = im[j]; im[j] = t
        }
        var k = n shr 1
        while (k <= j) {
            j -= k
            k = k shr 1
        }
        j += k
    }
    var len = 2
    while (len <= n) {
        val ang = -2 * PI / len
        val wRe = cos(ang)
        val wIm = sin(ang)
        var i = 0
        while (i < n) {
            var curRe = 1.0
            var curIm = 0.0
            for (k in 0 until len / 2) {
                val aRe = re[i + k]
                val aIm = im[i + k]
                val bRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm
                val bIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe
                re[i + k] = aRe + bRe
                im[i + k] = aIm + bIm
                re[i + k + len / 2] = aRe - bRe
                im[i + k + len / 2] = aIm - bIm
                val nRe = curRe * wRe - curIm * wIm
                curIm = curRe * wIm + curIm * wRe
                curRe = nRe
            }
            i += len
        }
        len = len shl 1
    }
}

/** What one analysis window tells us about the sound in the room. */
data class WindowMetrics(
    val rms: Double,
    val peak: Double,
    /** Fundamental pitch in Hz, or 0 when the sound has no clear pitch at all. */
    val pitchHz: Double,
    /** How strongly periodic the sound is, 0..1. A cry is tonal; a fan or a bang is not. */
    val tonality: Double,
    /** Fraction of voice-band energy that lives above 1 kHz. Muffled = dark; in-room = bright. */
    val brightness: Double,
    /** Fraction of all energy below 300 Hz — rumble, and what is left of speech through a wall. */
    val lowRatio: Double,
)

/** Cry pitch range (ALRM-3). Comfortably above an adult voice, comfortably below a whistle. */
const val CRY_PITCH_MIN_HZ = 250.0
const val CRY_PITCH_MAX_HZ = 700.0

// Pitch search covers adult voices too, so an adult is *found* at their own pitch and then
// rejected for being too low — rather than being mistaken for a quiet baby.
private const val PITCH_SEARCH_MIN_HZ = 80.0
private const val PITCH_SEARCH_MAX_HZ = 900.0
private const val PITCH_DECIMATION = 4 // 48 kHz → 12 kHz: plenty for a 900 Hz search, 16× cheaper

/**
 * Fundamental pitch by normalised autocorrelation, with the sub-harmonic guard that matters here:
 * a periodic signal also correlates at 2×, 3×… its period, so a naive peak-pick reports a baby at
 * *half* her real pitch — i.e. right in the adult range — and the cry gets thrown away. We
 * therefore prefer the shortest lag that is nearly as good as the best one.
 */
fun estimatePitch(x: DoubleArray, sampleRate: Int): Pair<Double, Double> {
    val minLag = (sampleRate / PITCH_SEARCH_MAX_HZ).toInt().coerceAtLeast(2)
    val maxLag = (sampleRate / PITCH_SEARCH_MIN_HZ).toInt()
    if (x.size < 2 * maxLag) return 0.0 to 0.0

    var energy = 0.0
    for (v in x) energy += v * v
    if (energy <= 1e-9) return 0.0 to 0.0

    val n = x.size - maxLag
    val r = DoubleArray(maxLag + 1)
    var best = 0.0
    for (lag in minLag..maxLag) {
        var num = 0.0
        var e0 = 0.0
        var e1 = 0.0
        for (i in 0 until n) {
            val a = x[i]
            val b = x[i + lag]
            num += a * b
            e0 += a * a
            e1 += b * b
        }
        val denom = sqrt(e0 * e1)
        val corr = if (denom > 1e-12) num / denom else 0.0
        r[lag] = corr
        if (corr > best) best = corr
    }
    if (best <= 0.0) return 0.0 to 0.0

    // Octave fix: a periodic signal correlates at every multiple of its period, so the highest
    // peak may sit at 2× or 3× the true one — which would report a crying baby at half her pitch,
    // squarely in the adult range, and throw the cry away. Take the EARLIEST strong *peak*, not
    // merely the earliest lag that is strong: around a real peak the correlation is smooth, so a
    // plain threshold-crossing would land short of it and read the pitch too high.
    var chosen = 0
    var chosenCorr = 0.0
    for (lag in (minLag + 1) until maxLag) {
        val isPeak = r[lag] > r[lag - 1] && r[lag] >= r[lag + 1]
        if (isPeak && r[lag] >= 0.85 * best) {
            chosen = lag
            chosenCorr = r[lag]
            break
        }
    }
    if (chosen == 0) { // no clear peak: fall back to the strongest lag
        for (lag in minLag..maxLag) {
            if (r[lag] == best) {
                chosen = lag
                chosenCorr = best
                break
            }
        }
    }
    if (chosen == 0) return 0.0 to 0.0

    // Whole-sample lags quantise pitch coarsely (tens of Hz up at cry pitches), which would blur
    // the very boundary the alarm decides on. Interpolate the correlation peak for the real period.
    var period = chosen.toDouble()
    if (chosen > minLag && chosen < maxLag) {
        val a = r[chosen - 1]
        val b = r[chosen]
        val c = r[chosen + 1]
        val denom = a - 2 * b + c
        if (abs(denom) > 1e-12) {
            val delta = 0.5 * (a - c) / denom
            if (delta > -1.0 && delta < 1.0) period = chosen + delta
        }
    }
    return sampleRate.toDouble() / period to chosenCorr.coerceIn(0.0, 1.0)
}

/** Cheap decimation (box average). Anti-aliasing enough for a pitch search below 900 Hz. */
fun decimate(pcm: ShortArray, factor: Int): DoubleArray {
    if (factor <= 1) return DoubleArray(pcm.size) { pcm[it] / 32768.0 }
    val out = DoubleArray(pcm.size / factor)
    for (i in out.indices) {
        var sum = 0.0
        for (k in 0 until factor) sum += pcm[i * factor + k] / 32768.0
        out[i] = sum / factor
    }
    return out
}

/**
 * Analyze one PCM window: loudness, pitch, tonality and spectral shape. Input is 16-bit PCM.
 */
fun analyzeWindow(pcm: ShortArray, sampleRate: Int): WindowMetrics {
    var n = pcm.size.takeHighestOneBit()
    if (n < 2) return WindowMetrics(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
    if (n > 4096) n = 4096

    var sumSq = 0.0
    var peak = 0.0
    for (s in pcm) {
        val v = s / 32768.0
        sumSq += v * v
        val a = abs(v)
        if (a > peak) peak = a
    }
    val rms = sqrt(sumSq / pcm.size)

    val re = DoubleArray(n)
    val im = DoubleArray(n)
    for (i in 0 until n) {
        // Hann window keeps leakage from smearing rumble into the bands we care about.
        val w = 0.5 * (1 - cos(2 * PI * i / (n - 1)))
        re[i] = (pcm[i] / 32768.0) * w
    }
    fft(re, im)

    val binHz = sampleRate.toDouble() / n
    var total = 0.0
    var low = 0.0 // < 300 Hz: rumble, and the muffled remains of speech through a wall
    var voice = 0.0 // 300–5000 Hz: where a cry lives
    var high = 0.0 // 1000–5000 Hz: the part a wall takes away
    for (bin in 1 until n / 2) {
        val energy = re[bin] * re[bin] + im[bin] * im[bin]
        total += energy
        val hz = bin * binHz
        when {
            hz < 300.0 -> low += energy
            hz <= 5000.0 -> {
                voice += energy
                if (hz >= 1000.0) high += energy
            }
        }
    }

    val (pitchHz, tonality) = estimatePitch(decimate(pcm, PITCH_DECIMATION), sampleRate / PITCH_DECIMATION)
    return WindowMetrics(
        rms = rms,
        peak = peak,
        pitchHz = pitchHz,
        tonality = tonality,
        brightness = if (voice > 0) high / voice else 0.0,
        lowRatio = if (total > 0) low / total else 0.0,
    )
}
