using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MusicSync.Core;
using MusicSync.Yandex;

namespace MusicSync.Client;

public partial class MainWindow : Window
{
    private sealed record Settings(string Server = "ws://127.0.0.1:5050/ws", bool AllowLan = false,
        int CorrectionThresholdMs = 1200);

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicSync");
    private static readonly string LogPath = Path.Combine(DataFolder, "MusicSync.log");
    private static readonly object LogGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly MonotonicClock localClock = new();
    private readonly RoomPlayer player;
    private readonly DispatcherTimer timer;
    private readonly TrayMenu tray;
    private readonly int threshold;
    private readonly bool usesSonata;
    private volatile RoomConnection? connection;
    private RoomConnection? copyAfterConnect;
    private Guid? displayedSwap, announcedSwap;
    private bool sending, swapping, leaving, closing;

    public MainWindow() : this(null) { }

    internal MainWindow(int? sonataPort)
    {
        InitializeComponent();
        usesSonata = sonataPort.HasValue;
        string mode = usesSonata ? "Sonata" : "Обычный режим";
        ModeLabel.Text = mode;
        Title = $"MusicSync {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · {mode}";
        var settings = ReadSettings();
        Endpoint.Text = settings.Server;
        AllowLan.IsChecked = settings.AllowLan;
        threshold = Math.Clamp(settings.CorrectionThresholdMs, 500, 10_000);
        long PlayerTime() => connection?.Clock.Now ?? localClock.Now;
        IPlayer adapter = sonataPort is int port ? new SonataPlayer(port, PlayerTime) : new YandexPlayer(PlayerTime);
        if (sonataPort is int debugPort)
            Log($"Player adapter: Sonata (local CDP port {debugPort}).");
        player = new RoomPlayer(adapter, () => connection, threshold, Log);
        tray = new TrayMenu(this);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, Render, Dispatcher);
        Closing += ClosingWindow;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Hide();
        };
    }

    private static Settings ReadSettings()
    {
        foreach (var path in new[] { Path.Combine(DataFolder, "settings.json"),
                     Path.Combine(AppContext.BaseDirectory, "MusicSync.settings.json") })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json);
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.Server))
                    return settings;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log($"Settings could not be read ({ex.GetType().Name}).");
            }
        }
        return new();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var settings = new Settings(Endpoint.Text.Trim(), AllowLan.IsChecked == true, threshold);
            File.WriteAllText(Path.Combine(DataFolder, "settings.json"), JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Settings could not be saved ({ex.GetType().Name}).");
        }
    }

    private static void Log(string line)
    {
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                if (new FileInfo(LogPath) is { Exists: true, Length: > 1_000_000 })
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Environment.ProcessId}] {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Create_Click(object sender, RoutedEventArgs args) => CreateFromTray();
    internal void CreateFromTray() => Connect(null, copyInvitation: true);

    internal void ShowStatusWindow()
    {
        if (closing)
            return;
        Render(null, EventArgs.Empty);
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    internal void ShowJoinWindow()
    {
        ShowStatusWindow();
        Invitation.Focus();
        Invitation.SelectAll();
    }

    private void Join_Click(object sender, RoutedEventArgs args)
    {
        string code = Invitation.Text.Trim().ToUpperInvariant();
        if (Protocol.IsCode(code))
            Connect(code);
        else
            Notice.Text = "Введите код комнаты из 24 символов.";
    }

    private void Connect(string? invitation, bool copyInvitation = false)
    {
        if (leaving || connection?.View.Status is ConnectionStatus.Connecting or ConnectionStatus.Connected or
            ConnectionStatus.Reconnecting)
            return;
        var uri = Protocol.ParseEndpoint(Endpoint.Text, AllowLan.IsChecked == true);
        if (uri is null)
        {
            ShowStatusWindow();
            NetworkOptions.IsExpanded = true;
            Endpoint.Focus();
            Notice.Text = "Укажите адрес wss://…/ws или ws://127.0.0.1:5050/ws. Для ws:// в домашней сети включите разрешение и укажите частный IPv4.";
            return;
        }
        if (connection is { } old)
            _ = old.DisposeAsync();
        Notice.Text = "";
        SaveSettings();
        var room = new RoomConnection(uri, invitation, Log);
        connection = room;
        copyAfterConnect = copyInvitation ? room : null;
        Render(null, EventArgs.Empty);
    }

    private async void Leave_Click(object sender, RoutedEventArgs args) => await LeaveRoomAsync();

    private async void SwapRoles_Click(object sender, RoutedEventArgs args) => await SwapRolesAsync();

    internal async Task SwapRolesAsync()
    {
        if (connection is not { } room || displayedSwap is not Guid swapId || swapping)
            return;
        swapping = true;
        Notice.Text = "";
        Render(null, EventArgs.Empty);
        try
        {
            string? error = await room.SwapRolesAsync(swapId);
            if (connection == room && error is not null)
            {
                Notice.Text = Explain(error);
                if (!IsVisible)
                    tray.ShowMessage(Notice.Text);
            }
        }
        finally
        {
            swapping = false;
            if (!closing)
                Render(null, EventArgs.Empty);
        }
    }

    internal async Task LeaveRoomAsync()
    {
        if (connection is not { } room || leaving)
            return;
        leaving = true;
        connection = null;
        copyAfterConnect = null;
        Notice.Text = "";
        Render(null, EventArgs.Empty);
        try { await room.LeaveAsync(); }
        finally { leaving = false; }
    }

    private void Copy_Click(object sender, RoutedEventArgs args) => CopyRoomCode();

    internal void CopyRoomCode(bool showNotification = false)
    {
        if (connection?.View.Room?.State.RoomId is not { } code)
            return;
        try
        {
            Clipboard.SetText(code);
            Notice.Text = "Код скопирован.";
            if (showNotification)
                tray.ShowMessage("Код комнаты скопирован.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Notice.Text = "Буфер обмена занят. Повторите копирование.";
            if (showNotification)
                tray.ShowMessage(Notice.Text);
        }
    }

    internal async Task PlayClipboardAsync()
    {
        TrackId? track;
        try { track = TrackId.Parse(Clipboard.GetText().Trim(), allowNative: true); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            tray.ShowMessage("Буфер обмена занят. Попробуйте ещё раз.");
            return;
        }
        if (track is null)
        {
            tray.ShowMessage("Скопируйте ссылку на трек из Яндекс Музыки.");
            return;
        }
        await SendAsync(ActionKind.Select, track);
    }

    private async void Select_Click(object sender, RoutedEventArgs args)
    {
        var track = TrackId.Parse(TrackUrl.Text.Trim());
        if (track is null)
            Notice.Text = "Укажите ссылку вида https://music.yandex.ru/album/10030/track/38634573.";
        else
            await SendAsync(ActionKind.Select, track);
    }

    private async void Play_Click(object sender, RoutedEventArgs args) => await SendAsync(ActionKind.Play);
    private async void Pause_Click(object sender, RoutedEventArgs args) => await SendAsync(ActionKind.Pause);

    private async void Seek_Click(object sender, RoutedEventArgs args)
    {
        var parts = SeekPosition.Text.Trim().Split(':');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long minutes) || minutes >= 1440 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) || seconds >= 60)
        {
            Notice.Text = "Введите позицию в формате м:сс, например 1:30.";
            return;
        }
        long ms = (minutes * 60 + seconds) * 1000;
        var reading = player.View.Reading;
        if (reading.Track is not null && reading.Track == connection?.View.Room?.State.Track &&
            reading.DurationMs is long duration && ms >= duration)
        {
            Notice.Text = "Позиция должна быть меньше длительности трека.";
            return;
        }
        await SendAsync(ActionKind.Seek, position: ms);
    }

    private async Task SendAsync(ActionKind kind, TrackId? track = null, long? position = null)
    {
        if (connection is not { } room || sending)
            return;
        sending = true;
        Notice.Text = "";
        Render(null, EventArgs.Empty);
        try
        {
            string? error = await room.ActionAsync(kind, track, position);
            if (connection == room && error is not null)
                Notice.Text = Explain(error);
        }
        finally { sending = false; }
    }

    private void Retry_Click(object sender, RoutedEventArgs args)
    {
        Notice.Text = "";
        player.Retry();
    }

    private void Logs_Click(object sender, RoutedEventArgs args)
    {
        Log("Diagnostics opened.");
        try { Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true }); }
        catch (Win32Exception) { Notice.Text = $"Журнал диагностики · {LogPath}"; }
    }

    private void Render(object? sender, EventArgs args)
    {
        var room = connection;
        var network = room?.View;
        var snapshot = network?.Room;
        var reading = player.View.Reading;
        bool connected = network?.Status == ConnectionStatus.Connected;
        RenderRoleSwap(room, network);
        tray.Update(network);
        if (connected && copyAfterConnect == room)
        {
            copyAfterConnect = null;
            CopyRoomCode(showNotification: !IsVisible);
        }
        bool inRoom = snapshot is not null && network?.Status != ConnectionStatus.Ended;
        bool busy = network?.Status is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting;
        Lobby.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        CancelConnection.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Endpoint.IsEnabled = Invitation.IsEnabled = AllowLan.IsEnabled = !busy;
        CreateButton.IsEnabled = JoinButton.IsEnabled = !busy && !leaving;
        NetworkOptions.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        RoomPanel.Visibility = SyncPanel.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        ServerStatus.Text = network?.Status switch
        {
            ConnectionStatus.Connected => "Подключён",
            ConnectionStatus.Connecting => "Подключение…",
            ConnectionStatus.Reconnecting => "Переподключение…",
            ConnectionStatus.Ended => Explain(network.Error),
            _ => "Отключён"
        };
        PlayerStatus.Text = reading switch
        {
            { Running: false } when usesSonata => "Нет подключения в режиме Sonata",
            { Running: false } => "Откройте приложение Яндекс Музыка",
            { Ready: false } => "Ожидание данных плеера",
            _ => "Готова к синхронизации"
        };
        if (!inRoom)
            return;
        var state = snapshot!.State;
        bool leader = network!.Role == Role.Leader;
        RoomCode.Text = state.RoomId;
        RoleStatus.Text = leader ? "Вы · ведущий" : "Вы · слушатель";
        var peer = leader ? snapshot.Listener : snapshot.Leader;
        var ownReport = leader ? snapshot.Leader.Report : snapshot.Listener.Report;
        string peerRole = leader ? "Слушатель" : "Ведущий";
        PeerStatus.Text = !peer.Connected ? $"{peerRole} · не в сети" :
            $"{peerRole} · в сети · {ReportText(peer.Report?.Kind, peer.Report?.Detail)}" +
            (peer.Report?.ApplyMs is long peerMs ? $" · {peerMs} мс" : "");
        Song.Text = string.IsNullOrWhiteSpace(reading.Title) ? "Нет активного трека" : reading.Title;
        Artist.Text = reading.Artist;
        PlaybackProgress.Visibility = reading.Ready ? Visibility.Visible : Visibility.Collapsed;
        PlaybackProgress.Value = reading.Ready ? Math.Clamp((double)reading.PositionMs!.Value / reading.DurationMs!.Value, 0, 1) : 0;
        Timeline.Text = $"{FormatTime(reading.PositionMs)} / {FormatTime(reading.DurationMs)}  ·  " +
            (reading.Playback switch { Playback.Playing => "играет", Playback.Paused => "пауза", _ => "ожидание" });
        SyncStatus.Text = "Синхронизация · " + (connected
            ? ReportText(ownReport?.Kind, ownReport?.Detail) : "ожидание соединения");
        RevisionStatus.Text = $"Ревизия {state.Revision}" +
            (connected && ownReport?.ApplyMs is long applyMs ? $" · применение за {applyMs} мс" : "") +
            (connected && room!.Clock.RttMs is long rttMs ? $" · RTT {rttMs} мс" : "");
        long? difference = connected ? SyncClock.Difference(snapshot, room!.Clock.Now) : null;
        DifferenceStatus.Text = difference is long delta
            ? $"Позиция слушателя − позиция ведущего ≈ {delta:+0;-0;0} мс"
            : "Разница позиций · данных пока недостаточно";
        LeaderControls.Visibility = leader ? Visibility.Visible : Visibility.Collapsed;
        LeaderControls.IsEnabled = leader && connected && !sending;
        PlayButton.IsEnabled = PauseButton.IsEnabled = SeekButton.IsEnabled = state.Track is not null;
        RetryButton.Visibility = connected && ownReport?.Kind == ReportKind.Failed && state.Track is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderRoleSwap(RoomConnection? room, ConnectionView? network)
    {
        var swap = network?.Room?.Swap;
        bool connected = network?.Status == ConnectionStatus.Connected;
        long remaining = swap?.ExpiresAt is long deadline && room is not null
            ? Math.Max(0, deadline - room.Clock.Now) : 0;
        bool expired = swap?.ExpiresAt is not null && remaining == 0;
        bool ownRequest = swap?.RequestedBy is Role requester && requester == network?.Role;
        bool peerRequest = connected && swap?.RequestedBy is not null && !ownRequest && !expired;
        displayedSwap = connected && !swapping && !ownRequest && !expired ? swap?.Id : null;
        string label = peerRequest ? "Подтвердить смену ролей" : "Сменить роли";
        SwapRolesButton.Content = label;
        SwapRolesButton.IsEnabled = displayedSwap.HasValue;
        tray.UpdateRoleSwap(label, displayedSwap.HasValue);
        long seconds = (remaining + 999) / 1000;
        var peer = network?.Role == Role.Leader ? network.Room?.Listener : network?.Room?.Leader;
        SwapStatus.Text = !connected ? "" :
            peer?.Connected != true ? "Смена ролей доступна, когда оба участника в сети." :
            swap is null ? "Для смены ролей обновите сервер и оба клиента." :
            swap.RequestedBy is null ? "Для смены ролей нужно подтверждение обоих участников." :
            expired ? "Время подтверждения истекло." :
            ownRequest ? $"Ждём подтверждения второго участника · {seconds} с" :
            $"Второй участник предлагает сменить роли · {seconds} с";

        if (peerRequest && announcedSwap != swap!.Id)
        {
            announcedSwap = swap.Id;
            if (!IsVisible)
                tray.ShowMessage("Второй участник предлагает сменить роли. Подтвердите в меню MusicSync.");
        }
    }

    private static string FormatTime(long? ms) => ms is long value ? $"{value / 60000}:{value / 1000 % 60:00}" : "—";

    private static string ReportText(ReportKind? kind, string? detail) => kind switch
    {
        ReportKind.Applying => "применение…",
        ReportKind.Applied => "подтверждено плеером",
        ReportKind.Corrected => "позиция скорректирована",
        ReportKind.Unknown => Explain("unknown-track"),
        ReportKind.Failed => Explain(detail),
        ReportKind.Waiting when detail == "waiting-next-track" => "ожидание следующего трека ведущего",
        _ => "ожидание"
    };

    private static string Explain(string? code) => code switch
    {
        "unknown-track" => "Трек определён, но точный ID недоступен.",
        "no-player" => "Откройте Яндекс Музыку.",
        "window-closed" => "Откройте окно Яндекс Музыки.",
        "no-session" => "Яндекс Музыка не опубликовала медиасессию.",
        "media-session-timeout" => "Windows не отвечает на запрос к плееру. Откройте альбом в Яндекс Музыке, чтобы была видна панель текущего трека.",
        "track-unavailable" => "Карточка или запуск трека не подтверждены. Проверьте доступность песни.",
        "track-unconfirmed" => "Точный трек не подтверждён плеером.",
        "command-unconfirmed" => "Плеер не подтвердил команду.",
        "position-unconfirmed" => "Плеер не подтвердил целевую позицию.",
        "track-ended" => "Трек дошёл до конца. Выберите следующий трек.",
        "player-busy" => "Другая команда MusicSync заняла плеер.",
        "leader-left" => "Ведущий вышел. Комната закрыта.",
        "leader-timeout" => "Время ожидания ведущего истекло. Комната закрыта.",
        "room-unavailable" => "Комната недоступна. Проверьте код или создайте новую.",
        "room-full" => "В комнате уже есть слушатель.",
        "server-full" => "Достигнут лимит комнат на сервере.",
        "stale-revision" => "Состояние уже изменилось. Повторите команду при необходимости.",
        "role-swap-stale" => "Запрос на смену ролей устарел. Проверьте состояние комнаты.",
        "role-swap-unavailable" => "Для смены ролей оба участника должны быть в сети.",
        "protocol-version" => "Версии протокола клиента и сервера различаются.",
        "offline" or "connection-lost" => "Соединение с сервером потеряно.",
        "delivery-unknown" => "Ответ на команду потерян. Сверьте состояние комнаты.",
        _ => "Операция завершилась с ошибкой. Откройте журнал диагностики."
    };

    private void ClosingWindow(object? sender, CancelEventArgs args)
    {
        if (closing)
            return;
        args.Cancel = true;
        Dispatcher.InvokeAsync(Hide);
    }

    internal async Task QuitAsync()
    {
        if (closing)
            return;
        closing = true;
        timer.Stop();
        Hide();
        var room = connection;
        connection = null;
        try
        {
            if (room is not null)
                await room.LeaveAsync();
        }
        finally
        {
            try { await player.DisposeAsync(); }
            finally { Application.Current.Shutdown(); }
        }
    }

    internal void DisposeTray() => tray.Dispose();
}
