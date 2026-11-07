using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;

internal partial class PatchWindow : Window
{
    private readonly PatchInstallation _installation;

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
        Notice.Text = message;
        UIElementAutomationPeer.FromElement(Notice)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = !Controls.IsEnabled;
        base.OnClosing(e);
    }
}
