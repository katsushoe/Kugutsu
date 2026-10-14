namespace Kugutsu.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly OperationBufferForm _bufferForm;
    private readonly OperationRequestQueue _queue;
    private readonly ToolStripMenuItem _lockStatus;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly ToolStripMenuItem _emergencyStatus;
    private readonly ToolStripMenuItem _emergencyStop;
    private readonly ToolStripMenuItem _resumeOperations;
    private readonly ToolStripMenuItem _forceUnlock;
    private readonly ToolStripMenuItem _exit;
    private readonly CancellationTokenRegistration _hostStoppingRegistration;
    private volatile bool _disposed;
    private bool _exitRequested;

    internal TrayApplicationContext(OperationRequestQueue queue, CancellationToken hostStopping)
    {
        _queue = queue;
        _bufferForm = new OperationBufferForm(queue);
        var showBuffer = new ToolStripMenuItem("操作バッファを表示", null,
            (_, _) => ShowBuffer());
        _exit = new ToolStripMenuItem("終了", null, async (_, _) => await ExitApplicationAsync());
        var menu = new ContextMenuStrip();
        _emergencyStatus = new ToolStripMenuItem { Enabled = false };
        _emergencyStop = new ToolStripMenuItem("操作を緊急停止", null, async (_, _) =>
        {
            var stopping = _queue.EmergencyStopAsync();
            RefreshLockStatus();
            await stopping;
            RefreshLockStatus();
        });
        _resumeOperations = new ToolStripMenuItem("操作受付を再開", null, (_, _) =>
        {
            _queue.TryResumeAfterEmergencyStop();
            RefreshLockStatus();
        });
        _forceUnlock = new ToolStripMenuItem("停止してロックを強制解除", null, async (_, _) =>
        {
            var stopping = _queue.ForceUnlockAsync();
            RefreshLockStatus();
            await stopping;
            RefreshLockStatus();
        });
        menu.Items.Add(_emergencyStatus);
        _lockStatus = new ToolStripMenuItem { Enabled = false };
        menu.Items.Add(_lockStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showBuffer);
        menu.Items.Add(_emergencyStop);
        menu.Items.Add(_forceUnlock);
        menu.Items.Add(_resumeOperations);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exit);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Kugutsu",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowBuffer();
        menu.Opening += (_, _) => RefreshLockStatus();
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshLockStatus();
        RefreshLockStatus();
        _statusTimer.Start();
        _ = _bufferForm.Handle;
        _hostStoppingRegistration = hostStopping.Register(() =>
        {
            if (_disposed) { return; }
            _queue.ShutdownAsync();
            _bufferForm.BeginInvoke(new Action(async () => await ExitApplicationAsync()));
        });
    }

    private void RefreshLockStatus()
    {
        if (_disposed) { return; }
        var status = _queue.GetLockDisplayStatus();
        var emergency = _queue.GetEmergencyStopStatus();
        _emergencyStatus.Text = EmergencyStopDisplay.Describe(emergency);
        _exit.Enabled = !emergency.ShuttingDown;
        _emergencyStop.Enabled = !emergency.ShuttingDown && !emergency.Active;
        _resumeOperations.Enabled = !emergency.ShuttingDown && emergency.Active && !emergency.StopConfirmationPending && !emergency.ForceUnlockPending;
        _forceUnlock.Enabled = !emergency.ShuttingDown && status.Locked && !emergency.ForceUnlockPending;
        _lockStatus.Text = LockStatusDisplay.Describe(status);
        _notifyIcon.Text = emergency.ShuttingDown ? "Kugutsu - 終了中・実行停止確認中" : emergency.ForceUnlockPending ? "Kugutsu - ロック強制解除・停止確認中" : emergency.Active
            ? emergency.StopConfirmationPending ? "Kugutsu - 緊急停止・実行停止確認中" : "Kugutsu - 緊急停止・操作受付停止中"
            : LockStatusDisplay.Tooltip(status);
    }

    private void ShowBuffer()
    {
        if (_bufferForm.IsDisposed) { return; }
        if (!_bufferForm.Visible) { _bufferForm.Show(); }
        if (_bufferForm.WindowState == FormWindowState.Minimized)
        { _bufferForm.WindowState = FormWindowState.Normal; }
        _bufferForm.Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_exitRequested || _disposed) { return; }
        _exitRequested = true;
        var shutdown = _queue.ShutdownAsync();
        RefreshLockStatus();
        await shutdown;
        if (_disposed) { return; }
        _notifyIcon.Visible = false;
        _bufferForm.AllowClose();
        _bufferForm.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hostStoppingRegistration.Dispose();
            _statusTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _bufferForm.AllowClose();
            _bufferForm.Dispose();
        }
        base.Dispose(disposing);
    }
}
