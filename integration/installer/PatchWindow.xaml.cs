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
    private bool _updatingRecovery = true;

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
        Controls.IsEnabled = false;
        try
        {
            await Task.WhenAll(RefreshDiagnosisAsync(), CheckUpdatesAsync(interactive: false));
            if (AutomaticRecovery.IsChecked == true)
            {
                var warning = await Task.Run(() => RefreshRecoveryWorker(true, _installation.Root, ""));
                if (warning.Length != 0)
                    SetNotice(warning);
            }
        }
        finally
        {
            Controls.IsEnabled = true;
        }
    }

    private async void CheckCompatibility_Click(object sender, RoutedEventArgs e)
    {
        Controls.IsEnabled = false;
        SetNotice("Проверяем совместимость…");
        try
        {
            await RefreshDiagnosisAsync();
            SetNotice(CompatibilityMessage.Text);
        }
        finally
        {
            Controls.IsEnabled = true;
        }
    }

    private async Task RefreshDiagnosisAsync()
    {
        try
        {
            var diagnosis = await Task.Run(_installation.Diagnose);
            MusicVersion.Text = $"Версия: {diagnosis.ProductVersion ?? "не определена"}" +
                (diagnosis.PackageVersion is { } package ? $" · ASAR {package}" : "");
            PatchState.Text = "Патч: " + (diagnosis.Status switch
            {
                InstallationStatus.Installed => "установлен",
                InstallationStatus.PatchMissing => "заменён обновлением Яндекс Музыки",
                InstallationStatus.Interrupted => "операция не завершена",
                InstallationStatus.Damaged => "требует проверки",
                InstallationStatus.Missing or InstallationStatus.SupportedOriginal => "не установлен",
                _ => "состояние не подтверждено"
            });
            CompatibilityState.Text = "Совместимость: " + (diagnosis.Status switch
            {
                InstallationStatus.Installed or InstallationStatus.SupportedOriginal or InstallationStatus.PatchMissing
                    => "проверенная сборка",
                InstallationStatus.Unsupported => "пока не поддерживается",
                InstallationStatus.HashMismatch => "контрольные суммы не совпали",
                InstallationStatus.Missing => "Яндекс Музыка не найдена",
                InstallationStatus.Busy => "дождитесь окончания другой операции",
                _ => "нужна проверка"
            });
            CompatibilityMessage.Text = diagnosis.Message;
            RepairButton.IsEnabled = diagnosis.CanRepair;
            InstallButton.IsEnabled = diagnosis.CanRepair || diagnosis.Status == InstallationStatus.Installed;
            UIElementAutomationPeer.FromElement(CompatibilityMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception error)
        {
            MusicVersion.Text = "Версия: не определена";
            PatchState.Text = "Патч: состояние не подтверждено";
            CompatibilityState.Text = "Совместимость: проверка не завершена";
            CompatibilityMessage.Text = error.Message;
            RepairButton.IsEnabled = false;
            InstallButton.IsEnabled = false;
        }

        _updatingRecovery = true;
        try
        {
            AutomaticRecovery.IsChecked = await Task.Run(() => AutoRecovery.IsEnabled(_installation.Root));
        }
        catch (Exception error)
        {
            AutomaticRecovery.IsChecked = false;
            SetNotice($"Не удалось прочитать настройку автоматического восстановления. {error.Message}");
        }
        finally
        {
            _updatingRecovery = false;
        }
    }

    private async void AutomaticRecovery_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingRecovery)
            return;

        var enabled = AutomaticRecovery.IsChecked == true;
        await RunAsync(enabled ? "Включаем автоматическое восстановление…" : "Отключаем автоматическое восстановление…", () =>
        {
            if (enabled)
                AutoRecovery.Enable(_installation.Root);
            else
                AutoRecovery.Disable(_installation.Root);
            return enabled
                ? "Автоматическое восстановление включено. Яндекс Музыка будет проверяться после обновлений без принудительного закрытия."
                : "Автоматическое восстановление отключено.";
        });
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
            ? "Проверить обновления MusicSync"
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
        var automaticRecovery = AutomaticRecovery.IsChecked == true;
        await RunAsync("Устанавливаем патч…", () =>
        {
            var endpoint = PatchSettings.Parse(address);
            var result = _installation.Run(install: true, endpoint);
            return RefreshRecoveryWorker(automaticRecovery, _installation.Root, SavePreference(endpoint, result));
        });
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var address = Endpoint.Text;
        var automaticRecovery = AutomaticRecovery.IsChecked == true;
        await RunAsync("Восстанавливаем патч…", () =>
        {
            var diagnosis = _installation.Diagnose();
            if (!diagnosis.CanRepair)
                throw new InvalidOperationException(diagnosis.Message);
            var endpoint = PatchSettings.Parse(address);
            return RefreshRecoveryWorker(automaticRecovery, _installation.Root,
                SavePreference(endpoint, _installation.Run(install: true, endpoint)));
        });
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Удаляем патч…", () =>
        {
            AutoRecovery.Disable(_installation.Root);
            return _installation.Run(install: false, endpoint: null);
        });

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

    private static string RefreshRecoveryWorker(bool enabled, string root, string result)
    {
        if (!enabled)
            return result;
        try
        {
            // A newer MusicSync installer also updates the opted-in worker's compatibility catalog.
            AutoRecovery.Enable(root);
            return result;
        }
        catch (Exception error)
        {
            return $"{result} Не удалось обновить автоматическое восстановление. {error.Message}".Trim();
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
            await RefreshDiagnosisAsync();
            Controls.IsEnabled = true;
        }
    }

    private void SetNotice(string message)
    {
        Notice.Text = message;
        UIElementAutomationPeer.FromElement(Notice)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = !Controls.IsEnabled;
        base.OnClosing(e);
    }
}
