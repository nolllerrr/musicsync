using System.IO;
using System.Text.Json;
using MusicSync.Core;

internal static class PatchSettings
{
    private static readonly string PathToSettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicSync", "together.json");

    internal static Uri Parse(string value)
    {
        var endpoint = Protocol.ParseEndpoint(value, allowLan: false);
        if (endpoint is null || endpoint.Host.Length == 0 ||
            (endpoint.Scheme == "ws" && endpoint.IdnHost is not ("127.0.0.1" or "::1" or "localhost")))
        {
            throw new InvalidDataException("Укажите адрес вида wss://сервер/ws. Для локального сервера можно использовать ws://127.0.0.1:5050/ws.");
        }

        return endpoint;
    }

    internal static Uri Read(string path)
    {
        using var file = File.OpenRead(path);
        using var document = JsonDocument.Parse(file);
        return Parse(document.RootElement.GetProperty("endpoint").GetString() ?? "");
    }

    internal static Uri DefaultEndpoint()
    {
        using var resource = typeof(PatchSettings).Assembly.GetManifestResourceStream("DefaultEndpoint");
        if (resource is null)
        {
            return new Uri("ws://127.0.0.1:5050/ws");
        }

        using var reader = new StreamReader(resource);
        return Parse(reader.ReadToEnd());
    }

    internal static Uri Load() => File.Exists(PathToSettings) ? Read(PathToSettings) : DefaultEndpoint();

    internal static byte[] Encode(Uri endpoint) => JsonSerializer.SerializeToUtf8Bytes(new { endpoint = endpoint.AbsoluteUri });

    internal static void Save(Uri endpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathToSettings)!);
        var next = PathToSettings + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(next, Encode(endpoint));
            File.Move(next, PathToSettings, overwrite: true);
        }
        finally { File.Delete(next); }
    }
}
