namespace BabyMonitor.Core.Monitor;

/// <summary>
/// WATCH-12: is the picture still moving?
///
/// The mirror of <see cref="StreamWatchdog"/>. That one guards audio dying while video flows; this one
/// guards video dying while audio flows — which is worse, because nothing about it looks wrong. The
/// feed is live, the status says so truthfully, the crying alarm is listening, and the parent is
/// looking at a photograph of a quiet cot.
///
/// "Moving" is judged on **what arrived**, not on what was drawn: a decoder is a shell's business, and
/// a picture that stopped arriving, a picture whose timeline stopped, and a camera repeating one frame
/// are one failure to a parent. Each shows up here as "nothing changed", and none of them needs a
/// decoded pixel to notice.
///
/// Deliberately pure — no clock, no sockets, `nowMs` passed in — so the same decision runs here and in
/// the Kotlin core, and so it can be tested without a camera.
/// </summary>
public sealed class PictureLiveness
{
    /// <summary>
    /// Long enough that a working feed can never look frozen — the picture carries a clock, so it
    /// changes every second — and short enough that nobody trusts a photograph for long.
    /// </summary>
    public const long FreezeMsDefault = 10_000;

    private const int SampleBytes = 64;

    // Written by the task reading frames off the camera, read by the watchdog's tick loop — two threads,
    // like MonitorHub.LastAudioAtMs next door, and synchronised the same way it is. Unsynchronised, the
    // tick could read a stale change time and call a working feed frozen (a reconnect, and a gap in the
    // sound, for nothing) or miss a real freeze; a plain long can be read torn as well. `volatile` is
    // not allowed on long in C#, hence Interlocked for those two.
    private long _lastChangeMs;
    private volatile int _lastSignature;
    private volatile bool _seenPicture;

    /// <summary>How long a still picture is allowed to stand before it is called frozen.</summary>
    public long FreezeMs { get; set; } = FreezeMsDefault;

    /// <summary>
    /// A video frame arrived. <paramref name="annexB"/> is the encoded frame as it came off the wire —
    /// its bytes are the cheapest honest answer to "is this a different picture?", because the camera
    /// burns a clock into the image, so no two seconds of a working feed encode alike.
    ///
    /// The frame's timestamp is deliberately **not** consulted. It advances on every frame of a live
    /// stream whether or not the picture behind it is moving, so believing it would mean a repeated
    /// frame — the failure that matters most here, because it is the one that still looks like a busy
    /// socket and a live feed — could never be seen at all. Only the picture speaks for the picture.
    /// </summary>
    public void OnFrame(byte[] annexB, long nowMs)
    {
        var signature = SignatureOf(annexB);
        var changed = !_seenPicture || signature != _lastSignature;
        _seenPicture = true;
        _lastSignature = signature;
        if (changed)
        {
            Interlocked.Exchange(ref _lastChangeMs, nowMs);
        }
    }

    /// <summary>A new session, or a picture that has just started: nothing is stale yet.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastChangeMs, 0);
        _lastSignature = 0;
        _seenPicture = false;
    }

    /// <summary>
    /// Has the picture stood still too long?
    ///
    /// False until a picture has actually been seen: a camera that never sends video is a capability
    /// gap that has already been said out loud (LIVE-7, DESK-22), not a freeze, and reconnecting for
    /// ever over a picture that was never coming would take the sound down with it.
    /// </summary>
    public bool Frozen(long nowMs) => _seenPicture && nowMs - Interlocked.Read(ref _lastChangeMs) > FreezeMs;

    private static int SignatureOf(byte[] annexB)
    {
        // FNV-1a over the length and a bounded sample of the bytes. A full hash of a 2304x1296
        // keyframe, 25 times a second, would be real work for a decision that only needs "did anything
        // at all change" — and an encoder cannot hold length, head, middle and tail identical across a
        // frame that differs.
        unchecked
        {
            var h = (int)2166136261;
            void Mix(int b) => h = (h ^ b) * 16777619;

            Mix(annexB.Length);
            if (annexB.Length == 0)
            {
                return h;
            }

            var step = annexB.Length <= SampleBytes ? 1 : annexB.Length / SampleBytes;
            for (var i = 0; i < annexB.Length; i += step)
            {
                Mix(annexB[i]);
            }

            Mix(annexB[^1]);
            return h;
        }
    }
}
