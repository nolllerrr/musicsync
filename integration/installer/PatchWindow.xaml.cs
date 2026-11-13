using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

internal partial class PatchWindow : Window
{
    private readonly PatchInstallation _installation;
    private readonly CancellationTokenSource _windowLifetime = new();
    private Task<InstallerRelease?>? _updateCheck;
    private int _noticeVersion;

    internal PatchWindow(PatchInstallation installation)
    {
        InitializeComponent();
        _installation = installation;
        try
        {
            Endpoint.Text = (_installation.ReadInstalledEndpoint() ?? PatchSettings.Load()).AbsoluteUri;
        }
        catch (Exception error)
        {
            Endpoint.Text = PatchSettings.DefaultEndpoint().AbsoluteUri;
            SetNotice($"Не удалось прочитать настройки. {error.Message}");
        }
    }

    private async void CheckUpdatesOnLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= CheckUpdatesOnLoad;
        await Task.WhenAll(CheckUpdatesAsync(interactive: false), CheckCompatibilityAsync());
    }

    private async Task CheckCompatibilityAsync()
    {
        var noticeVersion = _noticeVersion;
        var cancellationToken = _windowLifetime.Token;
        var diagnosis = await Task.Run(_installation.Diagnose);
        if (cancellationToken.IsCancellationRequested || noticeVersion != _noticeVersion)
            return;

        if (CompatibilityNotice.For(diagnosis) is { } message)
            SetNotice(message);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            await CheckUpdatesAsync(interactive: true);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        var cancellationToken = _windowLifetime.Token;
        InstallerRelease? release;
        try
        {
            if (_updateCheck is not { IsCompleted: false })
                _updateCheck = InstallerRelease.CheckAsync(cancellationToken);
            release = await _updateCheck;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or InvalidDataException
                                      or OperationCanceledException)
        {
            if (interactive && !cancellationToken.IsCancellationRequested)
                MessageBox.Show(this, "Не удалось проверить обновления. Попробуйте ещё раз позже.",
                    "Обновления MusicSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        UpdateBadge.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        var buttonLabel = release is null
            ? "Проверить обновления"
            : $"Доступно обновление MusicSync {release.Version}";
        UpdateButton.ToolTip = buttonLabel;
        AutomationProperties.SetName(UpdateButton, buttonLabel);

        if (!interactive)
            return;

        if (release is null)
        {
            MessageBox.Show(this, "У вас актуальная версия MusicSync.", "Обновления MusicSync",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Доступна версия MusicSync {release.Version}. Скачать новый установщик?",
                "Обновления MusicSync", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(release.DownloadUrl.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "Не удалось открыть ссылку на скачивание.", "Обновления MusicSync",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
        base.OnClosed(e);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var address = Endpoint.Text;
        await RunAsync("Устанавливаем патч…", () =>
        {
            var endpoint = PatchSettings.Parse(address);
            var result = _installation.Run(install: true, endpoint);
            return SavePreference(endpoint, result);
        });
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Удаляем патч…", () => _installation.Run(install: false, endpoint: null));

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var address = Endpoint.Text;
        await RunAsync("Сохраняем адрес…", () =>
        {
            var endpoint = PatchSettings.Parse(address);
            if (_installation.SaveEndpoint(endpoint))
            {
                return SavePreference(endpoint, "Адрес сохранён. Он будет использован при подключении к следующей комнате.");
            }

            PatchSettings.Save(endpoint);
            return "Адрес сохранён. Он будет использован при установке патча.";
        });
    }

    private static string SavePreference(Uri endpoint, string result)
    {
        try
        {
            PatchSettings.Save(endpoint);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{result} Не удалось сохранить адрес для будущих установок. {error.Message}";
        }
    }

    private async Task RunAsync(string progress, Func<string> action)
    {
        Controls.IsEnabled = false;
        SetNotice(progress);
        try
        {
            SetNotice(await Task.Run(action));
        }
        catch (Exception error)
        {
            SetNotice(error.Message);
        }
        finally
        {
            Controls.IsEnabled = true;
        }
    }

    private void SetNotice(string message)
    {
        _noticeVersion++;
        Notice.Text = message;
        UIElementAutomationPeer.FromElement(Notice)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = !Controls.IsEnabled;
        base.OnClosing(e);
    }
}
