using System.Runtime.InteropServices;

namespace BabyMonitor.App.Services;

/// <summary>
/// libde265's C API, exactly as much of it as the picture needs.
///
/// The decoder is bundled with the app (see windows/hevc/build-libde265.ps1) because Windows has no
/// H.265 decoder of its own and the camera speaks nothing else. Everything here is a plain C call over
/// an opaque pointer: no COM, no Media Foundation, and nothing for a parent to install.
///
/// The API is a pump, and the order matters: push data in, call decode until it stops asking for more,
/// take pictures out, release each one. A picture that is not released is a buffer the decoder cannot
/// reuse, which is a leak that ends as a stall — so <see cref="de265_release_next_picture"/> is not
/// optional politeness, it is part of the loop.
/// </summary>
internal static class Libde265
{
    private const string Dll = "libde265";

    /// <summary>de265_error: 0 is OK. Everything else has text (<see cref="de265_get_error_text"/>).</summary>
    public const int DE265_OK = 0;

    /// <summary>
    /// Not an error worth reporting: the decoder simply wants more data before it can go on. It is the
    /// normal end of every decode pass, so treating it as a failure fills the log and hides real ones.
    /// (13 — from de265.h. Do not guess this: 10 is CANNOT_START_THREADPOOL, and mistaking the two makes
    /// every frame look like a stall.)
    /// </summary>
    public const int DE265_ERROR_WAITING_FOR_INPUT_DATA = 13;

    /// <summary>
    /// The decoder's output queue is full and it will not decode another frame until pictures are taken
    /// out of it (9 — de265.h). Not a fault in the stream: a fault in the caller's loop.
    /// </summary>
    public const int DE265_ERROR_IMAGE_BUFFER_FULL = 9;

    /// <summary>de265_chroma_420. The camera sends 4:2:0, which is the only format this renderer converts.</summary>
    public const int DE265_CHROMA_420 = 1;

    /// <summary>
    /// de265_param: do not output frames that failed to decode (default is to output them anyway).
    ///
    /// A monitor must not draw a picture it knows is wrong. Joining a live stream means the first frames
    /// arrive without their references, and libde265 will happily hand those over as a grey, blocky
    /// half-picture of a cot — which is precisely the kind of thing a parent should never be shown, since
    /// a broken frame of a sleeping baby looks a lot like a sleeping baby. Better to wait for a frame we
    /// can stand behind.
    /// </summary>
    public const int DE265_DECODER_PARAM_SUPPRESS_FAULTY_PICTURES = 6;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void de265_set_parameter_bool(IntPtr ctx, int param, int value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr de265_new_decoder();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_start_worker_threads(IntPtr ctx, int numberOfThreads);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_free_decoder(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void de265_reset(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_push_data(IntPtr ctx, IntPtr data, int length, long pts, IntPtr userData);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_flush_data(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_decode(IntPtr ctx, out int more);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr de265_get_next_picture(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void de265_release_next_picture(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_get_image_width(IntPtr image, int channel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_get_image_height(IntPtr image, int channel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_get_chroma_format(IntPtr image);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr de265_get_image_plane(IntPtr image, int channel, out int outStride);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int de265_get_bits_per_pixel(IntPtr image, int channel);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr de265_get_error_text(int code);

    public static string ErrorText(int code) =>
        Marshal.PtrToStringAnsi(de265_get_error_text(code)) ?? $"de265 error {code}";
}
