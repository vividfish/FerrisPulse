// ============================================================================
// 模块名称: LogitechDeviceHelper.cs
// 所属区域: Hardware (硬件驱动层)
// 核心职责:
//   罗技 (Logitech) HID++ 1.0/2.0 私有协议通信与电量检测实现。
//   Lightspeed 接收器（如 GPW 的 C539）用根功能 0 查特征 0x1001 电池电压。
//   应答必须对上功能号和软件标识。发送前排空迟到的包。
//   电量相对上次大幅下跌（包括 0% 和十几%）必须再读一次，两次接近才覆盖缓存。
//   同一条 HID++ 长报文通道：0x2201 功能 1 读 DPI 区间（可含步进），功能 2 读当前值，功能 3 写入。
//   0x8060 功能 0 的位图给出回报率档位（1–8 ms），功能 1 读当前毫秒，功能 2 写入。
//   鼠标接口被系统独占时用零权限打开识别在线，电量问询失败不得标成待机。
// 依赖关系: NativeMethods.cs, MouseBatteryInfo.cs
//
// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️:
// 1. [契约同步更新]: 任何 AI Agent 在修改本文件逻辑、协议解析或签名时，【必须】同步更新上方说明。
// 2. [C# 5 兼容铁律]: 严禁使用 C# 6+ 语法。
// 3. [编码格式锁定]: 本文件必须严格保存为【UTF-8 with BOM】编码。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RazerBatteryTray
{
public static class LogitechDeviceHelper
{
    private static readonly object hidLock = new object();
    private const string CacheRegistryKey = @"Software\RazerBatteryTray\LogitechCache";
    private static DateTime logitechProbeCooldownUtc = DateTime.MinValue;
    private static bool logitechSawMouseInterface = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid interfaceClassGuid;
        public int flags;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll", SetLastError = true)]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIo(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint WAIT_OBJECT_0 = 0;

    public static int CachedBatteryPercent = -1;
    public static bool CachedIsCharging = false;
    public static string CachedDeviceName = "罗技无线游戏鼠标";
    public static DateTime CachedLastUpdated = DateTime.MinValue;
    public static bool HasValidHardwareReading = false;
    public static int CachedDpi = -1;
    public static int CachedReportHz = -1;
    public static int CachedDpiMin = 100;
    public static int CachedDpiMax = 16000;
    public static int CachedDpiStep = 50;
    public static bool CachedDpiIsRange = true;
    private static int[] cachedDiscreteDpi = null;
    private static int[] cachedRateChoices = new int[] { 125, 250, 500, 1000 };
    private static Dictionary<string, int> deviceBatteryCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public static void LoadHardwareCache()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(CacheRegistryKey, false))
            {
                if (key != null)
                {
                    var bp = key.GetValue("BatteryPercent");
                    var isValidObj = key.GetValue("IsValid");
                    bool isValid = (isValidObj != null && Convert.ToInt32(isValidObj) == 1);

                    if (bp != null)
                    {
                        int val = Convert.ToInt32(bp);
                        if (val > 0 && val <= 100 && isValid)
                        {
                            CachedBatteryPercent = val;
                            HasValidHardwareReading = true;
                        }
                    }

                    var dn = key.GetValue("DeviceName") as string;
                    if (!string.IsNullOrEmpty(dn)) CachedDeviceName = dn;

                    var chg = key.GetValue("IsCharging");
                    if (chg != null) CachedIsCharging = (Convert.ToInt32(chg) != 0);

                    var updatedStr = key.GetValue("LastUpdated") as string;
                    if (!string.IsNullOrEmpty(updatedStr))
                    {
                        DateTime dt;
                        if (DateTime.TryParse(updatedStr, out dt)) CachedLastUpdated = dt;
                    }
                }
            }
        }
        catch { }
    }

    public static void SaveHardwareCache()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(CacheRegistryKey))
            {
                if (key != null)
                {
                    if (CachedBatteryPercent > 0)
                    {
                        key.SetValue("BatteryPercent", CachedBatteryPercent);
                        key.SetValue("IsValid", HasValidHardwareReading ? 1 : 0);
                    }
                    if (!string.IsNullOrEmpty(CachedDeviceName)) key.SetValue("DeviceName", CachedDeviceName);
                    key.SetValue("IsCharging", CachedIsCharging ? 1 : 0);
                    if (CachedLastUpdated != DateTime.MinValue) key.SetValue("LastUpdated", CachedLastUpdated.ToString("o"));
                }
            }
        }
        catch { }
    }

    public static int GetCachedBatteryForDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return CachedBatteryPercent;
        int val;
        if (deviceBatteryCache.TryGetValue(deviceId, out val) && val > 0)
        {
            return val;
        }
        return (HasValidHardwareReading && CachedBatteryPercent > 0) ? CachedBatteryPercent : -1;
    }

    private static byte[] SendAndReceive(IntPtr handle, IntPtr hEvent, byte[] tx, int inLen, int timeoutMs)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;

        // Drain pending reports
        byte[] drain = new byte[inLen];
        while (true)
        {
            OVERLAPPED dOl = new OVERLAPPED();
            dOl.hEvent = hEvent;
            uint readBytes;
            bool ok = ReadFile(handle, drain, (uint)drain.Length, out readBytes, ref dOl);
            if (!ok && Marshal.GetLastWin32Error() == 997)
            {
                if (WaitForSingleObject(hEvent, 10) == WAIT_OBJECT_0)
                {
                    GetOverlappedResult(handle, ref dOl, out readBytes, false);
                    continue;
                }
                CancelIo(handle);
            }
            break;
        }

        // Write command
        OVERLAPPED wOl = new OVERLAPPED();
        wOl.hEvent = hEvent;
        uint written;
        bool wOk = WriteFile(handle, tx, (uint)tx.Length, out written, ref wOl);
        if (!wOk && Marshal.GetLastWin32Error() == 997)
        {
            if (WaitForSingleObject(hEvent, 500) == WAIT_OBJECT_0)
            {
                GetOverlappedResult(handle, ref wOl, out written, false);
            }
            else
            {
                CancelIo(handle);
                return null;
            }
        }

        // Read response
        byte[] rx = new byte[inLen];
        OVERLAPPED rOl = new OVERLAPPED();
        rOl.hEvent = hEvent;
        uint rxRead = 0;
        bool rOk = ReadFile(handle, rx, (uint)rx.Length, out rxRead, ref rOl);
        if (!rOk && Marshal.GetLastWin32Error() == 997)
        {
            if (WaitForSingleObject(hEvent, (uint)timeoutMs) == WAIT_OBJECT_0)
            {
                GetOverlappedResult(handle, ref rOl, out rxRead, false);
                return rx;
            }
            else
            {
                CancelIo(handle);
                return null;
            }
        }
        else if (rOk)
        {
            return rx;
        }

        return null;
    }

    public static string GetModelName(int pid, string productString)
    {
        // 1. Specific PID database
        switch (pid)
        {
            case 0xC547:
            case 0x4093: return "Logitech G PRO X SUPERLIGHT 2";
            case 0x4092:
            case 0xC094: return "Logitech G PRO X SUPERLIGHT";
            case 0x4079:
            case 0xC088: return "Logitech G PRO Wireless (GPW)";
            case 0x4087:
            case 0xC08D: return "Logitech G502 LIGHTSPEED";
            case 0x408E:
            case 0xC096: return "Logitech G502 X PLUS / LIGHTSPEED";
            case 0x4074: return "Logitech G304 / G305 LIGHTSPEED";
            case 0x4070: return "Logitech G703 LIGHTSPEED (HERO)";
            case 0x4071: return "Logitech G903 LIGHTSPEED (HERO)";
            case 0x406E: return "Logitech G604 LIGHTSPEED";
            case 0x4068: return "Logitech G603 LIGHTSPEED";
            case 0x4069:
            case 0xB023: return "Logitech MX Master 3";
            case 0x4090: return "Logitech MX Master 3S";
            case 0x408F: return "Logitech MX Anywhere 3";
            case 0x406B: return "Logitech MX Keys 无线键盘";
            case 0x408A: return "Logitech MX Keys Mini";
            case 0x4082: return "Logitech G913 TKL 机械键盘";
            case 0x4086: return "Logitech G913 机械键盘";
            case 0x409B: return "Logitech G PRO X 60 游戏键盘";
            case 0x4088: return "Logitech G733 LIGHTSPEED 耳机";
            case 0x4097: return "Logitech G PRO X 2 LIGHTSPEED 耳机";
            case 0x4085: return "Logitech G435 无线耳机";
            case 0x4080: return "Logitech G533 无线耳机";
            case 0x407B: return "Logitech G935 无线耳机";
        }

        if (!string.IsNullOrEmpty(productString))
        {
            string p = productString.Trim();
            if (p.Length > 2 && 
                !p.Equals("USB Receiver", StringComparison.OrdinalIgnoreCase) &&
                !p.Equals("Lightspeed Receiver", StringComparison.OrdinalIgnoreCase) &&
                !p.Equals("Logitech Receiver", StringComparison.OrdinalIgnoreCase))
            {
                return p.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) ? p : ("Logitech " + p);
            }
        }

        // Receiver defaults
        if (pid == 0xC547) return "Logitech G PRO X SUPERLIGHT 2 (Lightspeed)";
        if (pid == 0xC539 || pid == 0xC53A) return "Logitech G 旗舰无线鼠标 (Lightspeed)";
        if (pid == 0xC545) return "Logitech G502 X LIGHTSPEED";

        return "Logitech 罗技无线游戏外设";
    }

    public static DeviceCategory DetectCategory(int pid, string name)
    {
        string text = (name + " " + pid.ToString("X4")).ToUpperInvariant();
        if (text.Contains("KEYBOARD") || text.Contains("键盘") || text.Contains("G913") || text.Contains("G915") || text.Contains("MX KEYS") || text.Contains("PRO X 60"))
        {
            return DeviceCategory.Keyboard;
        }
        if (text.Contains("HEADSET") || text.Contains("HEADPHONE") || text.Contains("耳机") || text.Contains("G733") || text.Contains("G533") || text.Contains("G935") || text.Contains("G435") || text.Contains("PRO X 2"))
        {
            return DeviceCategory.Headset;
        }
        return DeviceCategory.Mouse;
    }

    private static byte[] SendMatched(IntPtr handle, IntPtr hEvent, byte[] tx, int inLen, int timeoutMs)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || tx == null) return null;

        int drainLen = inLen > 0 ? inLen : tx.Length;
        for (int drained = 0; drained < 6; drained++)
        {
            byte[] drain = new byte[drainLen];
            OVERLAPPED dOl = new OVERLAPPED();
            dOl.hEvent = hEvent;
            uint readBytes;
            bool ok = ReadFile(handle, drain, (uint)drain.Length, out readBytes, ref dOl);
            if (!ok && Marshal.GetLastWin32Error() == 997)
            {
                if (WaitForSingleObject(hEvent, 12) != WAIT_OBJECT_0)
                {
                    CancelIo(handle);
                    ResetEvent(hEvent);
                    break;
                }
                GetOverlappedResult(handle, ref dOl, out readBytes, false);
                ResetEvent(hEvent);
                continue;
            }
            ResetEvent(hEvent);
            if (!ok) break;
        }

        OVERLAPPED wOl = new OVERLAPPED();
        wOl.hEvent = hEvent;
        uint written;
        bool wOk = WriteFile(handle, tx, (uint)tx.Length, out written, ref wOl);
        if (!wOk && Marshal.GetLastWin32Error() == 997)
        {
            if (WaitForSingleObject(hEvent, 200) != WAIT_OBJECT_0)
            {
                CancelIo(handle);
                return null;
            }
            GetOverlappedResult(handle, ref wOl, out written, false);
            ResetEvent(hEvent);
        }
        else if (!wOk)
        {
            return null;
        }
        else
        {
            ResetEvent(hEvent);
        }

        int waited = 0;
        while (waited < timeoutMs)
        {
            byte[] rx = new byte[inLen > 0 ? inLen : tx.Length];
            OVERLAPPED rOl = new OVERLAPPED();
            rOl.hEvent = hEvent;
            uint rxRead;
            bool rOk = ReadFile(handle, rx, (uint)rx.Length, out rxRead, ref rOl);
            if (!rOk && Marshal.GetLastWin32Error() == 997)
            {
                if (WaitForSingleObject(hEvent, 100) != WAIT_OBJECT_0)
                {
                    CancelIo(handle);
                    waited += 100;
                    continue;
                }
                GetOverlappedResult(handle, ref rOl, out rxRead, false);
                ResetEvent(hEvent);
            }
            else if (!rOk)
            {
                return null;
            }
            else
            {
                ResetEvent(hEvent);
            }
            waited += 15;
            if (rx.Length > 4 && rx[0] == tx[0] && rx[1] == tx[1] && HidppResponseMatches(tx, rx))
            {
                return rx;
            }
        }
        return null;
    }

    // 成功应答要回显功能号和软件标识。错误包的特征索引在 byte3，不能把别的请求的 0xFF 当成这次的结果。
    private static bool HidppResponseMatches(byte[] tx, byte[] rx)
    {
        if (tx == null || rx == null || tx.Length < 4 || rx.Length < 5) return false;
        if (rx[2] == tx[2])
        {
            return rx[3] == tx[3];
        }
        if (rx[2] == 0xFF && rx[3] == tx[2])
        {
            return (rx[4] & 0x0F) == (tx[3] & 0x0F);
        }
        return false;
    }

    private static byte LookupHidppFeature(IntPtr handle, IntPtr hEvent, int inLen, byte devIdx, ushort featureId)
    {
        byte[] tx = new byte[20];
        tx[0] = 0x11;
        tx[1] = devIdx;
        tx[2] = 0x00;
        tx[3] = 0x0A;
        tx[4] = (byte)((featureId >> 8) & 0xFF);
        tx[5] = (byte)(featureId & 0xFF);
        byte[] rx = SendMatched(handle, hEvent, tx, inLen, 350);
        if (rx == null || rx.Length < 5 || rx[2] == 0xFF) return 0;
        return rx[4];
    }

    private static int EstimatePercentFromMillivolts(int millivolts)
    {
        int[] volts = new int[] { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500 };
        int[] percents = new int[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0 };
        if (millivolts >= volts[0]) return 100;
        if (millivolts <= volts[volts.Length - 1]) return 0;
        for (int i = 0; i < volts.Length - 1; i++)
        {
            if (millivolts <= volts[i] && millivolts >= volts[i + 1])
            {
                int span = volts[i] - volts[i + 1];
                if (span <= 0) return percents[i];
                return percents[i + 1] + ((percents[i] - percents[i + 1]) * (millivolts - volts[i + 1]) / span);
            }
        }
        return 0;
    }

    private static bool TryReadDongleBattery(IntPtr handle, int inLen, out int batteryPercent, out bool isCharging)
    {
        batteryPercent = -1;
        isCharging = false;
        if (inLen < 7) inLen = 20;

        IntPtr hEvent = CreateEvent(IntPtr.Zero, false, false, null);
        if (hEvent == IntPtr.Zero) return false;
        try
        {
            byte devIdx = 0x01;
            byte voltageFeature = LookupHidppFeature(handle, hEvent, inLen, devIdx, 0x1001);
            if (voltageFeature != 0)
            {
                byte[] tx = new byte[20];
                tx[0] = 0x11;
                tx[1] = devIdx;
                tx[2] = voltageFeature;
                tx[3] = 0x0A;
                byte[] rx = SendMatched(handle, hEvent, tx, inLen, 350);
                if (rx != null && rx.Length > 6 && rx[2] == voltageFeature)
                {
                    int millivolts = (rx[4] << 8) | rx[5];
                    if (millivolts >= 2500 && millivolts <= 5000)
                    {
                        int percent = EstimatePercentFromMillivolts(millivolts);
                        bool charging = (rx[6] & 0x80) != 0;
                        bool sharpDrop = CachedBatteryPercent > 8 && (percent == 0 || percent + 12 < CachedBatteryPercent);
                        if (sharpDrop)
                        {
                            byte[] rx2 = SendMatched(handle, hEvent, tx, inLen, 350);
                            if (rx2 == null || rx2.Length <= 6 || rx2[2] != voltageFeature)
                            {
                                batteryPercent = -1;
                                return true;
                            }
                            int millivolts2 = (rx2[4] << 8) | rx2[5];
                            if (millivolts2 < 2500 || millivolts2 > 5000)
                            {
                                batteryPercent = -1;
                                return true;
                            }
                            int percent2 = EstimatePercentFromMillivolts(millivolts2);
                            int gapLow = percent2 - percent;
                            if (gapLow < 0) gapLow = -gapLow;
                            int gapCache = percent2 - CachedBatteryPercent;
                            if (gapCache < 0) gapCache = -gapCache;
                            if (gapLow > 8 && gapCache > 8)
                            {
                                batteryPercent = -1;
                                return true;
                            }
                            percent = percent2;
                            charging = (rx2[6] & 0x80) != 0;
                        }
                        batteryPercent = percent;
                        isCharging = charging;
                        return true;
                    }
                }
            }

            byte unified = LookupHidppFeature(handle, hEvent, inLen, devIdx, 0x1004);
            if (unified != 0)
            {
                byte[] tx = new byte[20];
                tx[0] = 0x11;
                tx[1] = devIdx;
                tx[2] = unified;
                tx[3] = 0x1A;
                byte[] rx = SendMatched(handle, hEvent, tx, inLen, 350);
                if (rx != null && rx.Length > 5 && rx[2] == unified && rx[4] <= 100 && !(CachedBatteryPercent > 8 && (rx[4] == 0 || rx[4] + 12 < CachedBatteryPercent)))
                {
                    batteryPercent = rx[4];
                    isCharging = rx.Length > 6 && (rx[6] == 1 || (rx[5] & 0x80) != 0);
                    return true;
                }
            }

            byte classic = LookupHidppFeature(handle, hEvent, inLen, devIdx, 0x1000);
            if (classic != 0)
            {
                byte[] tx = new byte[20];
                tx[0] = 0x11;
                tx[1] = devIdx;
                tx[2] = classic;
                tx[3] = 0x0A;
                byte[] rx = SendMatched(handle, hEvent, tx, inLen, 350);
                if (rx != null && rx.Length > 4 && rx[2] == classic && rx[4] <= 100 && !(CachedBatteryPercent > 8 && (rx[4] == 0 || rx[4] + 12 < CachedBatteryPercent)))
                {
                    batteryPercent = rx[4];
                    isCharging = rx.Length > 6 && rx[6] == 1;
                    return true;
                }
            }

            byte[] proto = new byte[20];
            proto[0] = 0x11;
            proto[1] = devIdx;
            proto[2] = 0x00;
            proto[3] = 0x1A;
            byte[] protoRx = SendMatched(handle, hEvent, proto, inLen, 350);
            return protoRx != null && protoRx.Length > 2 && protoRx[2] != 0xFF;
        }
        finally
        {
            CloseHandle(hEvent);
        }
    }

    private static bool IsLightspeedReceiver(int pid)
    {
        return pid == 0xC547 || pid == 0xC539 || pid == 0xC53A ||
               pid == 0xC545 || pid == 0xC548 || pid == 0xC52B ||
               pid == 0xC534 || pid == 0xC52F;
    }

    private static void ReadDpiCapability(IntPtr handle, IntPtr hEvent, int inLen, byte feature)
    {
        List<byte> raw = new List<byte>();
        for (int page = 0; page < 4; page++)
        {
            byte[] tx = new byte[20];
            tx[0] = 0x11;
            tx[1] = 0x01;
            tx[2] = feature;
            tx[3] = 0x1A;
            tx[4] = 0x00;
            tx[5] = 0x00;
            tx[6] = (byte)page;
            byte[] rx = SendMatched(handle, hEvent, tx, inLen, 250);
            if (rx == null || rx.Length < 7 || rx[2] == 0xFF) break;
            for (int b = 5; b < rx.Length; b++) raw.Add(rx[b]);
            if (raw.Count >= 2 && raw[raw.Count - 2] == 0 && raw[raw.Count - 1] == 0) break;
        }
        if (raw.Count < 2) return;

        List<int> discrete = new List<int>();
        int i = 0;
        while (i + 1 < raw.Count)
        {
            int val = (raw[i] << 8) | raw[i + 1];
            if (val == 0) break;
            if ((val >> 13) == 7)
            {
                int step = val & 0x1FFF;
                if (i + 3 >= raw.Count) break;
                int last = (raw[i + 2] << 8) | raw[i + 3];
                int first = discrete.Count > 0 ? discrete[0] : 0;
                if (first >= 50 && last > first && step > 0 && step <= 1000)
                {
                    CachedDpiMin = first;
                    CachedDpiMax = last;
                    CachedDpiStep = step;
                    CachedDpiIsRange = true;
                    cachedDiscreteDpi = null;
                    return;
                }
                i += 4;
            }
            else
            {
                if (val >= 50 && val <= 32000) discrete.Add(val);
                i += 2;
            }
        }
        if (discrete.Count == 0) return;
        if (discrete.Count <= 8)
        {
            CachedDpiIsRange = false;
            cachedDiscreteDpi = discrete.ToArray();
            CachedDpiMin = discrete[0];
            CachedDpiMax = discrete[discrete.Count - 1];
            CachedDpiStep = discrete.Count > 1 ? (discrete[1] - discrete[0]) : 50;
            if (CachedDpiStep <= 0) CachedDpiStep = 50;
            return;
        }
        CachedDpiIsRange = true;
        cachedDiscreteDpi = null;
        CachedDpiMin = discrete[0];
        CachedDpiMax = discrete[discrete.Count - 1];
        int gap = discrete[1] - discrete[0];
        CachedDpiStep = gap > 0 ? gap : 50;
    }

    private static int[] ReadReportRateChoices(IntPtr handle, IntPtr hEvent, int inLen, byte feature)
    {
        byte[] tx = new byte[20];
        tx[0] = 0x11;
        tx[1] = 0x01;
        tx[2] = feature;
        tx[3] = 0x0A;
        byte[] rx = SendMatched(handle, hEvent, tx, inLen, 250);
        if (rx == null || rx.Length < 5 || rx[2] == 0xFF) return null;
        int flags = rx[4];
        List<int> hz = new List<int>();
        for (int bit = 0; bit < 8; bit++)
        {
            if ((flags & (1 << bit)) == 0) continue;
            int ms = bit + 1;
            if (ms <= 0 || (1000 % ms) != 0) continue;
            int rate = 1000 / ms;
            bool seen = false;
            for (int n = 0; n < hz.Count; n++)
            {
                if (hz[n] == rate) seen = true;
            }
            if (!seen) hz.Add(rate);
        }
        if (hz.Count == 0) return null;
        hz.Sort();
        return hz.ToArray();
    }

    private static int ReadCurrentDpi(IntPtr handle, IntPtr hEvent, int inLen, byte feature)
    {
        byte[] tx = new byte[20];
        tx[0] = 0x11;
        tx[1] = 0x01;
        tx[2] = feature;
        tx[3] = 0x2A;
        tx[4] = 0x00;
        byte[] rx = SendMatched(handle, hEvent, tx, inLen, 250);
        if (rx == null || rx.Length < 7 || rx[2] == 0xFF) return -1;
        int dpi = (rx[5] << 8) | rx[6];
        if (dpi < 50 || dpi > 32000) return -1;
        return dpi;
    }

    private static void TryReadTuning(IntPtr handle, int inLen)
    {
        IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
        if (hEvent == IntPtr.Zero) return;
        try
        {
            byte dpiFeature = LookupHidppFeature(handle, hEvent, inLen, 0x01, 0x2201);
            if (dpiFeature != 0)
            {
                ReadDpiCapability(handle, hEvent, inLen, dpiFeature);
                int dpi = ReadCurrentDpi(handle, hEvent, inLen, dpiFeature);
                if (dpi > 0) CachedDpi = dpi;
            }

            byte rateFeature = LookupHidppFeature(handle, hEvent, inLen, 0x01, 0x8060);
            if (rateFeature != 0)
            {
                byte[] tx = new byte[20];
                tx[0] = 0x11;
                tx[1] = 0x01;
                tx[2] = rateFeature;
                tx[3] = 0x1A;
                byte[] rx = SendMatched(handle, hEvent, tx, inLen, 250);
                if (rx != null && rx.Length >= 5 && rx[2] != 0xFF)
                {
                    int ms = rx[4];
                    if (ms < 1 || ms > 8) ms = rx.Length > 5 ? rx[5] : 0;
                    if (ms >= 1 && ms <= 8 && (1000 % ms) == 0) CachedReportHz = 1000 / ms;
                }
                int[] choices = ReadReportRateChoices(handle, hEvent, inLen, rateFeature);
                if (choices != null && choices.Length > 0) cachedRateChoices = choices;
            }
        }
        finally
        {
            CloseHandle(hEvent);
        }
    }

    private static bool TryOpenHidpp(out IntPtr handle, out int inLen)
    {
        handle = IntPtr.Zero;
        inLen = 20;
        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);
        IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
        if (devInfo == IntPtr.Zero || devInfo.ToInt64() == -1) return false;
        try
        {
            SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf(ifData);
            uint memberIdx = 0;
            while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, memberIdx++, ref ifData))
            {
                uint reqSize;
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                if (reqSize == 0) continue;
                IntPtr detailBuffer = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 5);
                    if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailBuffer, reqSize, out reqSize, IntPtr.Zero)) continue;
                    IntPtr pDevicePath = new IntPtr(detailBuffer.ToInt64() + 4);
                    string devicePath = Marshal.PtrToStringAuto(pDevicePath);
                    if (devicePath == null || devicePath.ToLower().IndexOf("vid_046d") < 0) continue;

                    IntPtr opened = CreateFile(
                        devicePath,
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED,
                        IntPtr.Zero);
                    if (opened == IntPtr.Zero || opened.ToInt64() == -1) continue;

                    HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                    attr.Size = Marshal.SizeOf(attr);
                    int foundPid = 0;
                    if (HidD_GetAttributes(opened, ref attr)) foundPid = attr.ProductID;
                    if (!IsLightspeedReceiver(foundPid))
                    {
                        CloseHandle(opened);
                        continue;
                    }

                    IntPtr preparsed;
                    if (!HidD_GetPreparsedData(opened, out preparsed))
                    {
                        CloseHandle(opened);
                        continue;
                    }
                    HIDP_CAPS caps;
                    HidP_GetCaps(preparsed, out caps);
                    HidD_FreePreparsedData(preparsed);
                    if (caps.UsagePage == 0xFF00 && caps.Usage == 0x0002 && caps.InputReportByteLength >= 20)
                    {
                        handle = opened;
                        inLen = caps.InputReportByteLength;
                        return true;
                    }
                    CloseHandle(opened);
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
        return false;
    }

    public static bool TrySetDpi(int dpi)
    {
        if (dpi < 50 || dpi > 32000) return false;
        if (CachedDpiMax > CachedDpiMin && CachedDpiStep > 0)
        {
            if (dpi < CachedDpiMin) dpi = CachedDpiMin;
            if (dpi > CachedDpiMax) dpi = CachedDpiMax;
            int steps = (int)Math.Round((dpi - CachedDpiMin) / (double)CachedDpiStep);
            dpi = CachedDpiMin + steps * CachedDpiStep;
            if (dpi > CachedDpiMax) dpi = CachedDpiMax;
            if (dpi < CachedDpiMin) dpi = CachedDpiMin;
        }
        lock (hidLock)
        {
            IntPtr handle;
            int inLen;
            if (!TryOpenHidpp(out handle, out inLen)) return false;
            IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
            try
            {
                if (hEvent == IntPtr.Zero) return false;
                byte feature = LookupHidppFeature(handle, hEvent, inLen, 0x01, 0x2201);
                if (feature == 0) return false;
                byte[] tx = new byte[20];
                tx[0] = 0x11;
                tx[1] = 0x01;
                tx[2] = feature;
                tx[3] = 0x3A;
                tx[4] = 0x00;
                tx[5] = (byte)((dpi >> 8) & 0xFF);
                tx[6] = (byte)(dpi & 0xFF);
                byte[] rx = SendMatched(handle, hEvent, tx, inLen, 400);
                if (rx == null || rx.Length < 5 || rx[2] == 0xFF) return false;
                int applied = ReadCurrentDpi(handle, hEvent, inLen, feature);
                CachedDpi = applied > 0 ? applied : dpi;
                return true;
            }
            finally
            {
                if (hEvent != IntPtr.Zero) CloseHandle(hEvent);
                CloseHandle(handle);
            }
        }
    }

    public static bool TrySetReportRate(int hz)
    {
        int ms = 0;
        if (hz == 1000) ms = 1;
        else if (hz == 500) ms = 2;
        else if (hz == 250) ms = 4;
        else if (hz == 125) ms = 8;
        if (ms == 0) return false;
        lock (hidLock)
        {
            IntPtr handle;
            int inLen;
            if (!TryOpenHidpp(out handle, out inLen)) return false;
            IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
            try
            {
                if (hEvent == IntPtr.Zero) return false;
                byte feature = LookupHidppFeature(handle, hEvent, inLen, 0x01, 0x8060);
                if (feature == 0) return false;
                byte[] tx = new byte[20];
                tx[0] = 0x11;
                tx[1] = 0x01;
                tx[2] = feature;
                tx[3] = 0x2A;
                tx[4] = (byte)ms;
                byte[] rx = SendMatched(handle, hEvent, tx, inLen, 400);
                if (rx == null || rx.Length < 5 || rx[2] == 0xFF) return false;
                CachedReportHz = hz;
                return true;
            }
            finally
            {
                if (hEvent != IntPtr.Zero) CloseHandle(hEvent);
                CloseHandle(handle);
            }
        }
    }

    public static List<MouseBatteryInfo> QueryAllLogitechDevices()
    {
        lock (hidLock)
        {
            List<MouseBatteryInfo> results = new List<MouseBatteryInfo>();
            HashSet<string> processedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int hidppProbeMisses = 0;
            logitechSawMouseInterface = false;
            bool skipHidppProbe = DateTime.UtcNow < logitechProbeCooldownUtc;
            bool sawDongle = false;
            int sawDonglePid = 0;
            string sawDongleName = null;

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return results;

            SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
            uint memberIdx = 0;

            try
            {
                ifData.cbSize = Marshal.SizeOf(ifData);
                while (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, memberIdx++, ref ifData))
                {
                    uint reqSize;
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr detailBuffer = Marshal.AllocHGlobal((int)reqSize);
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 5);

                    if (SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailBuffer, reqSize, out reqSize, IntPtr.Zero))
                    {
                        IntPtr pDevicePath = new IntPtr(detailBuffer.ToInt64() + 4);
                        string devicePath = Marshal.PtrToStringAuto(pDevicePath);
                        if (devicePath != null && devicePath.ToLower().Contains("vid_046d"))
                        {
                            IntPtr handle = CreateFile(
                                devicePath,
                                GENERIC_READ | GENERIC_WRITE,
                                FILE_SHARE_READ | FILE_SHARE_WRITE,
                                IntPtr.Zero,
                                OPEN_EXISTING,
                                FILE_FLAG_OVERLAPPED,
                                IntPtr.Zero);
                            bool writable = !(handle == IntPtr.Zero || handle.ToInt64() == -1);
                            if (!writable)
                            {
                                // 鼠标集合被系统独占时，零权限打开仍能读到 Usage，用来判断鼠标是否在线。
                                handle = CreateFile(
                                    devicePath,
                                    0,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                                    IntPtr.Zero,
                                    OPEN_EXISTING,
                                    0,
                                    IntPtr.Zero);
                            }

                            if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                            {
                                try
                                {
                                    int foundVid = 0;
                                    int foundPid = 0;
                                    string foundName = null;

                                    HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                                    attr.Size = Marshal.SizeOf(attr);
                                    if (HidD_GetAttributes(handle, ref attr))
                                    {
                                        foundVid = attr.VendorID;
                                        foundPid = attr.ProductID;
                                    }

                                    StringBuilder sb = new StringBuilder(256);
                                    if (HidD_GetProductString(handle, sb, sb.Capacity))
                                    {
                                        string pStr = sb.ToString().Trim();
                                        if (!string.IsNullOrEmpty(pStr) && pStr.Length > 2) foundName = pStr;
                                    }

                                    IntPtr preparsed;
                                    if (HidD_GetPreparsedData(handle, out preparsed))
                                    {
                                        HIDP_CAPS caps;
                                        HidP_GetCaps(preparsed, out caps);
                                        HidD_FreePreparsedData(preparsed);

                                        if (caps.UsagePage == 0x01 && caps.Usage == 0x02)
                                        {
                                            logitechSawMouseInterface = true;
                                        }
                                        bool isDonglePidEarly = (foundPid == 0xC547 || foundPid == 0xC539 || foundPid == 0xC53A ||
                                                                 foundPid == 0xC545 || foundPid == 0xC548 || foundPid == 0xC52B ||
                                                                 foundPid == 0xC534 || foundPid == 0xC52F);
                                        if (isDonglePidEarly)
                                        {
                                            sawDongle = true;
                                            sawDonglePid = foundPid;
                                            if (!string.IsNullOrEmpty(foundName)) sawDongleName = foundName;
                                        }

                                        // Logitech HID++ endpoint detection:
                                        // UsagePage == 0xFF00 || UsagePage == 0xFF43, InputReportByteLength >= 7
                                        bool isHidppEndpoint = (caps.UsagePage == 0xFF00 || caps.UsagePage == 0xFF43) && 
                                                               (caps.InputReportByteLength >= 7 && caps.OutputReportByteLength >= 7);

                                        if (writable && isDonglePidEarly && caps.UsagePage == 0xFF00 && caps.Usage == 0x0002 && caps.InputReportByteLength >= 20)
                                        {
                                            string dongleKey = foundVid.ToString("X4") + ":" + foundPid.ToString("X4");
                                            if (!processedDevices.Contains(dongleKey))
                                            {
                                                int dongleBatt;
                                                bool dongleCharging;
                                                bool dongleAlive = TryReadDongleBattery(handle, caps.InputReportByteLength, out dongleBatt, out dongleCharging);
                                                if (dongleAlive)
                                                {
                                                    string dongleName = !string.IsNullOrEmpty(foundName) ? foundName : GetModelName(foundPid, null);
                                                    if (string.IsNullOrEmpty(dongleName) || dongleName.IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0)
                                                    {
                                                        dongleName = GetModelName(foundPid, foundName);
                                                    }
                                                    string dongleId = "Logitech:" + foundVid.ToString("X4") + ":" + foundPid.ToString("X4") + ":1";
                                                    if (dongleBatt >= 0)
                                                    {
                                                        CachedBatteryPercent = dongleBatt;
                                                        CachedIsCharging = dongleCharging;
                                                        CachedLastUpdated = DateTime.Now;
                                                        CachedDeviceName = dongleName;
                                                        HasValidHardwareReading = true;
                                                        deviceBatteryCache[dongleId] = dongleBatt;
                                                        SaveHardwareCache();
                                                    }
                                                    else
                                                    {
                                                        dongleBatt = GetCachedBatteryForDevice(dongleId);
                                                        dongleCharging = CachedIsCharging;
                                                    }
                                                    TryReadTuning(handle, caps.InputReportByteLength);
                                                    int[] dpiStages = CachedDpiIsRange ? null : cachedDiscreteDpi;
                                                    int dpiStage = 0;
                                                    if (dpiStages != null)
                                                    {
                                                        for (int stageIndex = 0; stageIndex < dpiStages.Length; stageIndex++)
                                                        {
                                                            if (dpiStages[stageIndex] == CachedDpi) dpiStage = stageIndex + 1;
                                                        }
                                                    }
                                                    int[] rateChoices = cachedRateChoices;
                                                    if (rateChoices == null || rateChoices.Length == 0)
                                                        rateChoices = new int[] { 125, 250, 500, 1000 };
                                                    results.Add(new MouseBatteryInfo
                                                    {
                                                        DeviceId = dongleId,
                                                        HardwareFingerprint = dongleId,
                                                        Brand = "Logitech",
                                                        DeviceName = dongleName,
                                                        Category = DeviceCategory.Mouse,
                                                        Transport = "Lightspeed 无线",
                                                        IsConnected = true,
                                                        IsSleeping = false,
                                                        IsDonglePresent = true,
                                                        IsCustom = false,
                                                        BatteryPercent = dongleBatt,
                                                        ExactBatteryPercent = dongleBatt,
                                                        IsCharging = dongleCharging,
                                                        LastUpdated = DateTime.Now,
                                                        Dpi = CachedDpi,
                                                        DpiStage = dpiStage,
                                                        DpiStageCount = dpiStages == null ? 0 : dpiStages.Length,
                                                        DpiStages = dpiStages,
                                                        DpiMin = CachedDpiMin,
                                                        DpiMax = CachedDpiMax,
                                                        DpiStep = CachedDpiStep,
                                                        PollingRate = CachedReportHz,
                                                        SupportedPollingRates = rateChoices
                                                    });
                                                    processedDevices.Add(dongleKey);
                                                }
                                            }
                                        }

                                        if (!isDonglePidEarly && isHidppEndpoint && !skipHidppProbe && caps.InputReportByteLength >= 20)
                                        {
                                            string devKey = foundVid.ToString("X4") + ":" + foundPid.ToString("X4");
                                            if (!processedDevices.Contains(devKey))
                                            {
                                                IntPtr hEvent = CreateEvent(IntPtr.Zero, true, false, null);
                                                if (hEvent != IntPtr.Zero)
                                                {
                                                    try
                                                    {
                                                        // 有线设备走 0xFF。Lightspeed / Unifying 接收器上的鼠标在无线槽，
                                                        // 0xFF 是接收器自身，先问它会超时，并把在线鼠标误标成休眠。
                                                        bool isDonglePid = (foundPid == 0xC547 || foundPid == 0xC539 || foundPid == 0xC53A ||
                                                                            foundPid == 0xC545 || foundPid == 0xC548 || foundPid == 0xC52B ||
                                                                            foundPid == 0xC534 || foundPid == 0xC52F);
                                                        byte[] targetIndices = isDonglePid ? new byte[] { 0x01 } : new byte[] { 0xFF, 0x01 };
                                                        bool foundOnline = false;
                                                        foreach (byte devIdx in targetIndices)
                                                        {
                                                            bool deviceOnline = false;
                                                            string detectedName = null;
                                                            int batteryPercent = -1;
                                                            bool isCharging = false;
                                                            int detectedPid = foundPid;

                                                            // 1. HID++ 2.0 Ping: Feature 0x0000 ROOT, Func 0x00, ping 0x5A
                                                            byte[] txPing = new byte[20];
                                                            txPing[0] = 0x11; // Long report
                                                            txPing[1] = devIdx;
                                                            txPing[2] = 0x00; // Feature 0x0000
                                                            txPing[3] = 0x00; // Func 0 (Ping)
                                                            txPing[4] = 0x00;
                                                            txPing[5] = 0x00;
                                                            txPing[6] = 0x5A; // Ping token

                                                            byte[] rxPing = SendAndReceive(handle, hEvent, txPing, caps.InputReportByteLength, isDonglePid ? 150 : 350);
                                                            if (rxPing != null && rxPing.Length >= 7 && rxPing[6] == 0x5A)
                                                            {
                                                                deviceOnline = true;
                                                            }
                                                            else
                                                            {
                                                                // Try Short Report ping (Report ID 0x10)
                                                                byte[] txShortPing = new byte[7] { 0x10, devIdx, 0x00, 0x00, 0x00, 0x00, 0x5A };
                                                                byte[] rxShortPing = SendAndReceive(handle, hEvent, txShortPing, caps.InputReportByteLength, isDonglePid ? 80 : 250);
                                                                if (rxShortPing != null && rxShortPing.Length >= 7 && rxShortPing[6] == 0x5A)
                                                                {
                                                                    deviceOnline = true;
                                                                }
                                                            }

                                                            // 2. Discover Features if online
                                                            if (deviceOnline)
                                                            {
                                                                // Feature 0x0005: Device Name Type
                                                                byte[] txFeatName = new byte[20];
                                                                txFeatName[0] = 0x11;
                                                                txFeatName[1] = devIdx;
                                                                txFeatName[2] = 0x00; // Root feature
                                                                txFeatName[3] = 0x00; // getFeature
                                                                txFeatName[4] = 0x00;
                                                                txFeatName[5] = 0x05; // Feature ID: 0x0005
                                                                byte[] rxFeatName = SendAndReceive(handle, hEvent, txFeatName, caps.InputReportByteLength, 250);
                                                                if (rxFeatName != null && rxFeatName.Length >= 5 && rxFeatName[4] > 0)
                                                                {
                                                                    byte nameFeatIdx = rxFeatName[4];
                                                                    // Get Name Count
                                                                    byte[] txNameCount = new byte[20];
                                                                    txNameCount[0] = 0x11;
                                                                    txNameCount[1] = devIdx;
                                                                    txNameCount[2] = nameFeatIdx;
                                                                    txNameCount[3] = 0x00; // Func 0: getCount
                                                                    byte[] rxNameCount = SendAndReceive(handle, hEvent, txNameCount, caps.InputReportByteLength, 200);
                                                                    if (rxNameCount != null && rxNameCount.Length >= 5 && rxNameCount[4] > 0)
                                                                    {
                                                                        int nameLen = rxNameCount[4];
                                                                        // Get Name String (Func 1: 0x10)
                                                                        byte[] txGetName = new byte[20];
                                                                        txGetName[0] = 0x11;
                                                                        txGetName[1] = devIdx;
                                                                        txGetName[2] = nameFeatIdx;
                                                                        txGetName[3] = 0x10; // Func 1: getName
                                                                        txGetName[4] = 0x00;
                                                                        byte[] rxGetName = SendAndReceive(handle, hEvent, txGetName, caps.InputReportByteLength, 250);
                                                                        if (rxGetName != null && rxGetName.Length > 5)
                                                                        {
                                                                            StringBuilder nsb = new StringBuilder();
                                                                            for (int c = 5; c < rxGetName.Length && (c - 5) < nameLen; c++)
                                                                            {
                                                                                if (rxGetName[c] == 0) break;
                                                                                nsb.Append((char)rxGetName[c]);
                                                                            }
                                                                            string str = nsb.ToString().Trim();
                                                                            if (str.Length > 2) detectedName = str;
                                                                        }
                                                                    }
                                                                }

                                                                // Feature 0x1004: Unified Battery
                                                                byte[] txFeat1004 = new byte[20];
                                                                txFeat1004[0] = 0x11;
                                                                txFeat1004[1] = devIdx;
                                                                txFeat1004[2] = 0x00;
                                                                txFeat1004[3] = 0x00;
                                                                txFeat1004[4] = 0x10;
                                                                txFeat1004[5] = 0x04; // 0x1004
                                                                byte[] rxFeat1004 = SendAndReceive(handle, hEvent, txFeat1004, caps.InputReportByteLength, 250);
                                                                if (rxFeat1004 != null && rxFeat1004.Length >= 5 && rxFeat1004[4] > 0)
                                                                {
                                                                    byte batFeatIdx = rxFeat1004[4];
                                                                    // Call getBatteryStatus (Func 1: 0x10)
                                                                    byte[] txBat = new byte[20];
                                                                    txBat[0] = 0x11;
                                                                    txBat[1] = devIdx;
                                                                    txBat[2] = batFeatIdx;
                                                                    txBat[3] = 0x10; // Func 1
                                                                    byte[] rxBat = SendAndReceive(handle, hEvent, txBat, caps.InputReportByteLength, 300);
                                                                    if (rxBat != null && rxBat.Length > 5)
                                                                    {
                                                                        int pct = rxBat[4];
                                                                        if (pct >= 0 && pct <= 100)
                                                                        {
                                                                            batteryPercent = pct;
                                                                            isCharging = (rxBat[5] == 1);
                                                                        }
                                                                    }
                                                                }

                                                                // Feature 0x1000: Standard Battery Status fallback
                                                                if (batteryPercent < 0)
                                                                {
                                                                    byte[] txFeat1000 = new byte[20];
                                                                    txFeat1000[0] = 0x11;
                                                                    txFeat1000[1] = devIdx;
                                                                    txFeat1000[2] = 0x00;
                                                                    txFeat1000[3] = 0x00;
                                                                    txFeat1000[4] = 0x10;
                                                                    txFeat1000[5] = 0x00; // 0x1000
                                                                    byte[] rxFeat1000 = SendAndReceive(handle, hEvent, txFeat1000, caps.InputReportByteLength, 250);
                                                                    if (rxFeat1000 != null && rxFeat1000.Length >= 5 && rxFeat1000[4] > 0)
                                                                    {
                                                                        byte batFeatIdx = rxFeat1000[4];
                                                                        // Call getBatteryLevelStatus (Func 0: 0x00)
                                                                        byte[] txBat = new byte[20];
                                                                        txBat[0] = 0x11;
                                                                        txBat[1] = devIdx;
                                                                        txBat[2] = batFeatIdx;
                                                                        txBat[3] = 0x00; // Func 0
                                                                        byte[] rxBat = SendAndReceive(handle, hEvent, txBat, caps.InputReportByteLength, 300);
                                                                        if (rxBat != null && rxBat.Length > 6)
                                                                        {
                                                                            int pct = rxBat[4];
                                                                            if (pct >= 0 && pct <= 100)
                                                                            {
                                                                                batteryPercent = pct;
                                                                                isCharging = (rxBat[6] == 1);
                                                                            }
                                                                        }
                                                                    }
                                                                }

                                                                // HID++ 1.0 Sub-ID 0x0D Fallback
                                                                if (batteryPercent < 0)
                                                                {
                                                                    byte[] txHidpp1 = new byte[7] { 0x10, devIdx, 0x0D, 0x00, 0x00, 0x00, 0x00 };
                                                                    byte[] rxHidpp1 = SendAndReceive(handle, hEvent, txHidpp1, caps.InputReportByteLength, 250);
                                                                    if (rxHidpp1 != null && rxHidpp1.Length >= 5 && rxHidpp1[2] == 0x0D)
                                                                    {
                                                                        int pct = rxHidpp1[3];
                                                                        if (pct >= 0 && pct <= 100)
                                                                        {
                                                                            batteryPercent = pct;
                                                                            isCharging = (rxHidpp1[4] == 1);
                                                                        }
                                                                    }
                                                                }
                                                            }

                                                            // Determine Device Name and ID
                                                            string finalName = !string.IsNullOrEmpty(detectedName) ? detectedName : GetModelName(detectedPid, foundName);
                                                            DeviceCategory cat = DetectCategory(detectedPid, finalName);
                                                            string deviceId = "Logitech:" + foundVid.ToString("X4") + ":" + detectedPid.ToString("X4") + (devIdx != 0xFF ? (":" + devIdx) : "");

                                                            // If device was online and battery read, update cache
                                                            if (batteryPercent >= 0)
                                                            {
                                                                CachedBatteryPercent = batteryPercent;
                                                                CachedIsCharging = isCharging;
                                                                CachedLastUpdated = DateTime.Now;
                                                                CachedDeviceName = finalName;
                                                                HasValidHardwareReading = true;
                                                                deviceBatteryCache[deviceId] = batteryPercent;
                                                                SaveHardwareCache();
                                                            }

                                                            int displayBattery = (batteryPercent >= 0) ? batteryPercent : GetCachedBatteryForDevice(deviceId);
                                                            bool displayCharging = (batteryPercent >= 0) ? isCharging : CachedIsCharging;

                                                            if (deviceOnline)
                                                            {
                                                                MouseBatteryInfo info = new MouseBatteryInfo
                                                                {
                                                                    DeviceId = deviceId,
                                                                    HardwareFingerprint = deviceId,
                                                                    Brand = "Logitech",
                                                                    DeviceName = finalName,
                                                                    Category = cat,
                                                                    Transport = (devIdx == 0xFF) ? "USB 有线" : "Lightspeed 无线",
                                                                    IsConnected = true,
                                                                    IsSleeping = false,
                                                                    IsDonglePresent = true,
                                                                    IsCustom = false,
                                                                    BatteryPercent = displayBattery,
                                                                    ExactBatteryPercent = displayBattery,
                                                                    IsCharging = displayCharging,
                                                                    LastUpdated = (batteryPercent >= 0) ? DateTime.Now : (CachedLastUpdated != DateTime.MinValue ? CachedLastUpdated : DateTime.Now),
                                                                    Dpi = CachedDpi,
                                                                    DpiStage = 0,
                                                                    DpiStageCount = 0,
                                                                    DpiStages = null,
                                                                    DpiMin = CachedDpiMin,
                                                                    DpiMax = CachedDpiMax,
                                                                    DpiStep = CachedDpiStep,
                                                                    PollingRate = CachedReportHz,
                                                                    SupportedPollingRates = cachedRateChoices
                                                                };
                                                                results.Add(info);
                                                                foundOnline = true;
                                                                break;
                                                            }
                                                        }

                                                        if (foundOnline)
                                                        {
                                                            processedDevices.Add(devKey);
                                                        }
                                                        else
                                                        {
                                                            hidppProbeMisses++;
                                                            if (!isDonglePid || hidppProbeMisses >= 1)
                                                            {
                                                                processedDevices.Add(devKey);
                                                                logitechProbeCooldownUtc = DateTime.UtcNow.AddSeconds(30);
                                                            }
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        CloseHandle(hEvent);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    CloseHandle(handle);
                                }
                            }
                        }
                    }
                    Marshal.FreeHGlobal(detailBuffer);
                    ifData.cbSize = Marshal.SizeOf(ifData);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }

            if (results.Count == 0 && sawDongle)
            {
                string finalName = !string.IsNullOrEmpty(sawDongleName) ? sawDongleName : GetModelName(sawDonglePid, null);
                string deviceId = "Logitech:046D:" + sawDonglePid.ToString("X4") + ":1";
                int displayBattery = GetCachedBatteryForDevice(deviceId);
                results.Add(new MouseBatteryInfo
                {
                    DeviceId = deviceId,
                    HardwareFingerprint = deviceId,
                    Brand = "Logitech",
                    DeviceName = finalName,
                    Category = DetectCategory(sawDonglePid, finalName),
                    Transport = "Lightspeed 无线",
                    IsConnected = true,
                    IsSleeping = !logitechSawMouseInterface,
                    IsDonglePresent = true,
                    IsCustom = false,
                    BatteryPercent = displayBattery,
                    ExactBatteryPercent = displayBattery,
                    IsCharging = CachedIsCharging,
                    LastUpdated = CachedLastUpdated != DateTime.MinValue ? CachedLastUpdated : DateTime.Now,
                    Dpi = CachedDpi,
                    DpiStage = 0,
                    DpiStageCount = 0,
                    DpiStages = null,
                    DpiMin = CachedDpiMin,
                    DpiMax = CachedDpiMax,
                    DpiStep = CachedDpiStep,
                    PollingRate = CachedReportHz,
                    SupportedPollingRates = cachedRateChoices
                });
            }

            return results;
        }
    }

        public static MouseBatteryInfo QueryLogitechDeviceInfo(string deviceFingerprint = null)
        {
            var list = QueryAllLogitechDevices();
            if (list == null || list.Count == 0) return null;
            if (!string.IsNullOrEmpty(deviceFingerprint))
            {
                foreach (var d in list)
                {
                    if (d.DeviceId != null && (d.DeviceId.Equals(deviceFingerprint, StringComparison.OrdinalIgnoreCase) ||
                                               (d.HardwareFingerprint != null && d.HardwareFingerprint.Equals(deviceFingerprint, StringComparison.OrdinalIgnoreCase))))
                    {
                        return d;
                    }
                }
            }
            return list[0];
        }
    }
}