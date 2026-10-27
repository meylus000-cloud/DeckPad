using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DeckPad
{
    public class MainForm : Form
    {
        readonly DeckConfig cfg = DeckConfig.Load();
        readonly TabletLink link;
        readonly Timer debounce = new Timer { Interval = 300 };

        Panel gridHost;
        TableLayoutPanel gridPanel;
        Button[,] cells;
        int selR, selC;
        bool loading;
        bool reallyExit;

        NumericUpDown nRows, nCols;
        Label lblStatus;
        CheckBox cAdb;
        TextBox tHost, tLabel, tArgs;
        ComboBox cType, cValue;
        Label lblValue, lblArgs;
        Button bColor, bBrowse, bTest, bClear;
        NotifyIcon tray;

        public MainForm()
        {
            Text = "DeckPad";
            ClientSize = new Size(1000, 560);
            MinimumSize = new Size(900, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            BuildTop();
            BuildEditor();
            gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(24, 25, 31) };
            Controls.Add(gridHost);
            gridHost.BringToFront();

            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "DeckPad",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            tray.ContextMenuStrip.Items.Add("Open", null, (s, e) => ShowMe());
            tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => { reallyExit = true; Close(); });
            tray.DoubleClick += (s, e) => ShowMe();

            debounce.Tick += (s, e) => { debounce.Stop(); cfg.Save(); Push(); };

            link = new TabletLink(cfg);
            link.StatusChanged += (ok, msg) => BeginInvokeSafe(() =>
            {
                lblStatus.Text = (ok ? "● " : "○ ") + msg;
                lblStatus.ForeColor = ok ? Color.FromArgb(22, 140, 70) : Color.Gray;
                if (ok) Push();
            });
            link.Pressed += OnPressed;

            loading = true;
            nRows.Value = cfg.Rows;
            nCols.Value = cfg.Cols;
            cAdb.Checked = cfg.UseAdb;
            tHost.Text = cfg.Host;
            tHost.Enabled = !cfg.UseAdb;
            loading = false;

            BuildGrid();
            LoadEditor();
            Push();
            link.Start();
        }

        // ------------------------------------------------------------ layout

        void BuildTop()
        {
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(10, 8, 10, 0),
                BackColor = Color.FromArgb(240, 240, 243)
            };
            top.Controls.Add(new Label { Text = "Rows", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            nRows = new NumericUpDown { Minimum = 1, Maximum = 8, Width = 48 };
            nRows.ValueChanged += (s, e) => GridSizeChanged();
            top.Controls.Add(nRows);
            top.Controls.Add(new Label { Text = "Columns", AutoSize = true, Margin = new Padding(14, 6, 4, 0) });
            nCols = new NumericUpDown { Minimum = 1, Maximum = 12, Width = 48 };
            nCols.ValueChanged += (s, e) => GridSizeChanged();
            top.Controls.Add(nCols);

            cAdb = new CheckBox { Text = "USB cable (adb)", AutoSize = true, Margin = new Padding(24, 4, 6, 0) };
            cAdb.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                cfg.UseAdb = cAdb.Checked;
                tHost.Enabled = !cfg.UseAdb;
                cfg.Save();
            };
            top.Controls.Add(cAdb);
            top.Controls.Add(new Label { Text = "or IP:", AutoSize = true, Margin = new Padding(6, 6, 2, 0) });
            tHost = new TextBox { Width = 110 };
            tHost.TextChanged += (s, e) => { if (!loading) { cfg.Host = tHost.Text.Trim(); cfg.Save(); } };
            top.Controls.Add(tHost);

            lblStatus = new Label
            {
                Text = "○ Starting...",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(24, 6, 0, 0)
            };
            top.Controls.Add(lblStatus);
            Controls.Add(top);
        }

        void BuildEditor()
        {
            var p = new Panel { Dock = DockStyle.Right, Width = 330, Padding = new Padding(14) };
            int y = 8;

            p.Controls.Add(new Label { Text = "Button text", Left = 14, Top = y, AutoSize = true });
            tLabel = new TextBox { Left = 14, Top = y + 20, Width = 300 };
            tLabel.TextChanged += (s, e) => EditorChanged();
            p.Controls.Add(tLabel);
            y += 56;

            p.Controls.Add(new Label { Text = "Color", Left = 14, Top = y, AutoSize = true });
            bColor = new Button { Left = 14, Top = y + 20, Width = 300, Height = 28, FlatStyle = FlatStyle.Flat };
            bColor.Click += (s, e) =>
            {
                using (var d = new ColorDialog { Color = ParseColor(CurrentColor()), FullOpen = true })
                    if (d.ShowDialog() == DialogResult.OK)
                    {
                        bColor.Tag = ToHex(d.Color);
                        bColor.BackColor = d.Color;
                        EditorChanged();
                    }
            };
            p.Controls.Add(bColor);
            y += 60;

            p.Controls.Add(new Label { Text = "Action", Left = 14, Top = y, AutoSize = true });
            cType = new ComboBox { Left = 14, Top = y + 20, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            cType.Items.AddRange(new object[]
            {
                "None", "Hotkey", "Type text", "Run program", "Open URL", "Media key"
            });
            cType.SelectedIndexChanged += (s, e) => { TypeChanged(); EditorChanged(); };
            p.Controls.Add(cType);
            y += 60;

            lblValue = new Label { Left = 14, Top = y, AutoSize = true, Text = "Value" };
            p.Controls.Add(lblValue);
            cValue = new ComboBox { Left = 14, Top = y + 20, Width = 262 };
            cValue.TextChanged += (s, e) => EditorChanged();
            p.Controls.Add(cValue);
            bBrowse = new Button { Left = 282, Top = y + 19, Width = 32, Height = 24, Text = "..." };
            bBrowse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*" })
                    if (d.ShowDialog() == DialogResult.OK) cValue.Text = d.FileName;
            };
            p.Controls.Add(bBrowse);
            y += 60;

            lblArgs = new Label { Left = 14, Top = y, AutoSize = true, Text = "Arguments" };
            p.Controls.Add(lblArgs);
            tArgs = new TextBox { Left = 14, Top = y + 20, Width = 300 };
            tArgs.TextChanged += (s, e) => EditorChanged();
            p.Controls.Add(tArgs);
            y += 64;

            bTest = new Button { Left = 14, Top = y, Width = 145, Height = 30, Text = "Test (in 2 s)" };
            bTest.Click += (s, e) =>
            {
                var b = cfg.Find(selR, selC);
                if (b != null) Task.Delay(2000).ContinueWith(t => Actions.Run(b));
            };
            p.Controls.Add(bTest);
            bClear = new Button { Left = 169, Top = y, Width = 145, Height = 30, Text = "Clear button" };
            bClear.Click += (s, e) =>
            {
                var b = cfg.Find(selR, selC);
                if (b != null) cfg.Buttons.Remove(b);
                LoadEditor();
                RefreshCell(selR, selC);
                Dirty();
            };
            p.Controls.Add(bClear);
            y += 44;

            p.Controls.Add(new Label
            {
                Left = 14,
                Top = y,
                Width = 300,
                Height = 60,
                ForeColor = Color.Gray,
                Text = "Hotkey examples: Ctrl+C, Ctrl+Shift+M, Alt+F4, Win+D, F5.\r\nChanges are sent to the tablet automatically. Closing the window keeps DeckPad running in the tray."
            });

            Controls.Add(p);
        }

        // ------------------------------------------------------------ grid

        void GridSizeChanged()
        {
            if (loading) return;
            cfg.Rows = (int)nRows.Value;
            cfg.Cols = (int)nCols.Value;
            if (selR >= cfg.Rows) selR = cfg.Rows - 1;
            if (selC >= cfg.Cols) selC = cfg.Cols - 1;
            BuildGrid();
            LoadEditor();
            Dirty();
        }

        void BuildGrid()
        {
            if (gridPanel != null) { gridHost.Controls.Remove(gridPanel); gridPanel.Dispose(); }
            gridPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            for (int c = 0; c < cfg.Cols; c++)
                gridPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cfg.Cols));
            for (int r = 0; r < cfg.Rows; r++)
                gridPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / cfg.Rows));
            gridPanel.ColumnCount = cfg.Cols;
            gridPanel.RowCount = cfg.Rows;
            cells = new Button[cfg.Rows, cfg.Cols];
            for (int r = 0; r < cfg.Rows; r++)
                for (int c = 0; c < cfg.Cols; c++)
                {
                    int rr = r, cc = c;
                    var b = new Button
                    {
                        Dock = DockStyle.Fill,
                        Margin = new Padding(4),
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Segoe UI", 11f, FontStyle.Bold)
                    };
                    b.FlatAppearance.BorderSize = 2;
                    b.Click += (s, e) => { selR = rr; selC = cc; LoadEditor(); RefreshAll(); };
                    cells[r, c] = b;
                    gridPanel.Controls.Add(b, c, r);
                }
            gridHost.Controls.Add(gridPanel);
            RefreshAll();
        }

        void RefreshAll()
        {
            for (int r = 0; r < cfg.Rows; r++)
                for (int c = 0; c < cfg.Cols; c++)
                    RefreshCell(r, c);
        }

        void RefreshCell(int r, int c)
        {
            if (cells == null || r >= cells.GetLength(0) || c >= cells.GetLength(1)) return;
            var cell = cells[r, c];
            var b = cfg.Find(r, c);
            bool selected = (r == selR && c == selC);
            cell.FlatAppearance.BorderColor = selected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(50, 52, 60);
            if (b == null || DeckConfig.IsEmpty(b))
            {
                cell.BackColor = Color.FromArgb(34, 36, 44);
                cell.ForeColor = Color.Gray;
                cell.Text = "+";
            }
            else
            {
                Color col = ParseColor(b.Color);
                cell.BackColor = col;
                cell.ForeColor = Luma(col) > 150 ? Color.Black : Color.White;
                cell.Text = b.Label;
            }
        }

        // ------------------------------------------------------------ editor

        string CurrentColor()
        {
            return bColor.Tag as string ?? "#2D3748";
        }

        static string[] TypeKeys = { "none", "hotkey", "text", "run", "url", "media" };

        void LoadEditor()
        {
            loading = true;
            var b = cfg.Find(selR, selC);
            tLabel.Text = b != null ? b.Label : "";
            string col = b != null ? b.Color : "#2D3748";
            bColor.Tag = col;
            bColor.BackColor = ParseColor(col);
            int ti = b != null ? Array.IndexOf(TypeKeys, b.Type) : 0;
            cType.SelectedIndex = ti < 0 ? 0 : ti;
            TypeChanged();
            cValue.Text = b != null ? b.Value : "";
            tArgs.Text = b != null ? b.Args : "";
            loading = false;
        }

        void TypeChanged()
        {
            string t = TypeKeys[Math.Max(0, cType.SelectedIndex)];
            cValue.Items.Clear();
            cValue.DropDownStyle = ComboBoxStyle.DropDown;
            bBrowse.Visible = t == "run";
            lblArgs.Visible = tArgs.Visible = t == "run";
            cValue.Enabled = t != "none";
            switch (t)
            {
                case "hotkey": lblValue.Text = "Keys (e.g. Ctrl+Shift+M)"; break;
                case "text": lblValue.Text = "Text to type"; break;
                case "run": lblValue.Text = "Program path"; break;
                case "url": lblValue.Text = "Web address"; break;
                case "media":
                    lblValue.Text = "Media key";
                    cValue.DropDownStyle = ComboBoxStyle.DropDownList;
                    cValue.Items.AddRange(Actions.MediaNames);
                    break;
                default: lblValue.Text = "Value"; break;
            }
        }

        void EditorChanged()
        {
            if (loading) return;
            string t = TypeKeys[Math.Max(0, cType.SelectedIndex)];
            var b = cfg.Find(selR, selC);
            if (b == null)
            {
                b = new DeckButton { Row = selR, Col = selC };
                cfg.Buttons.Add(b);
            }
            b.Label = tLabel.Text;
            b.Color = CurrentColor();
            b.Type = t;
            b.Value = cValue.Text;
            b.Args = tArgs.Text;
            if (DeckConfig.IsEmpty(b)) cfg.Buttons.Remove(b);
            RefreshCell(selR, selC);
            Dirty();
        }

        // ------------------------------------------------------------ sync

        void Dirty()
        {
            debounce.Stop();
            debounce.Start();
        }

        void Push()
        {
            var list = new List<object>();
            foreach (var b in cfg.Buttons)
            {
                if (DeckConfig.IsEmpty(b) || b.Row >= cfg.Rows || b.Col >= cfg.Cols) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "i", b.Row * cfg.Cols + b.Col },
                    { "label", b.Label },
                    { "color", b.Color }
                });
            }
            var msg = new Dictionary<string, object>
            {
                { "type", "config" },
                { "cols", cfg.Cols },
                { "rows", cfg.Rows },
                { "buttons", list }
            };
            link.SetConfigJson(new JavaScriptSerializer().Serialize(msg));
        }

        void OnPressed(int index)
        {
            int r = index / cfg.Cols, c = index % cfg.Cols;
            var b = cfg.Find(r, c);
            if (b != null) Task.Run(() => Actions.Run(b));
        }

        // ------------------------------------------------------------ helpers

        void BeginInvokeSafe(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (Exception) { }
        }

        void ShowMe()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            cfg.Save();
            link.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        static Color ParseColor(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); } catch (Exception) { return Color.FromArgb(45, 55, 72); }
        }

        static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        static double Luma(Color c)
        {
            return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        }
    }
}
