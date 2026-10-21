using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BatteryViewer
{
    /// <summary>一个 HID 接口(顶层集合)的基本信息。</summary>
    public class HidInfo
    {
        public string Path;
        public int Vid, Pid, UsagePage, Usage;
        public int InputLength, OutputLength, FeatureLength;
        public string Product = "", Manufacturer = "", Serial = "";

        public override string ToString()
        {
            return string.Format("{0:X4}:{1:X4} page={2:X4} usage={3:X4} in={4} out={5} feat={6} \"{7}\"",
                Vid, Pid, UsagePage, Usage, InputLength, OutputLength, FeatureLength, Product);
        }
    }

    /// <summary>
    /// Windows HID 访问: 枚举接口、读写报文。插件常用的就三个:
    ///   Hid.Enumerate(vid, pid, usagePage, usage)     列出接口
    ///   new HidDevice(info.Path)                      打开接口
    ///   Hid.Transact(devices, send, match, timeout)  发请求, 等第一个匹配的回复
    /// </summary>
    public static class Hid
    {
        public static List<HidInfo> Enumerate(int? vid = null, int? pid = null, int? usagePage = null, int? usage = null)
        {
            var result = new List<HidInfo>();
            foreach (var path in InterfacePaths())
            {
                // 路径里带 vid_xxxx&pid_xxxx, 先按字符串粗筛, 少开很多句柄
                var low = path.ToLowerInvariant();
                if (vid.HasValue && !low.Contains(string.Format("vid_{0:x4}", vid.Value))) continue;
                if (pid.HasValue && !low.Contains(string.Format("pid_{0:x4}", pid.Value))) continue;
                var info = Query(path);
                if (info == null) continue;
                if (usagePage.HasValue && info.UsagePage != usagePage.Value) continue;
                if (usage.HasValue && info.Usage != usage.Value) continue;
                result.Add(info);
            }
            return result;
        }

        /// <summary>
        /// 清空缓冲 -> send() -> 在 devices 上读报文, 直到 match(报文) 返回非 null 或超时。
        /// 返回 match 的结果, 超时返回 null。中途收到的无关报文(如鼠标移动)会被跳过。
        /// </summary>
        public static T Transact<T>(IList<HidDevice> devices, Action send, Func<byte[], T> match, int timeoutMs) where T : class
        {
            foreach (var d in devices) d.Flush();
            send();
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0) return null;
                byte[] data;
                if (ReadAny(devices, left, out data) != null && data != null)
                {
                    var r = match(data);
                    if (r != null) return r;
                }
            }
        }

        /// <summary>在多个接口上同时等输入报文, 返回最先到的接口; 超时返回 null。</summary>
        public static HidDevice ReadAny(IList<HidDevice> devices, int timeoutMs, out byte[] data)
        {
            data = null;
            var events = new IntPtr[devices.Count];
            for (int i = 0; i < devices.Count; i++)
            {
                devices[i].ArmRead();
                events[i] = devices[i].ReadEvent;
            }
            uint rc = Native.WaitForMultipleObjects((uint)events.Length, events, false, (uint)Math.Max(0, timeoutMs));
            if (rc < events.Length)
            {
                var dev = devices[(int)rc];
                data = dev.CollectRead();
                return dev;
            }
            return null;
        }

        // ---------------------------------------------------------------- 枚举
        private static List<string> InterfacePaths()
        {
            var paths = new List<string>();
            Guid guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE) return paths;
            try
            {
                for (int i = 0; ; i++)
                {
                    var data = new Native.SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(data);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    int need;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(need + 4);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 64 位是 8, 32 位是 6
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, need, out need, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(buf + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        internal static HidInfo Query(string path)
        {
            // 访问权限 0: 鼠标/键盘这类被系统独占的集合也能打开读属性
            IntPtr h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE) return null;
            try
            {
                var attrs = new Native.HIDD_ATTRIBUTES();
                attrs.Size = Marshal.SizeOf(attrs);
                if (!Native.HidD_GetAttributes(h, ref attrs)) return null;
                var info = new HidInfo { Path = path, Vid = attrs.VendorID, Pid = attrs.ProductID };
                IntPtr pre;
                if (Native.HidD_GetPreparsedData(h, out pre))
                {
                    Native.HIDP_CAPS caps;
                    Native.HidP_GetCaps(pre, out caps);
                    Native.HidD_FreePreparsedData(pre);
                    info.UsagePage = caps.UsagePage; info.Usage = caps.Usage;
                    info.InputLength = caps.InputReportByteLength;
                    info.OutputLength = caps.OutputReportByteLength;
                    info.FeatureLength = caps.FeatureReportByteLength;
                }
                info.Product = HidString(h, Native.HidD_GetProductString);
                info.Manufacturer = HidString(h, Native.HidD_GetManufacturerString);
                info.Serial = HidString(h, Native.HidD_GetSerialNumberString);
                return info;
            }
            finally { Native.CloseHandle(h); }
        }

        private delegate bool HidStringFn(IntPtr h, byte[] buf, int len);

        private static string HidString(IntPtr h, HidStringFn fn)
        {
            var buf = new byte[512];
            if (!fn(h, buf, buf.Length)) return "";
            var s = Encoding.Unicode.GetString(buf);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }
    }

    /// <summary>以读写权限、overlapped 方式打开的 HID 接口。同一时刻每个句柄上只有一个读和一个写。</summary>
    public sealed class HidDevice : IDisposable
    {
        public readonly string Path;
        public readonly int InputLength, OutputLength;
        private IntPtr _handle;
        private IntPtr _readOv, _readBuf;
        private bool _reading;

        public HidDevice(string path)
        {
            Path = path;
            _handle = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_handle == Native.INVALID_HANDLE)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "打开 HID 接口失败: " + path);
            var info = Hid.Query(path);
            InputLength = info != null && info.InputLength > 0 ? info.InputLength : 64;
            OutputLength = info != null && info.OutputLength > 0 ? info.OutputLength : 64;
            _readOv = Native.NewOverlapped();
            _readBuf = Marshal.AllocHGlobal(InputLength);
        }

        internal IntPtr ReadEvent { get { return Native.OverlappedEvent(_readOv); } }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CancelRead();
            Native.CloseHandle(_handle);
            Native.FreeOverlapped(_readOv);
            Marshal.FreeHGlobal(_readBuf);
            _handle = IntPtr.Zero;
        }

        /// <summary>丢掉驱动缓冲里还没读的输入报文。</summary>
        public void Flush()
        {
            CancelRead();
            Native.HidD_FlushQueue(_handle);
        }

        /// <summary>写 Output Report, data[0] 是 Report ID; 不足报文长度时补 0。</summary>
        public void Write(byte[] data, int timeoutMs = 1000)
        {
            var buf = new byte[Math.Max(OutputLength, data.Length)];
            Array.Copy(data, buf, data.Length);
            IntPtr ov = Native.NewOverlapped();
            IntPtr mem = Marshal.AllocHGlobal(buf.Length);
            try
            {
                Marshal.Copy(buf, 0, mem, buf.Length);
                int done;
                if (!Native.WriteFile(_handle, mem, buf.Length, IntPtr.Zero, ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) throw new System.ComponentModel.Win32Exception(err, "WriteFile 失败");
                    if (Native.WaitForSingleObject(Native.OverlappedEvent(ov), (uint)timeoutMs) != 0)
                    {
                        Native.CancelIoEx(_handle, ov);
                        Native.GetOverlappedResult(_handle, ov, out done, true);
                        throw new TimeoutException("WriteFile 超时");
                    }
                }
                if (!Native.GetOverlappedResult(_handle, ov, out done, true))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WriteFile 失败");
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
                Native.FreeOverlapped(ov);
            }
        }

        /// <summary>读一个 Input Report(第一个字节是 Report ID), 超时返回 null。</summary>
        public byte[] Read(int timeoutMs = 1000)
        {
            byte[] data;
            Hid.ReadAny(new[] { this }, timeoutMs, out data);
            return data;
        }

        public byte[] GetFeature(byte reportId, int length)
        {
            var buf = new byte[length];
            buf[0] = reportId;
            return Native.HidD_GetFeature(_handle, buf, length) ? buf : null;
        }

        public bool SetFeature(byte[] data)
        {
            return Native.HidD_SetFeature(_handle, data, data.Length);
        }

        internal void ArmRead()
        {
            if (_reading) return;
            Native.ResetEvent(ReadEvent);
            if (!Native.ReadFile(_handle, _readBuf, InputLength, IntPtr.Zero, _readOv))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING) throw new System.ComponentModel.Win32Exception(err, "ReadFile 失败");
            }
            _reading = true;
        }

        internal byte[] CollectRead()
        {
            int done;
            bool ok = Native.GetOverlappedResult(_handle, _readOv, out done, false);
            _reading = false;
            if (!ok) return null;
            var data = new byte[done];
            Marshal.Copy(_readBuf, data, 0, done);
            return data;
        }

        private void CancelRead()
        {
            if (!_reading) return;
            Native.CancelIoEx(_handle, _readOv);
            int done;
            Native.GetOverlappedResult(_handle, _readOv, out done, true);
            _reading = false;
        }
    }

    internal static class Native
    {
        public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        public const int DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        // OVERLAPPED 放在非托管内存里, 异步 I/O 进行期间地址不能变
        public static IntPtr NewOverlapped()
        {
            int size = 2 * IntPtr.Size + 8 + IntPtr.Size;
            IntPtr ov = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(ov, i, 0);
            Marshal.WriteIntPtr(ov, 2 * IntPtr.Size + 8, CreateEvent(IntPtr.Zero, true, false, null));
            return ov;
        }

        public static IntPtr OverlappedEvent(IntPtr ov) { return Marshal.ReadIntPtr(ov, 2 * IntPtr.Size + 8); }

        public static void FreeOverlapped(IntPtr ov)
        {
            CloseHandle(OverlappedEvent(ov));
            Marshal.FreeHGlobal(ov);
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr data);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(IntPtr h);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetProductString(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetManufacturerString(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetSerialNumberString(IntPtr h, byte[] buf, int len);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
        public static extern IntPtr SetupDiGetClassDevsAll(IntPtr cls, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int need, IntPtr info);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA info);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int need);
        [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
        public static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[] buf, int size, out int need, int flags);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        public static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateEvent(IntPtr sa, bool manual, bool initial, string name);
        [DllImport("kernel32.dll")] public static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(IntPtr h, IntPtr buf, int len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(IntPtr h, IntPtr buf, int len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(IntPtr h, IntPtr ov, out int done, bool wait);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(IntPtr h, IntPtr ov);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern uint WaitForMultipleObjects(uint n, IntPtr[] handles, bool all, uint ms);
    }
}
