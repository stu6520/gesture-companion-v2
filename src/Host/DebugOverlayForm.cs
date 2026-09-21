using System.Drawing;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace GestureCompanionPointerHost;

sealed class DebugOverlayForm : Forms.Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private readonly Forms.Label _label;

    public DebugOverlayForm()
    {
        Text = "Gesture Companion Debug";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = Forms.FormStartPosition.Manual;
        Size = new Size(365, 232);
        BackColor = Color.FromArgb(20, 24, 30);
        Opacity = 0.9;
        Padding = new Forms.Padding(12);

        _label = new Forms.Label
        {
            Dock = Forms.DockStyle.Fill,
            ForeColor = Color.FromArgb(218, 245, 247),
            Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Text = WaitingText(),
            TextAlign = ContentAlignment.TopLeft
        };
        Controls.Add(_label);

        var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 18, area.Top + 18);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public void UpdateSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            _label.Text = WaitingText();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("state", out var state) || state.GetString() != "debug")
            {
                _label.Text = "GESTURE DEBUG\nBridge response: " + json;
                return;
            }

            var contacts = root.GetProperty("contacts").GetInt32();
            var peak = root.GetProperty("peak").GetInt32();
            var panX = root.GetProperty("panX100").GetInt32() / 100.0;
            var panY = root.GetProperty("panY100").GetInt32() / 100.0;
            var zoom = root.GetProperty("zoom100").GetInt32() / 100.0;
            var rotate = root.GetProperty("rotate100").GetInt32() / 100.0;
            var movement = root.GetProperty("movement100").GetInt32() / 100.0;
            var panActive = root.GetProperty("panActive").GetBoolean();
            var last = root.GetProperty("last").GetString() ?? "none";
            var sessionEvents = root.GetProperty("sessionEvents").GetUInt64();
            var events = root.GetProperty("events").GetUInt64();
            var rawUpdates = root.GetProperty("rawUpdates").GetUInt64();
            var evaluatedFrames = root.GetProperty("evaluatedFrames").GetUInt64();
            var pointerMessages = root.GetProperty("pointerMessages").GetUInt64();
            var touchMessages = root.GetProperty("touchMessages").GetUInt64();
            var pointerInfoFailures = root.GetProperty("pointerInfoFailures").GetUInt64();
            var hookInstalled = root.GetProperty("hookInstalled").GetBoolean();

            _label.Text =
                "GESTURE DEBUG  [temporary]\n" +
                $"Hook     : {(hookInstalled ? "INSTALLED" : "FAILED")}\n" +
                $"Pointer  : {pointerMessages} messages / {touchMessages} touch / {pointerInfoFailures} failed\n" +
                $"Contacts : {contacts}   Peak: {peak}\n" +
                $"Pan      : X {panX,7:0.00}  Y {panY,7:0.00}  {(panActive ? "ACTIVE" : "idle")}\n" +
                $"Zoom     : {zoom,8:0.00} px\n" +
                $"Rotate   : {rotate,8:0.00} deg\n" +
                $"Movement : {movement,8:0.00} px\n" +
                $"Last     : {last}\n" +
                $"Events   : {sessionEvents} session / {events} total\n" +
                $"Frames   : {evaluatedFrames} evaluated / {rawUpdates} raw";
        }
        catch
        {
            _label.Text = "GESTURE DEBUG\nInvalid snapshot:\n" + json;
        }
    }

    private static string WaitingText() =>
        "GESTURE DEBUG  [temporary]\nBridge: waiting for supported app...\nContacts : 0   Peak: 0\nLast     : none\nEvents   : 0";
}
