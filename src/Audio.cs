using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScrollVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint pcDevices);
        void Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        void OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        void GetState(out int pdwState);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr pNotify);
        void UnregisterControlChangeNotify(IntPtr pNotify);
        void GetChannelCount(out uint pnChannelCount);
        void SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        void SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        void GetMasterVolumeLevel(out float pfLevelDB);
        void GetMasterVolumeLevelScalar(out float pfLevel);
        void SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        void SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        void GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        void GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    // 既定デバイス切替用（Windows 標準のサウンド設定も内部で使っている非公開インターフェイス）
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    sealed class AudioDevice
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    /// <summary>
    /// 既定の再生デバイスのマスター音量を扱う。
    /// 毎回「今の既定デバイス」を確認し、変わっていたら・エラーが出たら丸ごと取り直す。
    /// 通知コールバックは使わず、ポーリングのみ（スリープ復帰やサービス再起動で通知が途絶える問題を避ける）。
    /// </summary>
    sealed class Audio : IDisposable
    {
        const int eRender = 0, eConsole = 0, CLSCTX_ALL = 23, DEVICE_STATE_ACTIVE = 1;
        static Guid Context = new Guid("6F1C2C3E-6E5B-4C59-9E0B-3D7B2A1C9F41");
        static PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pvar);

        IMMDeviceEnumerator enumerator;
        IMMDevice device;
        IAudioEndpointVolume volume;
        string deviceId;

        bool Ensure()
        {
            try
            {
                if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice d;
                int hr = enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out d);
                if (hr != 0 || d == null) { ReleaseDevice(); return false; }
                string id;
                d.GetId(out id);
                if (volume != null && id == deviceId) { Marshal.ReleaseComObject(d); return true; }

                ReleaseDevice();
                device = d;
                deviceId = id;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
                volume = (IAudioEndpointVolume)o;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Audio.Ensure failed: " + ex.Message);
                Reset();
                return false;
            }
        }

        public bool TryGet(out int percent, out bool muted)
        {
            percent = -1; muted = false;
            if (!Ensure()) return false;
            try
            {
                float f;
                volume.GetMasterVolumeLevelScalar(out f);
                volume.GetMute(out muted);
                percent = (int)Math.Round(f * 100f);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Audio.TryGet failed: " + ex.Message);
                Reset();
                return false;
            }
        }

        public bool SetPercent(int percent)
        {
            if (!Ensure()) return false;
            try
            {
                percent = Math.Max(0, Math.Min(100, percent));
                volume.SetMasterVolumeLevelScalar(percent / 100f, ref Context);
                return true;
            }
            catch (Exception ex) { Log.Write("Audio.SetPercent failed: " + ex.Message); Reset(); return false; }
        }

        public bool SetMute(bool mute)
        {
            if (!Ensure()) return false;
            try { volume.SetMute(mute, ref Context); return true; }
            catch (Exception ex) { Log.Write("Audio.SetMute failed: " + ex.Message); Reset(); return false; }
        }

        public bool ToggleMute()
        {
            int p; bool m;
            if (!TryGet(out p, out m)) return false;
            return SetMute(!m);
        }

        public List<AudioDevice> ListDevices()
        {
            List<AudioDevice> list = new List<AudioDevice>();
            if (!Ensure()) return list;
            IMMDeviceCollection col = null;
            try
            {
                enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col);
                uint n;
                col.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d = null;
                    try
                    {
                        col.Item(i, out d);
                        AudioDevice ad = new AudioDevice();
                        d.GetId(out ad.Id);
                        ad.Name = FriendlyName(d) ?? ad.Id;
                        ad.IsDefault = ad.Id == deviceId;
                        list.Add(ad);
                    }
                    catch (Exception ex) { Log.Write("ListDevices item: " + ex.Message); }
                    finally { if (d != null) Marshal.ReleaseComObject(d); }
                }
            }
            catch (Exception ex) { Log.Write("ListDevices failed: " + ex.Message); }
            finally { if (col != null) Marshal.ReleaseComObject(col); }
            return list;
        }

        internal static string FriendlyName(IMMDevice d)
        {
            IPropertyStore ps = null;
            try
            {
                d.OpenPropertyStore(0, out ps);
                PROPVARIANT pv;
                PROPERTYKEY key = PKEY_FriendlyName;
                ps.GetValue(ref key, out pv);
                string s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.pointerValue) : null;
                PropVariantClear(ref pv);
                return s;
            }
            catch { return null; }
            finally { if (ps != null) Marshal.ReleaseComObject(ps); }
        }

        public static bool SetDefaultDevice(string id)
        {
            object o = null;
            try
            {
                o = new PolicyConfigClient();
                IPolicyConfig pc = (IPolicyConfig)o;
                for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role);
                return true;
            }
            catch (Exception ex) { Log.Write("SetDefaultDevice failed: " + ex.Message); return false; }
            finally { if (o != null) Marshal.ReleaseComObject(o); }
        }

        void ReleaseDevice()
        {
            if (volume != null) { try { Marshal.ReleaseComObject(volume); } catch { } volume = null; }
            if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } device = null; }
            deviceId = null;
        }

        /// <summary>全部捨てて次回アクセス時に作り直す。</summary>
        public void Reset()
        {
            ReleaseDevice();
            if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } enumerator = null; }
        }

        public void Dispose() { Reset(); }
    }
}
