using BabyMonitor.Core.Data;
using BabyMonitor.Core.Net;
using BabyMonitor.Core.Xiaomi;

namespace BabyMonitor.Core.Monitor;

/// <summary>
/// LIVE-10 / PROTO-24: camera-side settings (night vision) over the MiOT cloud API. Separate from the
/// streaming connection — it builds a short-lived cloud client from the stored session, like the device
/// picker does. The mode lives on the camera, shared by all viewers.
/// </summary>
public static class CameraControl
{
    // Camera Control service, night-shot property (C100 / chuangmi.camera.077ac1).
    private const int NightVisionSiid = 2;
    private const int NightVisionPiid = 3;

    public static async Task<NightVisionMode?> GetNightVisionAsync(
        AppStore store,
        IMiHttp? http = null,
        CancellationToken ct = default)
    {
        var (cloud, did) = Cloud(store, http);
        var raw = await cloud.MiotGetPropAsync(did, NightVisionSiid, NightVisionPiid, ct).ConfigureAwait(false);
        return raw switch
        {
            long l => Mi.NightVisionFromValue((int)l),
            double d => Mi.NightVisionFromValue((int)d),
            _ => null,
        };
    }

    public static async Task SetNightVisionAsync(
        AppStore store,
        NightVisionMode mode,
        IMiHttp? http = null,
        CancellationToken ct = default)
    {
        var (cloud, did) = Cloud(store, http);
        await cloud.MiotSetPropAsync(did, NightVisionSiid, NightVisionPiid, (int)mode, ct).ConfigureAwait(false);
    }

    private static (MiCloud Cloud, string Did) Cloud(AppStore store, IMiHttp? http)
    {
        var session = store.LoadSession() ?? throw new XiaomiException("not signed in");
        var device = store.LoadDevice() ?? throw new XiaomiException("no camera selected");
        var cloud = new MiCloud(http ?? SystemMiHttp.Shared, session: session)
        {
            OnSessionRefreshed = s =>
            {
                store.SaveSession(s); // AUTH-7
                return Task.CompletedTask;
            },
        };
        return (cloud, device.Did);
    }
}
