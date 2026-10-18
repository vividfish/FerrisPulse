// ============================================================================
// 模块名称: AppleProximityAdvertisement.cs
// 所属区域: Hardware (硬件驱动层)
// 核心职责:
//   解析 Apple 连续性协议里的近距配对广播，取出 Beats / AirPods 的十档电量
// 依赖关系: 无
//
// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️:
// 1. [契约同步更新]: 任何 AI Agent 在修改本文件逻辑、协议解析或签名时，【必须】同步更新上方说明。
// 2. [C# 5 兼容铁律]: 严禁使用 C# 6+ 语法。
// 3. [编码格式锁定]: 本文件必须严格保存为【UTF-8 with BOM】编码。
// ============================================================================

using System;
using System.Collections.Generic;

namespace RazerBatteryTray
{
    public static class AppleProximityAdvertisement
    {
        const long FreshTicks = 120L * 10000000L;
        static readonly object Gate = new object();
        static readonly Dictionary<ushort, Sample> ByModel = new Dictionary<ushort, Sample>();
        static readonly Dictionary<string, bool> ChargingByMac = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        sealed class Sample
        {
            public int Percent;
            public bool Charging;
            public long Ticks;
        }

        public static bool TryParse(byte[] data, out ushort model, out int percent, out bool charging)
        {
            model = 0;
            percent = -1;
            charging = false;
            if (data == null || data.Length < 27) return false;
            if (data[0] != 0x07 || data[1] != 0x19) return false;

            model = (ushort)(data[3] | (data[4] << 8));
            bool fromLeft = (data[5] & 0x20) != 0;
            int curr = data[6] & 0x0F;
            int anot = (data[6] >> 4) & 0x0F;
            bool currCharging = (data[7] & 0x10) != 0;
            bool anotCharging = (data[7] & 0x20) != 0;

            int right = fromLeft ? anot : curr;
            int left = fromLeft ? curr : anot;
            bool rightCharging = fromLeft ? anotCharging : currCharging;
            bool leftCharging = fromLeft ? currCharging : anotCharging;
            bool rightOk = right <= 10;
            bool leftOk = left <= 10;

            if (rightOk && leftOk)
            {
                if (left <= right)
                {
                    percent = left * 10;
                    charging = leftCharging;
                }
                else
                {
                    percent = right * 10;
                    charging = rightCharging;
                }
            }
            else if (rightOk)
            {
                percent = right * 10;
                charging = rightCharging;
            }
            else if (leftOk)
            {
                percent = left * 10;
                charging = leftCharging;
            }
            else
            {
                return false;
            }

            return percent >= 0 && percent <= 100;
        }

        public static void Offer(byte[] data)
        {
            ushort model;
            int percent;
            bool charging;
            if (!TryParse(data, out model, out percent, out charging)) return;

            Sample sample = new Sample();
            sample.Percent = percent;
            sample.Charging = charging;
            sample.Ticks = DateTime.UtcNow.Ticks;
            lock (Gate)
            {
                ByModel[model] = sample;
            }
        }

        public static bool TryGetBattery(string hintName, string friendlyName, ushort modelHint, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            ushort model = modelHint;
            if (model == 0) model = InferModel(hintName);
            if (model == 0) model = InferModel(friendlyName);

            lock (Gate)
            {
                if (model != 0)
                {
                    return CopyFresh(model, out percent, out charging);
                }

                if (!LooksLikeAppleAudio(hintName) && !LooksLikeAppleAudio(friendlyName)) return false;
                if (ByModel.Count != 1) return false;
                foreach (ushort only in ByModel.Keys)
                {
                    return CopyFresh(only, out percent, out charging);
                }
            }
            return false;
        }

        public static void RememberCharging(string mac, bool charging)
        {
            string key = NormalizeMac(mac);
            if (key == null) return;
            lock (Gate)
            {
                ChargingByMac[key] = charging;
            }
        }

        public static bool GetCharging(string mac)
        {
            string key = NormalizeMac(mac);
            if (key == null) return false;
            lock (Gate)
            {
                bool value;
                if (ChargingByMac.TryGetValue(key, out value)) return value;
            }
            return false;
        }

        public static bool LooksLikeAppleAudio(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.IndexOf("beats") >= 0 || n.IndexOf("airpod") >= 0 || n.IndexOf("powerbeats") >= 0;
        }

        static bool CopyFresh(ushort model, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;
            Sample sample;
            if (!ByModel.TryGetValue(model, out sample) || sample == null) return false;
            if (DateTime.UtcNow.Ticks - sample.Ticks > FreshTicks) return false;
            percent = sample.Percent;
            charging = sample.Charging;
            return percent >= 0;
        }

        static ushort InferModel(string name)
        {
            if (!LooksLikeAppleAudio(name)) return 0;
            string n = name.ToLowerInvariant();
            bool buds = n.IndexOf("bud") >= 0;
            bool pro = n.IndexOf("pro") >= 0;
            if (n.IndexOf("studio") >= 0 && !buds && !pro) return 0x2009;
            if (n.IndexOf("solo") >= 0 && !pro) return 0x2006;
            if (n.IndexOf("powerbeats") >= 0 && !pro) return 0x2003;
            if (n.IndexOf("beats x") >= 0 || n.IndexOf("beatsx") >= 0) return 0x2005;
            if (n.IndexOf("fit") >= 0 && pro) return 0x2012;
            return 0;
        }

        static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            return mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        }
    }
}
