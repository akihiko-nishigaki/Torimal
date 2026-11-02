using System.Runtime.InteropServices;

namespace Torimaru.App.Audio;

/// <summary>音声デバイス 1 個。</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>再生 / 録音デバイスを列挙する。</summary>
public static class AudioDevices
{
    /// <summary>マイクなどの録音デバイス。</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        => Enumerate(WasapiInterop.DataFlow.Capture);

    /// <summary>スピーカーなどの再生デバイス（ループバック録音の対象）。</summary>
    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
        => Enumerate(WasapiInterop.DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(WasapiInterop.DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();
        WasapiInterop.IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = (WasapiInterop.IMMDeviceEnumerator)new WasapiInterop.MMDeviceEnumerator();

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(flow, WasapiInterop.Role.Multimedia, out var defaultDevice) >= 0)
            {
                try
                {
                    defaultDevice.GetId(out defaultId);
                }
                finally
                {
                    Marshal.ReleaseComObject(defaultDevice);
                }
            }

            WasapiInterop.ThrowIfFailed(
                enumerator.EnumAudioEndpoints(flow, WasapiInterop.DEVICE_STATE_ACTIVE, out var collection),
                "音声デバイスの列挙");

            try
            {
                WasapiInterop.ThrowIfFailed(collection.GetCount(out var count), "デバイス数の取得");

                for (var i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0)
                    {
                        continue;
                    }

                    try
                    {
                        device.GetId(out var id);
                        var name = ReadFriendlyName(device) ?? id;
                        devices.Add(new AudioDeviceInfo(id, name, id == defaultId));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"音声デバイスの列挙に失敗 ({flow})", ex);
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        return devices;
    }

    private static string? ReadFriendlyName(WasapiInterop.IMMDevice device)
    {
        if (device.OpenPropertyStore(WasapiInterop.STGM_READ, out var store) < 0)
        {
            return null;
        }

        try
        {
            var key = WasapiInterop.PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }

            try
            {
                return value.AsString();
            }
            finally
            {
                WasapiInterop.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}
