using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Batery")]
[assembly: System.Reflection.AssemblyDescription("Monitor de bateria de dispositivos sem fio")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace Batery
{
    internal static class Program
    {
        private const string InstanceName = @"Local\Batery.9E3A30DF";
        internal static readonly int ShowMessage = RegisterWindowMessage("Batery.Show.9E3A30DF");

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--diagnostico")
                return WriteDiagnostics(args[1]);

            bool background = args.Contains("--startup");
            bool created;
            using (var instance = new Mutex(true, InstanceName, out created))
            {
                if (!created)
                {
                    if (!background && ShowMessage != 0)
                        PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new BatteryApplication(background))
                    Application.Run(app);
                instance.ReleaseMutex();
            }
            return 0;
        }

        private static int WriteDiagnostics(string path)
        {
            try
            {
                var scan = Task.Run(() => Scanner.ReadAsync()).GetAwaiter().GetResult();
                var lines = new List<string> { "Bluetooth: " + (scan.BluetoothFailed ? "falha" : "OK") };
                lines.AddRange(scan.Devices.Select(d => d.Name + " | " + d.Connection + " | " + d.Level));
                File.WriteAllLines(path, lines, Encoding.UTF8);
                return scan.BluetoothFailed ? 1 : 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(path, error.ToString(), Encoding.UTF8);
                return 1;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class BatteryApplication : ApplicationContext
    {
        private readonly Icon icon;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem refreshItem;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly System.Windows.Forms.Timer changeTimer;
        private readonly DeviceEvents events;
        private readonly HashSet<string> alerted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private BatteryWindow window;
        private List<Device> devices = new List<Device>();
        private DateTime? updatedAt;
        private bool bluetoothFailed;
        private bool refreshing;
        private bool stopped;

        public BatteryApplication(bool background)
        {
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            refreshTimer = new System.Windows.Forms.Timer();
            changeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            refreshTimer.Tick += (sender, args) => BeginRefresh();
            changeTimer.Tick += (sender, args) =>
            {
                changeTimer.Stop();
                BeginRefresh();
            };
            events = new DeviceEvents(Open, HandleHardwareChange);
            menu = new ContextMenuStrip();
            menu.Items.Add("Abrir Batery", null, (sender, args) => Open());
            refreshItem = new ToolStripMenuItem("Atualizar agora", null, (sender, args) => BeginRefresh());
            startupItem = new ToolStripMenuItem("Iniciar com o Windows", null,
                (sender, args) => SetStartup(!Startup.IsEnabled()));
            menu.Items.Add(refreshItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (sender, args) => ExitThread());
            menu.Opening += (sender, args) => startupItem.Checked = Startup.IsEnabled();

            tray = new NotifyIcon { ContextMenuStrip = menu, Icon = icon, Text = "Batery", Visible = true };
            tray.MouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left)
                    Open();
            };
            tray.BalloonTipClicked += (sender, args) => Open();

            refreshTimer.Interval = background ? 12000 : 100;
            refreshTimer.Start();
            if (!background)
                Open();
        }

        private void Open()
        {
            if (window == null || window.IsDisposed)
                window = new BatteryWindow(icon, BeginRefresh, SetStartup);
            window.Render(devices, updatedAt, refreshing, bluetoothFailed);
            window.SetStartup(Startup.IsEnabled());
            if (window.WindowState == FormWindowState.Minimized)
                window.WindowState = FormWindowState.Normal;
            window.Show();
            window.Activate();
            if (!updatedAt.HasValue && !refreshing)
                BeginRefresh();
        }

        private void HandleHardwareChange()
        {
            if (refreshing || !updatedAt.HasValue)
                return;
            changeTimer.Stop();
            changeTimer.Start();
        }

        private async void BeginRefresh()
        {
            if (refreshing || stopped)
                return;

            refreshTimer.Stop();
            changeTimer.Stop();
            refreshing = true;
            refreshItem.Enabled = false;
            RenderWindow();

            try
            {
                DeviceScan scan = await Task.Run(() => Scanner.ReadAsync());
                if (stopped)
                    return;
                devices = scan.Devices.OrderByDescending(d => d.IsLow)
                    .ThenBy(d => !d.HasReading)
                    .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                bluetoothFailed = scan.BluetoothFailed;
                updatedAt = DateTime.Now;
                UpdateTray();
            }
            catch (Exception)
            {
                if (!stopped)
                {
                    bluetoothFailed = true;
                    tray.Text = "Batery · Falha na leitura";
                }
            }
            finally
            {
                if (!stopped)
                {
                    refreshing = false;
                    refreshItem.Enabled = true;
                    RenderWindow();
                    refreshTimer.Interval = 60000;
                    refreshTimer.Start();
                }
            }
        }

        private void UpdateTray()
        {
            var lowest = devices.Where(d => d.HasReading)
                .OrderByDescending(d => d.IsLow).ThenBy(d => d.Percent ?? 101).FirstOrDefault();
            tray.Text = lowest == null ? "Batery · Sem leitura de bateria" :
                "Batery · " + BatteryWindow.Fit(lowest.Name, 35) + " · " + lowest.Level;

            var newAlerts = new List<string>();
            foreach (Device device in devices)
            {
                if (device.IsLow && alerted.Add(device.Id))
                    newAlerts.Add(device.Name + " (" + device.Level + ")");
                else if (device.CanRepeatAlert)
                    alerted.Remove(device.Id);
            }
            if (newAlerts.Count > 0)
                tray.ShowBalloonTip(5000, "Bateria baixa", string.Join("\n", newAlerts), ToolTipIcon.Warning);
        }

        private void RenderWindow()
        {
            if (window != null && !window.IsDisposed && window.Visible)
                window.Render(devices, updatedAt, refreshing, bluetoothFailed);
        }

        private void SetStartup(bool enabled)
        {
            try
            {
                Startup.Set(enabled);
            }
            catch (Exception ex)
            {
                if (!(ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                    ex is IOException))
                    throw;
                MessageBox.Show("O Windows recusou a alteração de inicialização.", "Batery",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (window != null && !window.IsDisposed)
                window.SetStartup(Startup.IsEnabled());
        }

        protected override void ExitThreadCore()
        {
            stopped = true;
            if (window != null)
                window.Dispose();
            changeTimer.Dispose();
            refreshTimer.Dispose();
            events.DestroyHandle();
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            if (icon != null)
                icon.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class Startup
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Batery";

        private static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\" --startup"; }
        }

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunPath))
                return key != null && string.Equals(key.GetValue(ValueName) as string, Command,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (key == null)
                    throw new IOException();
                if (enabled)
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                else
                    key.DeleteValue(ValueName, false);
            }
        }
    }

    internal sealed class DeviceEvents : NativeWindow
    {
        private readonly Action show;
        private readonly Action hardwareChanged;

        public DeviceEvents(Action show, Action hardwareChanged)
        {
            this.show = show;
            this.hardwareChanged = hardwareChanged;
            CreateHandle(new CreateParams { Caption = "Batery" });
        }

        protected override void WndProc(ref Message message)
        {
            if (Program.ShowMessage != 0 && message.Msg == Program.ShowMessage)
                show();
            if ((message.Msg == 0x0219 && message.WParam.ToInt64() == 7) ||
                (message.Msg == 0x0218 && message.WParam.ToInt64() == 18))
                hardwareChanged();
            base.WndProc(ref message);
        }
    }
}
