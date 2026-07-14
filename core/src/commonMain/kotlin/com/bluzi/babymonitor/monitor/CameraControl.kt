package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.AppStore
import com.bluzi.babymonitor.net.platformHttp
import com.bluzi.babymonitor.platform.ioDispatcher
import com.bluzi.babymonitor.xiaomi.MiCloud
import com.bluzi.babymonitor.xiaomi.NightVisionMode
import com.bluzi.babymonitor.xiaomi.XiaomiException
import kotlinx.coroutines.withContext

/**
 * LIVE-10 / PROTO-24: camera-side settings (night vision) over the MiOT cloud API. Separate from
 * the streaming connection — it builds a short-lived cloud client from the stored session, like
 * the device picker does. The mode lives on the camera, shared by all viewers.
 */
object CameraControl {
    // Camera Control service, night-shot property (C100 / chuangmi.camera.077ac1).
    private const val NIGHT_VISION_SIID = 2
    private const val NIGHT_VISION_PIID = 3

    private fun cloud(store: AppStore): Pair<MiCloud, String> {
        val session = store.loadSession() ?: throw XiaomiException("not signed in")
        val device = store.loadDevice() ?: throw XiaomiException("no camera selected")
        val cloud = MiCloud(platformHttp, session = session)
        cloud.onSessionRefreshed = { store.saveSession(it) } // AUTH-7
        return cloud to device.did
    }

    suspend fun getNightVision(store: AppStore): NightVisionMode? = withContext(ioDispatcher) {
        val (cloud, did) = cloud(store)
        val raw = cloud.miotGetProp(did, NIGHT_VISION_SIID, NIGHT_VISION_PIID)
        (raw as? Number)?.let { NightVisionMode.fromValue(it.toInt()) }
    }

    suspend fun setNightVision(store: AppStore, mode: NightVisionMode) = withContext(ioDispatcher) {
        val (cloud, did) = cloud(store)
        cloud.miotSetProp(did, NIGHT_VISION_SIID, NIGHT_VISION_PIID, mode.value)
    }
}
