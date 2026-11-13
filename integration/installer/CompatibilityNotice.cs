internal static class CompatibilityNotice
{
    internal static string? For(InstallationDiagnosis diagnosis) => diagnosis.Status switch
    {
        InstallationStatus.Installed or InstallationStatus.SupportedOriginal => null,
        InstallationStatus.PatchMissing => "Яндекс Музыка обновилась. Нажмите «Установить патч», чтобы вернуть раздел «Вместе».",
        InstallationStatus.Unsupported or InstallationStatus.HashMismatch =>
            $"Яндекс Музыка {diagnosis.ProductVersion ?? diagnosis.PackageVersion ?? ""}: сборка пока не проверена. Проверьте обновления MusicSync кнопкой со стрелками.",
        _ => diagnosis.Message
    };
}
