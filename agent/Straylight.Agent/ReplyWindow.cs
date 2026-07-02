using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Windows.Forms;

namespace Straylight.Agent;

/// <summary>
/// Interactive ask/reply window, launched into the user session as `--reply-window &lt;token&gt;`
/// (same CreateProcessAsUser path as --dim-watch). Reads ask-&lt;token&gt;.json, shows a window —
/// title + body with a small markdown subset (incl. real <b>**bold**</b>), optional buttons,
/// optional text box — then writes reply-&lt;token&gt;.json with the outcome. The service's
/// FileSystemWatcher forwards that to MQTT.
///
/// Non-urgent asks render as a **toast**: bottom-right, top-most but does NOT steal focus (so it
/// won't interrupt what you're typing), no taskbar/alt-tab entry, dismiss with the ✕. `urgent`
/// asks render as a centered, focus-grabbing dialog meant to be seen right now.
/// </summary>
internal static class ReplyWindow
{
    const string Dir = @"C:\ProgramData\Straylight";

    /// <summary>A Form that can appear without stealing focus (for the toast style).</summary>
    sealed class ToastForm : Form
    {
        public bool NoActivate;
        protected override bool ShowWithoutActivation => NoActivate;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (NoActivate) cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: keep out of alt-tab
                return cp;
            }
        }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    const int SW_SHOWNOACTIVATE = 4;
    const uint RDW_INVALIDATE = 0x1, RDW_UPDATENOW = 0x100;

    // WinForms wants an STA thread; the service/helper main thread is MTA.
    public static void Run(string token)
    {
        var t = new Thread(() => RunUi(token));
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    static void RunUi(string token)
    {
        string askPath = Path.Combine(Dir, $"ask-{token}.json");
        Ask ask;
        try { ask = Ask.Load(askPath); }
        catch { return; }

        bool answered = false;
        string? btnId = null, btnName = null;
        string typed = "";

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var bg = Color.FromArgb(32, 33, 36);
        var border = Color.FromArgb(70, 71, 75);
        var title = string.IsNullOrWhiteSpace(ask.Title) ? "Message" : ask.Title!;
        bool toast = !ask.Urgent;
        bool hasButtons = ask.Buttons is { Length: > 0 };
        bool showInput = ask.Reply && !hasButtons;   // buttons = a fixed choice; no free-text box

        var form = new ToastForm
        {
            NoActivate = toast,
            Text = title,
            TopMost = true,
            MinimizeBox = false,
            MaximizeBox = false,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            KeyPreview = true,
            ClientSize = new Size(440, showInput ? 244 : 210),
        };
        if (toast)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.StartPosition = FormStartPosition.Manual;
            form.ShowInTaskbar = false;
            form.BackColor = border;          // 1px frame shows through the layout's inset
            form.Padding = new Padding(1);
        }
        else
        {
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ShowInTaskbar = true;
            form.BackColor = bg;
        }

        var lblTitle = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoEllipsis = true,
        };
        var closeBtn = new Label
        {
            Text = "✕",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(150, 150, 155),
            Font = new Font("Segoe UI", 11f),
            Cursor = Cursors.Hand,
            AutoSize = false,
            Width = 26,
        };

        var body = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = bg,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5f),
            TabStop = false,
        };
        RenderMarkdown(body, ask.Text ?? "");

        TextBox? input = null;
        if (showInput)
        {
            input = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(48, 49, 52),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
            };
        }

        var buttonsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = bg,
            WrapContents = false,
        };
        var tip = new ToolTip();

        void Finish(bool ans, string? bid, string? bname)
        {
            answered = ans;
            btnId = bid;
            btnName = bname;
            typed = input?.Text ?? "";
            form.Close();
        }

        closeBtn.Click += (_, _) => Finish(false, null, null);

        Button MakeButton(string text, Color color)
            => new()
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(74, 30),
                Margin = new Padding(6, 6, 0, 4),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = color,
            };

        if (ask.Buttons is { Length: > 0 })
        {
            for (int i = ask.Buttons.Length - 1; i >= 0; i--) // reverse: first button ends up left-most
            {
                var b = ask.Buttons[i];
                var btn = MakeButton(b.Name ?? b.Id ?? "OK", Color.FromArgb(60, 64, 72));
                string? bid = b.Id, bname = b.Name;
                btn.Click += (_, _) => Finish(true, bid, bname);
                if (!string.IsNullOrEmpty(b.Hint)) tip.SetToolTip(btn, b.Hint);
                buttonsRow.Controls.Add(btn);
            }
        }
        else
        {
            var send = MakeButton("Send", Color.FromArgb(66, 133, 244));
            send.Click += (_, _) => Finish(true, null, null);
            buttonsRow.Controls.Add(send);
        }

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = bg };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(lblTitle, 0, 0);
        header.Controls.Add(closeBtn, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = bg,
            Padding = new Padding(12, 10, 12, 10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.Controls.Add(header);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.Controls.Add(body);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        if (input != null)
        {
            layout.Controls.Add(input);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }
        layout.Controls.Add(buttonsRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowCount = layout.RowStyles.Count;

        form.Controls.Add(layout);

        form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(false, null, null); };

        if (toast)
        {
            // bottom-right of the working area (above the taskbar); no focus grab
            form.Load += (_, _) =>
            {
                var wa = Screen.PrimaryScreen!.WorkingArea;
                form.Location = new Point(wa.Right - form.Width - 16, wa.Bottom - form.Height - 16);
            };
            // TopMost alone doesn't assert over the active window when shown without activation
            // (worse for a service-launched window that isn't composited yet). Force it visible +
            // top-of-z-order + repainted, without stealing focus, and re-assert once shortly after.
            form.Shown += (_, _) =>
            {
                void Surface()
                {
                    ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
                    SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                }
                Surface();
                var t = new System.Windows.Forms.Timer { Interval = 150 };
                t.Tick += (_, _) => { t.Stop(); t.Dispose(); Surface(); };
                t.Start();
            };
        }
        else
        {
            form.Shown += (_, _) => { form.Activate(); SetForegroundWindow(form.Handle); input?.Focus(); };
        }

        Application.Run(form);

        var reply = new Dictionary<string, object?>
        {
            ["id"] = ask.Id,
            ["question"] = ask.Text,   // echo the ask text so the HA log/voice read cleanly
            ["title"] = title,
            ["button"] = btnId,
            ["button_name"] = btnName,
            ["text"] = answered ? typed : "",
            ["dismissed"] = !answered,
            ["ts"] = DateTime.Now.ToString("s"),
        };
        try { File.WriteAllText(Path.Combine(Dir, $"reply-{token}.json"), JsonSerializer.Serialize(reply)); }
        catch { }
        try { File.Delete(askPath); } catch { }
    }

    // Render the markdown subset into the RichTextBox: "- "/"* "/"• " -> bullet, **bold** -> bold run.
    static void RenderMarkdown(RichTextBox rtb, string src)
    {
        var baseFont = rtb.Font;
        using var boldFont = new Font(baseFont, FontStyle.Bold);
        foreach (var rawLine in src.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            var m = Regex.Match(line, @"^\s*[-*•]\s+(.*)$");
            if (m.Success) { AppendRun(rtb, "  •  ", baseFont); line = m.Groups[1].Value; }

            int idx = 0;
            foreach (Match b in Regex.Matches(line, @"\*\*(.+?)\*\*"))
            {
                if (b.Index > idx) AppendRun(rtb, line[idx..b.Index], baseFont);
                AppendRun(rtb, b.Groups[1].Value, boldFont);
                idx = b.Index + b.Length;
            }
            if (idx < line.Length) AppendRun(rtb, line[idx..], baseFont);
            rtb.AppendText("\n");
        }
    }

    static void AppendRun(RichTextBox rtb, string text, Font font)
    {
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionFont = font;
        rtb.SelectionColor = rtb.ForeColor;
        rtb.AppendText(text);
    }

    sealed class Ask
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? Text { get; set; }
        public bool Urgent { get; set; }
        public bool Reply { get; set; }
        public Btn[]? Buttons { get; set; }

        public static Ask Load(string path)
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<Ask>(File.ReadAllText(path), opts)
                   ?? throw new InvalidDataException("ask deserialized to null");
        }
    }

    sealed class Btn
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Hint { get; set; }
    }
}
