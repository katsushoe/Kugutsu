using Kugutsu.Contracts;

namespace Kugutsu.App;

internal sealed class OperationBufferForm : Form
{
    private readonly OperationRequestQueue _queue;
    private readonly DataGridView _grid;
    private readonly Label _countLabel;
    private readonly Label _lockStatusLabel;
    private readonly Label _emergencyStatusLabel;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _allowClose;

    internal OperationBufferForm(OperationRequestQueue queue)
    {
        _queue = queue;
        Text = "Kugutsu - 操作バッファ";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 320);
        Size = new Size(960, 520);

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            Text = "要求ID・受付時刻・状態・操作名を表示します。入力値、結果データ、画像は表示しません。"
        };
        _grid = CreateGrid();
        _countLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        };
        Controls.Add(_grid);
        Controls.Add(_countLabel);
        _lockStatusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        };
        Controls.Add(_lockStatusLabel);
        _emergencyStatusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0)
        };
        Controls.Add(_emergencyStatusLabel);
        Controls.Add(heading);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshBuffer();
        Shown += (_, _) => RefreshBuffer();
        VisibleChanged += (_, _) =>
        {
            if (Visible) { RefreshBuffer(); _refreshTimer.Start(); }
            else { _refreshTimer.Stop(); }
        };
        FormClosing += (_, args) =>
        {
            if (_allowClose || args.CloseReason != CloseReason.UserClosing) { return; }
            args.Cancel = true;
            Hide();
        };
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "requestId", HeaderText = "要求ID", FillWeight = 42 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "acceptedAt", HeaderText = "受付時刻（UTC）", FillWeight = 26 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "state", HeaderText = "状態", FillWeight = 16 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "operations", HeaderText = "操作", FillWeight = 38 });
        return grid;
    }

    private void RefreshBuffer()
    {
        _emergencyStatusLabel.Text = EmergencyStopDisplay.Describe(_queue.GetEmergencyStopStatus());
        _lockStatusLabel.Text = LockStatusDisplay.Describe(_queue.GetLockDisplayStatus());
        var entries = _queue.GetBufferSnapshot();
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            foreach (var entry in entries)
            {
                _grid.Rows.Add(entry.RequestId.ToString("D"),
                    entry.AcceptedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture),
                    ToStateLabel(entry.State), entry.OperationSummary);
            }
            _countLabel.Text = $"表示中: {entries.Count} 件";
        }
        finally { _grid.ResumeLayout(); }
    }

    private static string ToStateLabel(RequestState state) => state switch
    {
        RequestState.Queued => "待機中",
        RequestState.Running => "実行中",
        RequestState.Succeeded => "完了",
        RequestState.Failed => "失敗",
        RequestState.Cancelled => "取消",
        RequestState.TimedOut => "タイムアウト",
        _ => "不明"
    };

    internal void AllowClose() => _allowClose = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _refreshTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
