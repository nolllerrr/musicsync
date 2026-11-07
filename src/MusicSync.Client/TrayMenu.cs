using MusicSync.Core;
using Forms = System.Windows.Forms;

namespace MusicSync.Client;

internal sealed class TrayMenu : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem status = new("Нет комнаты") { Enabled = false };
    private readonly Forms.ToolStripMenuItem create;
    private readonly Forms.ToolStripMenuItem join;
    private readonly Forms.ToolStripMenuItem copy;
    private readonly Forms.ToolStripMenuItem play;
    private readonly Forms.ToolStripMenuItem swap;
    private readonly Forms.ToolStripMenuItem leave;

    public TrayMenu(MainWindow owner)
    {
        Forms.ToolStripMenuItem Item(string title, Action action) =>
            new(title, null, (_, _) => owner.Dispatcher.InvokeAsync(action));

        create = Item("Создать комнату и скопировать код", owner.CreateFromTray);
        join = Item("Войти по коду…", owner.ShowJoinWindow);
        copy = Item("Копировать код комнаты", () => owner.CopyRoomCode(showNotification: true));
        play = Item("Запустить ссылку из буфера", async () => await owner.PlayClipboardAsync());
        swap = Item("Сменить роли", async () => await owner.SwapRolesAsync());
        swap.Enabled = false;
        leave = Item("Выйти из комнаты", async () => await owner.LeaveRoomAsync());
        menu.Items.AddRange([status, new Forms.ToolStripSeparator(), create, join, copy, play, swap, leave,
            new Forms.ToolStripSeparator(), Item("Показать MusicSync", owner.ShowStatusWindow),
            Item("Завершить MusicSync", async () => await owner.QuitAsync())]);

        icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "MusicSync"
        };
        icon.DoubleClick += (_, _) => owner.Dispatcher.InvokeAsync(owner.ShowStatusWindow);
        Update(null);
        icon.Visible = true;
    }

    public void UpdateRoleSwap(string label, bool enabled)
    {
        swap.Text = label;
        swap.Enabled = enabled;
    }

    public void Update(ConnectionView? connection)
    {
        bool active = connection?.Status is ConnectionStatus.Connecting or
            ConnectionStatus.Connected or ConnectionStatus.Reconnecting;
        bool connected = connection?.Status == ConnectionStatus.Connected;
        status.Text = connection?.Status switch
        {
            ConnectionStatus.Connecting => "Подключение…",
            ConnectionStatus.Reconnecting => "Переподключение…",
            ConnectionStatus.Connected when connection.Room?.State.Unknown == true => "Точный ID трека недоступен",
            ConnectionStatus.Connected when connection.Role == Role.Leader =>
                connection.Room?.Listener.Connected == true ? "Ведущий · слушатель в сети" : "Ведущий · ждём слушателя",
            ConnectionStatus.Connected =>
                connection.Room?.Leader.Connected == true ? "Слушатель · ведущий в сети" : "Слушатель · ждём ведущего",
            ConnectionStatus.Ended => "Комната закрыта",
            _ => "Нет комнаты"
        };
        string tooltip = "MusicSync · " + status.Text;
        if (icon.Text != tooltip)
            icon.Text = tooltip;
        create.Enabled = join.Enabled = !active;
        copy.Enabled = connection?.Room is not null && active;
        play.Enabled = connected && connection?.Role == Role.Leader;
        leave.Enabled = active;
    }

    public void ShowMessage(string message)
    {
        icon.BalloonTipTitle = "MusicSync";
        icon.BalloonTipText = message;
        icon.ShowBalloonTip(3500);
    }

    public void Dispose()
    {
        icon.Dispose();
        menu.Dispose();
    }
}
