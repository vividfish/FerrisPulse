// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️
// 1. 目标平台与编译规范：严格维持 C# 5.0 语法标准与 .NET Framework 4.0 兼容性。严禁引入任何第三方包或依赖。
// 2. 编码规范：所有文件严格保存为 UTF-8 带签名（BOM）格式，防止中文字符乱码。
// 3. 架构规范：本文件属于 MainForm 的分部类 (partial class) 之一。修改时请保持职责清晰，任何新增方法或逻辑请同步维护 ARCHITECTURE.md。
// 模块描述: MainForm 任务栏托盘生命周期、右键上下文菜单、任务栏重启自愈与 GDI+ 矢量图标绘制
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RazerBatteryTray
{
    public partial class MainForm : Form
    {
        private void ConfigureSubMenuDropDown(ToolStripDropDown dropDown)
        {
            if (dropDown == null) return;
            dropDown.Renderer = contextMenu != null ? contextMenu.Renderer : new ModernDarkMenuRenderer();
            if (dropDown is ToolStripDropDownMenu)
            {
                ToolStripDropDownMenu menu = (ToolStripDropDownMenu)dropDown;
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = true;
            }
            dropDown.ItemClicked += (s, e) => {
                lastClickedTrayItem = e.ClickedItem;
            };
            dropDown.Closing += (s, e) => {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    if (ShouldKeepTrayMenuOpen(lastClickedTrayItem))
                    {
                        e.Cancel = true;
                    }
                }
            };
        }

        private void InitializeTray()
        {
            contextMenu = new ContextMenuStrip();
            contextMenu.Renderer = new ModernDarkMenuRenderer();
            contextMenu.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            contextMenu.ShowImageMargin = false;
            contextMenu.ShowCheckMargin = true;
            contextMenu.Padding = new Padding(3, 4, 3, 4);

            contextMenu.ItemClicked += (s, e) => {
                lastClickedTrayItem = e.ClickedItem;
            };
            contextMenu.Closing += (s, e) => {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                {
                    if (ShouldKeepTrayMenuOpen(lastClickedTrayItem))
                    {
                        e.Cancel = true;
                    }
                }
            };

            statusMenuItem = new ToolStripMenuItem("正在检测设备...");
            statusMenuItem.Tag = "Header";
            statusMenuItem.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            statusMenuItem.Click += (s, e) => ShowWindow();
            contextMenu.Items.Add(statusMenuItem);

            deviceMenuSep = new ToolStripSeparator();
            contextMenu.Items.Add(deviceMenuSep);

            showItem = new ToolStripMenuItem("打开控制面板", null, (s, e) => ShowWindow());
            contextMenu.Items.Add(showItem);

            refreshItem = new ToolStripMenuItem("立即刷新电量", null, (s, e) => RefreshBatteryStatus(true));
            contextMenu.Items.Add(refreshItem);

            // Submenu: DPI 调节
            dpiMenu = new ToolStripMenuItem("调节 DPI 档位");
            ConfigureSubMenuDropDown(dpiMenu.DropDown);
            for (int i = 0; i < dpiStageValues.Length; i++)
            {
                int val = dpiStageValues[i];
                var sub = new ToolStripMenuItem(val + " DPI", null, (s, e) => {
                    SetDpiFromUI(val);
                    foreach (ToolStripItem it in dpiMenu.DropDownItems)
                    {
                        var tmi = it as ToolStripMenuItem;
                        if (tmi != null && tmi.Tag is int)
                        {
                            tmi.Checked = ((int)tmi.Tag == val);
                        }
                    }
                });
                sub.Tag = val;
                dpiMenu.DropDownItems.Add(sub);
            }
            contextMenu.Items.Add(dpiMenu);

            // Submenu: 回报率调节
            rateMenu = new ToolStripMenuItem("调节回报率");
            ConfigureSubMenuDropDown(rateMenu.DropDown);
            for (int i = 0; i < pollingRateValues.Length; i++)
            {
                int val = pollingRateValues[i];
                var sub = new ToolStripMenuItem(val + " Hz", null, (s, e) => {
                    SetPollingRateFromUI(val);
                    foreach (ToolStripItem it in rateMenu.DropDownItems)
                    {
                        var tmi = it as ToolStripMenuItem;
                        if (tmi != null && tmi.Tag is int)
                        {
                            tmi.Checked = ((int)tmi.Tag == val);
                        }
                    }
                });
                sub.Tag = val;
                rateMenu.DropDownItems.Add(sub);
            }
            contextMenu.Items.Add(rateMenu);

            // Submenu: 全屏游戏自动回报率
            autoGameRateMenu = new ToolStripMenuItem("全屏游戏自动回报率");
            ConfigureSubMenuDropDown(autoGameRateMenu.DropDown);

            autoGameRateToggleItem = new ToolStripMenuItem("启用全屏自动切换", null, (s, e) => {
                SetAutoGameRateEnabled(!autoGameRateEnabled);
                autoGameRateToggleItem.Checked = autoGameRateEnabled;
            });
            autoGameRateToggleItem.Checked = autoGameRateEnabled;
            autoGameRateMenu.DropDownItems.Add(autoGameRateToggleItem);
            autoGameRateMenu.DropDownItems.Add(new ToolStripSeparator());

            var gameRateSub = new ToolStripMenuItem("全屏电竞回报率");
            ConfigureSubMenuDropDown(gameRateSub.DropDown);
            for (int i = 0; i < pollingRateValues.Length; i++)
            {
                int val = pollingRateValues[i];
                var sub = new ToolStripMenuItem(val + " Hz", null, (s, e) => {
                    SetGamePollingRateFromUI(val);
                    foreach (ToolStripItem it in gameRateSub.DropDownItems)
                    {
                        var tmi = it as ToolStripMenuItem;
                        if (tmi != null && tmi.Tag is int)
                        {
                            tmi.Checked = ((int)tmi.Tag == val);
                        }
                    }
                });
                sub.Tag = val;
                gameRateSub.DropDownItems.Add(sub);
            }
            autoGameRateMenu.DropDownItems.Add(gameRateSub);

            var notifySub = new ToolStripMenuItem("切换提醒方式");
            ConfigureSubMenuDropDown(notifySub.DropDown);
            var miOsd = new ToolStripMenuItem("游戏 OSD 浮窗", null, (s, e) => {
                gameRateNotifyOsd = !gameRateNotifyOsd;
                if (chkNotifyOsd != null) chkNotifyOsd.Checked = gameRateNotifyOsd;
                SaveConfig();
                ((ToolStripMenuItem)s).Checked = gameRateNotifyOsd;
            });
            miOsd.Checked = gameRateNotifyOsd;
            notifySub.DropDownItems.Add(miOsd);

            var miToast = new ToolStripMenuItem("Windows 系统通知", null, (s, e) => {
                gameRateNotifyToast = !gameRateNotifyToast;
                if (chkNotifyToast != null) chkNotifyToast.Checked = gameRateNotifyToast;
                SaveConfig();
                ((ToolStripMenuItem)s).Checked = gameRateNotifyToast;
            });
            miToast.Checked = gameRateNotifyToast;
            notifySub.DropDownItems.Add(miToast);

            var miSound = new ToolStripMenuItem("柔和提示音", null, (s, e) => {
                gameRateNotifySound = !gameRateNotifySound;
                if (chkNotifySound != null) chkNotifySound.Checked = gameRateNotifySound;
                SaveConfig();
                ((ToolStripMenuItem)s).Checked = gameRateNotifySound;
            });
            miSound.Checked = gameRateNotifySound;
            notifySub.DropDownItems.Add(miSound);

            autoGameRateMenu.DropDownItems.Add(notifySub);
            contextMenu.Items.Add(autoGameRateMenu);

            nuphySleepMenuItem = new ToolStripMenuItem("自动休眠: 已开启");
            nuphySleepMenuItem.Click += (s, e) => {
                var cur = NuphyDeviceHelper.CachedHardwareSettings ?? new NuphyDeviceHelper.NuphyHardwareSettings();
                bool newEn = !cur.SleepEnabled;
                NuphyDeviceHelper.SetAutoSleep(newEn);
                ApplyNuphySettingsToUI();
                UpdateTrayDeviceMenuItems();
            };
            nuphySleepMenuItem.Visible = false;
            contextMenu.Items.Add(nuphySleepMenuItem);

            nuphyWinLockMenuItem = new ToolStripMenuItem("Win键锁定: 已关闭");
            nuphyWinLockMenuItem.Click += (s, e) => {
                var cur = NuphyDeviceHelper.CachedHardwareSettings ?? new NuphyDeviceHelper.NuphyHardwareSettings();
                bool newLock = !cur.WinLock;
                NuphyDeviceHelper.SetWinLock(newLock);
                ApplyNuphySettingsToUI();
                UpdateTrayDeviceMenuItems();
            };
            nuphyWinLockMenuItem.Visible = false;
            contextMenu.Items.Add(nuphyWinLockMenuItem);

            nuphyBacklightMenu = new ToolStripMenuItem("键盘背光调节");
            ConfigureSubMenuDropDown(nuphyBacklightMenu.DropDown);
            int[] brightPresets = new int[] { 0, 25, 50, 75, 100 };
            for (int i = 0; i < brightPresets.Length; i++)
            {
                int bVal = brightPresets[i];
                var sub = new ToolStripMenuItem(bVal == 0 ? "关闭背光 (0%)" : (bVal + "% 亮度"));
                sub.Click += (s, e) => {
                    NuphyDeviceHelper.SetBacklightBrightness(bVal);
                    ApplyNuphySettingsToUI();
                    UpdateTrayDeviceMenuItems();
                };
                sub.Tag = bVal;
                nuphyBacklightMenu.DropDownItems.Add(sub);
            }
            nuphyBacklightMenu.Visible = false;
            contextMenu.Items.Add(nuphyBacklightMenu);

            nuphySidelightMenuItem = new ToolStripMenuItem("前置灯带: 已开启");
            nuphySidelightMenuItem.Click += (s, e) => {
                var cur = NuphyDeviceHelper.CachedHardwareSettings ?? new NuphyDeviceHelper.NuphyHardwareSettings();
                bool newSide = !(cur.SidelightEnabled && cur.SidelightBrightness > 0);
                NuphyDeviceHelper.SetSidelightEnabled(newSide);
                ApplyNuphySettingsToUI();
                UpdateTrayDeviceMenuItems();
            };
            nuphySidelightMenuItem.Visible = false;
            contextMenu.Items.Add(nuphySidelightMenuItem);

            nuphyTraySep = new ToolStripSeparator();
            nuphyTraySep.Visible = false;
            contextMenu.Items.Add(nuphyTraySep);

            var intervalMenu = new ToolStripMenuItem("自动刷新频率");
            ConfigureSubMenuDropDown(intervalMenu.DropDown);
            int30sMenuItem = new ToolStripMenuItem("30 秒", null, (s, e) => {
                SetInterval(30000, true);
            });
            int1mMenuItem = new ToolStripMenuItem("1 分钟", null, (s, e) => {
                SetInterval(60000, true);
            });
            int5mMenuItem = new ToolStripMenuItem("5 分钟", null, (s, e) => {
                SetInterval(300000, true);
            });
            int10mMenuItem = new ToolStripMenuItem("10 分钟", null, (s, e) => {
                SetInterval(600000, true);
            });
            intervalMenu.DropDownItems.AddRange(new ToolStripItem[] { int30sMenuItem, int1mMenuItem, int5mMenuItem, int10mMenuItem });
            contextMenu.Items.Add(intervalMenu);

            contextMenu.Items.Add(new ToolStripSeparator());

            batteryFloatMenuItem = new ToolStripMenuItem("电量悬浮图标", null, (s, e) => {
                SetBatteryFloatEnabled(!batteryFloatEnabled);
            });
            batteryFloatMenuItem.Checked = batteryFloatEnabled;
            contextMenu.Items.Add(batteryFloatMenuItem);

            lowBatteryAlertMenuItem = new ToolStripMenuItem("低电量气泡通知 (≤20%)", null, (s, e) => {
                SetLowBatteryAlert(!lowBatteryAlertEnabled, true);
                lowBatteryAlertMenuItem.Checked = lowBatteryAlertEnabled;
            });
            contextMenu.Items.Add(lowBatteryAlertMenuItem);

            autoStartMenuItem = new ToolStripMenuItem("开机自动启动", null, (s, e) => {
                SetAutoStart(!IsAutoStartEnabled(), true);
                autoStartMenuItem.Checked = IsAutoStartEnabled();
            });
            contextMenu.Items.Add(autoStartMenuItem);

            autoStartShowUIMenuItem = new ToolStripMenuItem("开机弹出主窗口", null, (s, e) => {
                SetAutoStartShowUI(!autoStartShowMainWindow, true);
                autoStartShowUIMenuItem.Checked = autoStartShowMainWindow;
            });
            contextMenu.Items.Add(new ToolStripSeparator());

            prioMenuItem = new ToolStripMenuItem("设备优先级管理 (拖拽排序)...", null, (s, e) => OpenPriorityManager());
            contextMenu.Items.Add(prioMenuItem);

            addDevMenuItem = new ToolStripMenuItem("手动添加新设备向导...", null, (s, e) => OpenAddDeviceWizard());
            contextMenu.Items.Add(addDevMenuItem);

            createDesktopLnkItem = new ToolStripMenuItem("创建桌面快捷方式", null, (s, e) => {
                bool ok = StartupManager.CreateDesktopShortcut(true);
                if (ok && trayIcon != null)
                {
                    try { trayIcon.ShowBalloonTip(3000, "桌面快捷方式已创建", "已成功在桌面为您生成 FerrisPulse · 灵脉 快捷方式！", ToolTipIcon.Info); } catch { }
                }
            });
            contextMenu.Items.Add(createDesktopLnkItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            aboutMenuItem = new ToolStripMenuItem("关于 FerrisPulse", null, (s, e) => OpenAboutForm());
            contextMenu.Items.Add(aboutMenuItem);

            checkUpdateMenuItem = new ToolStripMenuItem("检查更新", null, (s, e) => OnTrayUpdateMenuItemClicked());
            contextMenu.Items.Add(checkUpdateMenuItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            exitItem = new ToolStripMenuItem("退出程序", null, (s, e) => ExitApp());
            contextMenu.Items.Add(exitItem);

            foreach (ToolStripItem item in contextMenu.Items)
            {
                item.Padding = new Padding(6, 4, 12, 4);
            }

            contextMenu.Opening += (s, e) => UpdateTrayDeviceMenuItems();

            UpdateManager.OnPendingUpdateChanged -= OnPendingUpdateChangedHandler;
            UpdateManager.OnPendingUpdateChanged += OnPendingUpdateChangedHandler;
            UpdateTrayUpdateMenuItem();

            trayIcon = new NotifyIcon();
            trayIcon.ContextMenuStrip = contextMenu;
            trayIcon.Text = "FerrisPulse · 灵脉 (正在检测外设...)";
            if (this.Icon != null)
            {
                trayIcon.Icon = this.Icon;
            }
            else
            {
                UpdateTrayIcon(-1, false, false);
            }
            trayIcon.Visible = true;

            trayIcon.Click += (s, e) => {
                var me = e as MouseEventArgs;
                if (me != null && me.Button == MouseButtons.Left)
                {
                    ToggleWindow();
                }
            };
            trayIcon.DoubleClick += (s, e) => ShowWindow();
        }


        /// <summary>
        /// 当 Windows 任务栏重建 (WM_TASKBARCREATED) 或 Explorer 重启时，重构托盘图标，防止托盘图标丢失
        /// </summary>
        public void ReinitializeTrayIcon()
        {
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                InitializeTray();
                if (lastInfo != null)
                {
                    UpdateTrayIcon(lastInfo.BatteryPercent, lastInfo.IsCharging, lastInfo.IsConnected, lastInfo.IsSleeping, lastInfo.BrandColor);
                }
            }
            catch { }
        }
        private void UpdateTrayDeviceMenuItems()
        {
            if (contextMenu == null) return;

            foreach (var item in dynamicDeviceMenuItems)
            {
                if (contextMenu.Items.Contains(item))
                {
                    contextMenu.Items.Remove(item);
                }
            }
            dynamicDeviceMenuItems.Clear();

            var primary = GetPrimaryDevice();
            var onlineDevices = new List<MouseBatteryInfo>();
            if (activeDevices != null)
            {
                for (int i = 0; i < activeDevices.Count; i++)
                {
                    if (activeDevices[i].IsConnected && !DeviceManager.IsDeviceHidden(activeDevices[i].DeviceId))
                    {
                        onlineDevices.Add(activeDevices[i]);
                    }
                }
            }

            if (onlineDevices.Count == 0)
            {
                if (statusMenuItem != null)
                {
                    statusMenuItem.Visible = true;
                    statusMenuItem.Text = "暂无在线设备";
                    statusMenuItem.ForeColor = Color.FromArgb(145, 155, 175);
                }
            }
            else
            {
                if (statusMenuItem != null)
                {
                    statusMenuItem.Visible = false;
                }

                int insertIdx = contextMenu.Items.IndexOf(deviceMenuSep);
                if (insertIdx < 0) insertIdx = 0;

                for (int i = 0; i < onlineDevices.Count; i++)
                {
                    var dev = onlineDevices[i];
                    string battStr;
                    if (dev.Brand == "NuPhy" || dev.Category == DeviceCategory.Keyboard)
                    {
                        bool isWired = (dev.Transport != null && (dev.Transport.IndexOf("有线", StringComparison.OrdinalIgnoreCase) >= 0 || dev.Transport.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0) && !dev.Transport.Contains("无线")) ||
                                       (dev.DeviceId != null && dev.DeviceId.IndexOf("pid_a011", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (isWired)
                        {
                            battStr = "供电中";
                        }
                        else if (dev.IsCharging || (dev.Brand == "NuPhy" && dev.BatteryPercent >= 100))
                        {
                            battStr = "充电中";
                        }
                        else
                        {
                            battStr = dev.BatteryPercent > 0 ? (dev.BatteryPercent.ToString() + "%") : "--";
                        }
                    }
                    else
                    {
                        if (dev.IsCharging)
                        {
                            battStr = string.Format("{0}% (充电中)", dev.BatteryPercent > 0 ? dev.BatteryPercent : 0);
                        }
                        else if (dev.IsSleeping)
                        {
                            battStr = string.Format("{0}% (休眠)", dev.BatteryPercent > 0 ? dev.BatteryPercent : 0);
                        }
                        else
                        {
                            battStr = dev.BatteryPercent > 0 ? (dev.BatteryPercent.ToString() + "%") : "--";
                        }
                    }

                    string itemText = dev.DisplayName + "\t" + battStr;
                    var mi = new ToolStripMenuItem(itemText);
                    mi.Checked = false;
                    mi.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
                    mi.ForeColor = dev.BrandColor;
                    mi.Padding = new Padding(6, 4, 12, 4);

                    var targetDev = dev;
                    mi.Click += (s, e) => {
                        ShowWindow(targetDev);
                    };

                    contextMenu.Items.Insert(insertIdx + i, mi);
                    dynamicDeviceMenuItems.Add(mi);
                }
            }

            // Context-sensitive controls based on primary tray device
            if (primary != null && primary.IsConnected)
            {
                if (primary.Brand == "NuPhy" || primary.Category == DeviceCategory.Keyboard)
                {
                    // Primary is NuPhy keyboard: Show NuPhy controls, hide Mouse DPI/Rate
                    if (dpiMenu != null) dpiMenu.Visible = false;
                    if (rateMenu != null) rateMenu.Visible = false;

                    var curSettings = NuphyDeviceHelper.CachedHardwareSettings ?? new NuphyDeviceHelper.NuphyHardwareSettings();

                    if (nuphySleepMenuItem != null)
                    {
                        nuphySleepMenuItem.Visible = true;
                        nuphySleepMenuItem.Text = curSettings.SleepEnabled ? "自动休眠: 已开启" : "自动休眠: 已关闭 (常亮)";
                    }
                    if (nuphyWinLockMenuItem != null)
                    {
                        nuphyWinLockMenuItem.Visible = true;
                        nuphyWinLockMenuItem.Text = curSettings.WinLock ? "Win键锁定: 已开启" : "Win键锁定: 已关闭";
                    }
                    if (nuphyBacklightMenu != null)
                    {
                        nuphyBacklightMenu.Visible = true;
                        ConfigureSubMenuDropDown(nuphyBacklightMenu.DropDown);
                        for (int k = 0; k < nuphyBacklightMenu.DropDownItems.Count; k++)
                        {
                            var sub = nuphyBacklightMenu.DropDownItems[k] as ToolStripMenuItem;
                            if (sub != null && sub.Tag is int)
                            {
                                sub.Checked = ((int)sub.Tag == curSettings.BacklightBrightness);
                            }
                        }
                    }
                    if (nuphySidelightMenuItem != null)
                    {
                        nuphySidelightMenuItem.Visible = true;
                        nuphySidelightMenuItem.Text = (curSettings.SidelightEnabled && curSettings.SidelightBrightness > 0) ? "前置灯带: 已开启" : "前置灯带: 已关闭";
                    }
                    if (nuphyTraySep != null) nuphyTraySep.Visible = true;
                }
                else if (primary.Category == DeviceCategory.Mouse)
                {
                    // Primary is Mouse (Razer, Rapoo, etc.): Show DPI/Rate, hide NuPhy controls
                    if (nuphySleepMenuItem != null) nuphySleepMenuItem.Visible = false;
                    if (nuphyWinLockMenuItem != null) nuphyWinLockMenuItem.Visible = false;
                    if (nuphyBacklightMenu != null) nuphyBacklightMenu.Visible = false;
                    if (nuphySidelightMenuItem != null) nuphySidelightMenuItem.Visible = false;
                    if (nuphyTraySep != null) nuphyTraySep.Visible = false;

                    if (dpiMenu != null)
                    {
                        dpiMenu.Visible = true;
                        dpiMenu.DropDownItems.Clear();
                        ConfigureSubMenuDropDown(dpiMenu.DropDown);
                        if (dpiStageValues != null)
                        {
                            for (int i = 0; i < dpiStageValues.Length; i++)
                            {
                                int val = dpiStageValues[i];
                                var sub = new ToolStripMenuItem(val + " DPI", null, (s, e) => {
                                    SetDpiFromUI(val);
                                    foreach (ToolStripItem it in dpiMenu.DropDownItems)
                                    {
                                        var tmi = it as ToolStripMenuItem;
                                        if (tmi != null && tmi.Tag is int)
                                        {
                                            tmi.Checked = ((int)tmi.Tag == val);
                                        }
                                    }
                                });
                                sub.Tag = val;
                                sub.Checked = (primary.Dpi == val);
                                dpiMenu.DropDownItems.Add(sub);
                            }
                        }
                    }

                    if (rateMenu != null)
                    {
                        rateMenu.Visible = true;
                        ConfigureSubMenuDropDown(rateMenu.DropDown);
                        for (int k = 0; k < rateMenu.DropDownItems.Count; k++)
                        {
                            var sub = rateMenu.DropDownItems[k] as ToolStripMenuItem;
                            if (sub != null && sub.Tag is int)
                            {
                                sub.Checked = ((int)sub.Tag == primary.PollingRate);
                            }
                        }
                    }
                }
                else
                {
                    // Other device (Headset, Gamepad)
                    if (dpiMenu != null) dpiMenu.Visible = false;
                    if (rateMenu != null) rateMenu.Visible = false;
                    if (nuphySleepMenuItem != null) nuphySleepMenuItem.Visible = false;
                    if (nuphyWinLockMenuItem != null) nuphyWinLockMenuItem.Visible = false;
                    if (nuphyBacklightMenu != null) nuphyBacklightMenu.Visible = false;
                    if (nuphySidelightMenuItem != null) nuphySidelightMenuItem.Visible = false;
                    if (nuphyTraySep != null) nuphyTraySep.Visible = false;
                }
            }
            else
            {
                // No connected primary device
                if (dpiMenu != null) dpiMenu.Visible = false;
                if (rateMenu != null) rateMenu.Visible = false;
                if (nuphySleepMenuItem != null) nuphySleepMenuItem.Visible = false;
                if (nuphyWinLockMenuItem != null) nuphyWinLockMenuItem.Visible = false;
                if (nuphyBacklightMenu != null) nuphyBacklightMenu.Visible = false;
                if (nuphySidelightMenuItem != null) nuphySidelightMenuItem.Visible = false;
                if (nuphyTraySep != null) nuphyTraySep.Visible = false;
            }
        }


        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSMICON = 49;
        private int GetTrayIconSize()
        {
            try
            {
                int size = GetSystemMetrics(SM_CXSMICON);
                if (size < 16) size = 16;
                if (size > 64) size = 64;
                return size;
            }
            catch
            {
                return 16;
            }
        }

        private void UpdateTrayIcon(int percent, bool isCharging, bool isConnected, bool isSleeping = false, Color? brandColor = null)
        {
            int iconSize = GetTrayIconSize();
            Color bCol = brandColor ?? ((lastInfo != null) ? lastInfo.BrandColor : Color.FromArgb(0, 230, 118));
            using (Bitmap bmp = DrawTrayBitmap(percent, isCharging, isConnected, iconSize, isSleeping, bCol))
            {
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon tempIcon = Icon.FromHandle(hIcon))
                    {
                        trayIcon.Icon = (Icon)tempIcon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private static Bitmap DrawTrayBitmap(int percent, bool isCharging, bool isConnected, int iconSize, bool isSleeping = false, Color? brandColor = null)
        {
            if (iconSize < 16) iconSize = 16;
            if (iconSize > 64) iconSize = 64;
            int capW = iconSize >= 32 ? 4 : (iconSize >= 20 ? 3 : 2);
            int bodyRight = iconSize - capW - 1;
            int innerW = bodyRight - 1;
            int innerH = iconSize - 2;
            int capH = (iconSize * 2) / 5;
            if (capH < 4) capH = 4;
            if (capH > iconSize - 4) capH = iconSize - 4;
            int capY = (iconSize - capH) / 2;

            Bitmap bmp = new Bitmap(iconSize, iconSize);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Color outline = isConnected ? Color.FromArgb(8, 12, 18) : Color.FromArgb(110, 116, 128);
                if (isConnected && percent >= 0 && innerW > 0 && innerH > 0)
                {
                    Color brandAccent = brandColor.HasValue ? brandColor.Value : Color.FromArgb(0, 230, 118);
                    Color accentColor = isSleeping ? Color.FromArgb(255, 183, 77) :
                                        ((percent > 20 || isCharging) ? brandAccent :
                                        ((percent > 10) ? Color.FromArgb(255, 214, 0) : Color.FromArgb(255, 50, 65)));
                    using (SolidBrush empty = new SolidBrush(Color.FromArgb(78, 84, 96)))
                        g.FillRectangle(empty, 1, 1, innerW, innerH);
                    int fillW = innerW * percent / 100;
                    if (percent >= 100) fillW = innerW;
                    else if (percent > 0 && fillW < 1) fillW = 1;
                    else if (percent < 100 && fillW >= innerW) fillW = innerW - 1;
                    if (fillW > 0)
                    {
                        using (SolidBrush fb = new SolidBrush(accentColor))
                            g.FillRectangle(fb, 1, 1, fillW, innerH);
                    }
                }
                using (SolidBrush edge = new SolidBrush(outline))
                {
                    g.FillRectangle(edge, 0, 0, bodyRight + 1, 1);
                    g.FillRectangle(edge, 0, iconSize - 1, bodyRight + 1, 1);
                    g.FillRectangle(edge, 0, 0, 1, iconSize);
                    g.FillRectangle(edge, bodyRight, 0, 1, iconSize);
                    g.FillRectangle(edge, bodyRight + 1, capY, capW, capH);
                }
            }
            return bmp;
        }

        private void OnPendingUpdateChangedHandler()
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke((MethodInvoker)UpdateTrayUpdateMenuItem);
                return;
            }
            UpdateTrayUpdateMenuItem();
        }

        private void UpdateTrayUpdateMenuItem()
        {
            if (this.IsDisposed || checkUpdateMenuItem == null) return;
            var pending = UpdateManager.PendingUpdate;
            if (pending != null && !string.IsNullOrEmpty(pending.LatestVersion) && UpdateManager.IsNewerVersion(pending.LatestVersion, UpdateManager.CurrentVersion))
            {
                string cleanVer = pending.LatestVersion.Trim().TrimStart('v', 'V');
                int sp = cleanVer.IndexOf(' ');
                if (sp > 0) cleanVer = cleanVer.Substring(0, sp);
                checkUpdateMenuItem.Text = "查看新版本 (v" + cleanVer + ")";
            }
            else
            {
                checkUpdateMenuItem.Text = "检查更新";
            }
        }

        private void OnTrayUpdateMenuItemClicked()
        {
            var pending = UpdateManager.PendingUpdate;
            if (pending != null && UpdateManager.IsNewerVersion(pending.LatestVersion, UpdateManager.CurrentVersion))
            {
                // 0ms 秒开：直接从内存缓存打开更新弹窗，弹窗内 SWR 后台静默并发复检
                UpdateNoticeForm.ShowUpdateDialog(this, pending, UpdateManager.CurrentVersion);
            }
            else
            {
                TriggerManualUpdateCheck();
            }
        }

        private void ExitApp()
        {
            UpdateManager.OnPendingUpdateChanged -= OnPendingUpdateChangedHandler;
            StopDpiMonitor();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            if (osdForm != null && !osdForm.IsDisposed)
            {
                osdForm.Dispose();
            }
            if (batteryFloatForm != null && !batteryFloatForm.IsDisposed)
            {
                batteryFloatForm.HideFloat();
                batteryFloatForm.Dispose();
            }
            Application.Exit();
        }

    }
}
