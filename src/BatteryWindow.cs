using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Batery
{
    internal sealed class BatteryWindow : Form
    {
        private static readonly Color Background = Color.FromArgb(18, 22, 21);
        private static readonly Color Surface = Color.FromArgb(29, 35, 32);
        private static readonly Color TextColor = Color.FromArgb(240, 245, 240);
        private static readonly Color Muted = Color.FromArgb(172, 186, 177);
        private static readonly Color Accent = Color.FromArgb(187, 244, 118);
        private static readonly Color Warning = Color.FromArgb(255, 183, 113);
        private static readonly Color Border = Color.FromArgb(52, 65, 57);
        private readonly Font headingFont = new Font("Segoe UI", 23, FontStyle.Bold);
        private readonly Font strongFont = new Font("Segoe UI", 10, FontStyle.Bold);
        private readonly Font bodyFont = new Font("Segoe UI", 9);
        private readonly Font smallFont = new Font("Segoe UI", 8.5f);
        private readonly Label count;
        private readonly Label status;
        private readonly Button refresh;
        private readonly CheckBox startup;
        private readonly Panel deviceList;
        private readonly ToolTip labels = new ToolTip();
        private bool updatingStartup;
        private string lastContent;

        public BatteryWindow(Icon icon, Action refreshAction, Action<bool> startupAction)
        {
            SuspendLayout();
            Text = "Batery";
            Icon = icon;
            Font = bodyFont;
            ClientSize = new Size(520, 461);
            MinimumSize = new Size(506, 376);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            BackColor = Background;
            ForeColor = TextColor;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(22, 17, 22, 10),
                ColumnCount = 1,
                RowCount = 4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            Controls.Add(layout);

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 121));
            layout.Controls.Add(header, 0, 0);
            var title = Label("Bateria sem fio", headingFont, TextColor);
            title.TextAlign = ContentAlignment.MiddleLeft;
            header.Controls.Add(title, 0, 0);
            refresh = new Button
            {
                Text = "Atualizar", Dock = DockStyle.Top, Height = 34,
                Margin = new Padding(0, 17, 0, 0), BackColor = Surface,
                ForeColor = Accent, FlatStyle = FlatStyle.Flat, Font = strongFont
            };
            refresh.FlatAppearance.BorderColor = Border;
            refresh.FlatAppearance.MouseOverBackColor = Border;
            refresh.Click += (sender, args) => refreshAction();
            header.Controls.Add(refresh, 1, 0);

            count = Label("Dispositivos", strongFont, Muted);
            count.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(count, 0, 1);
            deviceList = new Panel
            {
                Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty,
                AccessibleName = "Bateria dos dispositivos"
            };
            layout.Controls.Add(deviceList, 0, 2);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0),
                ColumnCount = 1, RowCount = 2
            };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            layout.Controls.Add(footer, 0, 3);
            startup = new CheckBox
            {
                Text = "Iniciar com o Windows", ForeColor = TextColor, Dock = DockStyle.Fill,
                Margin = Padding.Empty, Font = bodyFont
            };
            startup.CheckedChanged += (sender, args) =>
            {
                if (!updatingStartup)
                    startupAction(startup.Checked);
            };
            footer.Controls.Add(startup, 0, 0);
            status = Label("Procurando dispositivos…", smallFont, Muted);
            status.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(status, 0, 1);
            ResumeLayout(true);
        }

        public void SetStartup(bool enabled)
        {
            updatingStartup = true;
            startup.Checked = enabled;
            updatingStartup = false;
        }

        public void Render(List<Device> devices, DateTime? updatedAt, bool refreshing, bool bluetoothFailed)
        {
            refresh.Enabled = !refreshing;
            status.ForeColor = bluetoothFailed && !refreshing ? Warning : Muted;
            if (refreshing)
                status.Text = "Atualizando…";
            else if (bluetoothFailed)
                status.Text = "Leitura Bluetooth indisponível. Tente atualizar.";
            else if (updatedAt.HasValue)
                status.Text = "Atualizado às " + updatedAt.Value.ToString("HH:mm") + "  ·  A cada minuto";
            else
                status.Text = "Procurando dispositivos…";

            count.Text = devices.Count == 1 ? "1 dispositivo detectado" :
                devices.Count + " dispositivos detectados";
            if (!updatedAt.HasValue && !bluetoothFailed)
                count.Text = "Dispositivos";
            string emptyHeading = bluetoothFailed ? "Consulta indisponível" :
                updatedAt.HasValue ? "Nenhum dispositivo detectado" : "Procurando dispositivos…";
            string content = devices.Count == 0 ? emptyHeading : string.Join("\n", devices.Select(
                d => d.Id + "|" + d.Name + "|" + d.Connection + "|" + d.Level + "|" + d.Detail));
            if (content == lastContent)
                return;
            lastContent = content;

            deviceList.SuspendLayout();
            labels.RemoveAll();
            while (deviceList.Controls.Count > 0)
                deviceList.Controls[0].Dispose();
            if (devices.Count == 0)
            {
                string message = bluetoothFailed ? "Confira se o Bluetooth está ligado." :
                    "Conecte um dispositivo Bluetooth ou um receptor USB compatível.";
                deviceList.Controls.Add(Card(emptyHeading, message, null, true));
            }
            else
            {
                for (int i = devices.Count - 1; i >= 0; i--)
                {
                    var device = devices[i];
                    deviceList.Controls.Add(Card(device.Name, device.Connection, device, false));
                }
            }
            deviceList.ResumeLayout(true);
        }

        private Control Card(string name, string description, Device device, bool empty)
        {
            int unit = Math.Max(1, (int)Math.Round(CurrentAutoScaleDimensions.Width));
            int height = device != null && device.Detail != null ? 91 : 75;
            var wrapper = new Panel { Dock = DockStyle.Top, Height = height * unit / 96 };
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty,
                Padding = new Padding(14 * unit / 96, 7 * unit / 96, 14 * unit / 96, 6 * unit / 96),
                ColumnCount = empty ? 1 : 2, RowCount = height == 91 ? 3 : 2
            };
            wrapper.Padding = new Padding(0, 0, 0, 8 * unit / 96);
            wrapper.Controls.Add(card);
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            if (!empty)
                card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120 * unit / 96));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 25 * unit / 96));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 20 * unit / 96));
            var nameLabel = Label(name, strongFont, TextColor);
            nameLabel.AutoEllipsis = true;
            nameLabel.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(nameLabel, 0, 0);
            var descriptionLabel = Label(description, smallFont, Muted);
            descriptionLabel.AutoEllipsis = true;
            descriptionLabel.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(descriptionLabel, 0, 1);
            if (empty)
                return wrapper;

            labels.SetToolTip(nameLabel, name);
            Color color = device.IsLow ? Warning : device.HasReading ? Accent : Muted;
            var level = Label(device.Level, strongFont, color);
            level.TextAlign = ContentAlignment.MiddleRight;
            card.Controls.Add(level, 1, 0);
            if (device.Percent.HasValue)
                card.Controls.Add(new Meter(device.Percent.Value, color), 1, 1);
            if (device.Detail != null)
            {
                var detail = Label(device.Detail, smallFont, Muted);
                detail.AutoEllipsis = true;
                card.Controls.Add(detail, 0, 2);
                card.SetColumnSpan(detail, 2);
            }
            return wrapper;
        }

        private static Label Label(string text, Font font, Color color)
        {
            return new Label
            {
                Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill,
                Margin = Padding.Empty, UseMnemonic = false
            };
        }

        internal static string Fit(string value, int length)
        {
            if (value.Length <= length)
                return value;
            int end = length - 1;
            if (char.IsHighSurrogate(value[end - 1]))
                end--;
            return value.Substring(0, end) + "…";
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                labels.Dispose();
                headingFont.Dispose();
                strongFont.Dispose();
                bodyFont.Dispose();
                smallFont.Dispose();
            }
        }

        private sealed class Meter : Control
        {
            private readonly int percent;
            private readonly Color color;

            public Meter(int percent, Color color)
            {
                this.percent = percent;
                this.color = color;
                Dock = DockStyle.Fill;
                Margin = Padding.Empty;
                AccessibleName = percent + "%";
            }

            protected override void OnPaint(PaintEventArgs args)
            {
                base.OnPaint(args);
                int height = Math.Max(4, Height / 4);
                int y = (Height - height) / 2;
                using (var track = new SolidBrush(Border))
                    args.Graphics.FillRectangle(track, 0, y, Width, height);
                using (var fill = new SolidBrush(color))
                    args.Graphics.FillRectangle(fill, 0, y, Width * percent / 100, height);
            }
        }
    }
}
