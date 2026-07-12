package com.bluzi.babymonitor.xiaomi

open class XiaomiException(message: String, cause: Throwable? = null) : Exception(message, cause)

/**
 * AUTH-8 / BG-8: the stored session is gone and cannot be refreshed — only the user can fix this.
 * Distinct from every other failure precisely because retrying it forever is pointless: the app
 * must say so instead of looping "connection lost" all night.
 */
class AuthExpiredException(message: String, cause: Throwable? = null) : XiaomiException(message, cause)

/**
 * LIVE-12: the selected camera speaks a protocol (or sends audio in a format) we do not support —
 * retrying can never fix it, so the engine must say so instead of looping "connection lost".
 */
class UnsupportedCameraException(message: String) : XiaomiException(message)

val REGIONS = listOf("cn", "de", "us", "ru", "sg", "i2")

data class Session(
    val userId: String,
    val cUserId: String,
    val passToken: String,
    val serviceToken: String,
    val ssecurity: ByteArray,
    val region: String,
) {
    override fun equals(other: Any?): Boolean =
        other is Session &&
            userId == other.userId &&
            cUserId == other.cUserId &&
            passToken == other.passToken &&
            serviceToken == other.serviceToken &&
            ssecurity.contentEquals(other.ssecurity) &&
            region == other.region

    override fun hashCode(): Int = userId.hashCode() * 31 + serviceToken.hashCode()
}

data class Device(
    val did: String,
    val name: String,
    val model: String,
    val mac: String,
    val ip: String,
)

/** PROTO-11: a camera is a device whose model contains ".camera.". */
fun isCamera(model: String): Boolean = model.contains(".camera.")

/** PROTO-24: the camera's night-vision mode (Camera Control siid 2 / piid 3 on the C100). */
enum class NightVisionMode(val value: Int) {
    ON(0),
    OFF(1),
    AUTO(2),
    ;

    companion object {
        fun fromValue(v: Int): NightVisionMode? = entries.firstOrNull { it.value == v }
    }
}

data class MissVendor(
    val vendor: Int,
    val uid: String?,
    val devicePublicHex: String,
    val sign: String,
)

fun vendorName(id: Int): String = when (id) {
    1 -> "tutk"
    3 -> "agora"
    4 -> "cs2"
    6 -> "mtp"
    else -> id.toString()
}

sealed class LoginResult {
    data class Ok(val session: Session) : LoginResult()

    data class Captcha(
        val image: ByteArray,
        val contentType: String,
        val submit: suspend (code: String) -> LoginResult,
    ) : LoginResult()

    data class TwoFactor(
        val channel: String, // "phone" | "email"
        val maskedTarget: String,
        val submit: suspend (ticket: String) -> LoginResult,
    ) : LoginResult()
}

sealed class Frame {
    abstract val pts: Long
    abstract val sequence: Long
    abstract val flags: Long
    abstract val data: ByteArray

    data class Video(
        val codec: String, // "h264" | "h265"
        override val pts: Long,
        override val sequence: Long,
        override val flags: Long,
        override val data: ByteArray,
    ) : Frame()

    data class Audio(
        val codec: String, // "opus" | "pcma" | "pcmu" | "pcm"
        val sampleRate: Int,
        override val pts: Long,
        override val sequence: Long,
        override val flags: Long,
        override val data: ByteArray,
    ) : Frame()
}
