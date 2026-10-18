// ============================================================================
// 模块名称: AppleProximityWatcher.cs
// 所属区域: Hardware (硬件驱动层)
// 核心职责:
//   持续监听 Apple 厂商广播，把近距配对包交给电量解析，不占用耳机的音频通道
// 依赖关系: AppleProximityAdvertisement.cs
//
// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️:
// 1. [契约同步更新]: 任何 AI Agent 在修改本文件逻辑、协议解析或签名时，【必须】同步更新上方说明。
// 2. [C# 5 兼容铁律]: 严禁使用 C# 6+ 语法。
// 3. [编码格式锁定]: 本文件必须严格保存为【UTF-8 with BOM】编码。
// ============================================================================

using System;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace RazerBatteryTray
{
    public static class AppleProximityWatcher
    {
        [ComImport]
        [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IBufferByteAccess
        {
            IntPtr GetBuffer();
        }

        static readonly object Gate = new object();
        static BluetoothLEAdvertisementWatcher watcher;
        static TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received;
        static TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> stopped;
        static bool stopping;

        public static void EnsureStarted()
        {
            try
            {
                lock (Gate)
                {
                    if (watcher == null)
                    {
                        watcher = new BluetoothLEAdvertisementWatcher();
                        watcher.ScanningMode = BluetoothLEScanningMode.Active;
                        received = OnReceived;
                        stopped = OnStopped;
                        watcher.Received += received;
                        watcher.Stopped += stopped;
                    }
                    if (stopping) return;
                    BluetoothLEAdvertisementWatcherStatus status = watcher.Status;
                    if (status == BluetoothLEAdvertisementWatcherStatus.Started) return;
                    if (status == BluetoothLEAdvertisementWatcherStatus.Stopping) return;
                    watcher.ScanningMode = BluetoothLEScanningMode.Active;
                    watcher.Start();
                }
            }
            catch
            {
            }
        }

        public static void Stop()
        {
            try
            {
                lock (Gate)
                {
                    stopping = true;
                    if (watcher != null)
                    {
                        BluetoothLEAdvertisementWatcherStatus status = watcher.Status;
                        if (status == BluetoothLEAdvertisementWatcherStatus.Started)
                        {
                            watcher.Stop();
                        }
                    }
                }
            }
            catch
            {
            }
        }

        static void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                if (args == null || args.Advertisement == null) return;
                var list = args.Advertisement.ManufacturerData;
                if (list == null) return;
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    BluetoothLEManufacturerData section = list[i];
                    if (section == null || section.CompanyId != 0x004C) continue;
                    byte[] data = CopyBuffer(section.Data);
                    if (data != null) AppleProximityAdvertisement.Offer(data);
                }
            }
            catch
            {
            }
        }

        static void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (stopping || sender == null) return;
            try
            {
                if (sender.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                {
                    sender.ScanningMode = BluetoothLEScanningMode.Active;
                    sender.Start();
                }
            }
            catch
            {
            }
        }

        static byte[] CopyBuffer(IBuffer buffer)
        {
            if (buffer == null) return null;
            int length = (int)buffer.Length;
            if (length <= 0 || length > 64) return null;
            IntPtr ptr = ((IBufferByteAccess)buffer).GetBuffer();
            if (ptr == IntPtr.Zero) return null;
            byte[] data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
            return data;
        }
    }
}
