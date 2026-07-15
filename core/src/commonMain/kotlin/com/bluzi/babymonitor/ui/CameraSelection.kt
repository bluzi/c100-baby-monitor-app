package com.bluzi.babymonitor.ui

/**
 * CAM-6: with exactly one camera on the account there is no choice to make, so it is selected
 * automatically and its live feed opens — the picker appears only when there is a genuine choice
 * (more than one) or none at all (CAM-5). One rule, so a phone, a Mac and a PC cannot come to
 * different conclusions about when the picker is worth showing.
 */
object CameraSelection {
    fun autoSelectsSingle(cameraCount: Int): Boolean = cameraCount == 1
}
