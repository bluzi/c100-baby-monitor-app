package com.bluzi.babymonitor.monitor

/** Android's media stack, handed to the engine. Everything above this is platform-free. */
object AndroidMedia : MediaFactory {
    override fun audio(onPcmWindow: (pcm: ShortArray, sampleRate: Int) -> Unit): AudioOutput =
        OpusAudioPlayer(onPcmWindow = onPcmWindow)

    override fun video(): VideoOutput = AndroidVideoOutput()
}
