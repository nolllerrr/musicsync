using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MusicSync.Core;

public sealed record TrackId(string AlbumId, string Id)
{
    private static readonly Regex PathPattern = new(@"^/album/([1-9][0-9]{0,18})/track/([1-9][0-9]{0,18})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [JsonIgnore] public bool IsValid => ValidId(AlbumId) && ValidId(Id);

    private static bool ValidId(string? value) =>
        value is { Length: >= 1 and <= 19 } && value[0] != '0' && value.All(char.IsAsciiDigit);

    public static TrackId? Parse(string value, bool allowNative = false)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            !url.IsDefaultPort || url.UserInfo.Length != 0)
            return null;

        if (url.Scheme == "https" && url.Host == "music.yandex.ru")
        {
            string raw = value.Split('?', '#')[0];
            int pathStart = raw.IndexOf('/', "https://".Length);
            return pathStart >= 0 && raw[pathStart..] == url.AbsolutePath ? ParsePath(url.AbsolutePath) : null;
        }
        return allowNative && url.Scheme == "yandexmusic" && url.Host == "album"
            ? ParsePath("/album" + url.AbsolutePath)
            : null;
    }

    public static TrackId? ParsePath(string path)
    {
        var match = PathPattern.Match(path);
        return match.Success ? new TrackId(match.Groups[1].Value, match.Groups[2].Value) : null;
    }
}

public enum Role { Leader, Listener }
public enum Playback { Paused, Playing }
public enum ActionKind { Select, Play, Pause, Seek, Adopt, Unknown }
public enum ReportKind { Applying, Applied, Corrected, Failed, Unknown, Waiting }

public sealed record RoomState(string RoomId, long Revision, long Selection,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TrackId? Track,
    Playback Playback, long PositionMs, long ServerTimestamp, bool Unknown)
{
    public long PositionAt(long serverTime, long? durationMs = null) =>
        Math.Min(durationMs ?? Protocol.MaxPositionMs, PositionMs +
            (Playback == Playback.Playing ? Math.Max(0, serverTime - ServerTimestamp) : 0));
}

public sealed record RoomAction(ActionKind Kind, long BaseRevision, TrackId? Track = null,
    long? PositionMs = null, Playback? Playback = null)
{
    [JsonIgnore] public bool IsValid => BaseRevision >= 0 && (Kind switch
    {
        ActionKind.Select => Track is { IsValid: true } && PositionMs is null && Playback is null,
        ActionKind.Adopt => Track is { IsValid: true } && ValidPosition && Playback is not null,
        ActionKind.Seek => Track is null && ValidPosition && Playback is null,
        ActionKind.Play or ActionKind.Pause or ActionKind.Unknown =>
            Track is null && PositionMs is null && Playback is null,
        _ => false
    });

    private bool ValidPosition => PositionMs is >= 0 and <= Protocol.MaxPositionMs;
}

public sealed record PlayerReport(long Revision, ReportKind Kind, long? PositionMs = null,
    long? DurationMs = null, long? SampleTime = null, long? RttMs = null, Playback? Playback = null,
    long? ApplyMs = null, string? Detail = null)
{
    [JsonIgnore] public bool IsValid => Revision >= 0 &&
        ApplyMs is null or (>= 0 and <= 120_000) && Detail is null or { Length: <= 350 } &&
        (Kind is ReportKind.Applied or ReportKind.Corrected
            ? PositionMs is >= 0 and <= Protocol.MaxPositionMs &&
                DurationMs is > 0 and <= Protocol.MaxPositionMs && PositionMs <= DurationMs &&
                SampleTime is > 0 && RttMs is null or (>= 0 and <= 30_000) && Playback is not null
            : PositionMs is null && DurationMs is null && SampleTime is null && RttMs is null && Playback is null);
}

public sealed record Participant(bool Connected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PlayerReport? Report);
public sealed record RoleSwap(Guid Id, Role? RequestedBy = null, long? ExpiresAt = null);
public sealed record RoomView(RoomState State, Participant Leader, Participant Listener, RoleSwap? Swap = null);

public sealed record ClientMessage(int Version, string Type, string? Token = null, string? RoomId = null,
    Guid? Id = null, RoomAction? Action = null, PlayerReport? Report = null, long? Ping = null, Guid? SwapId = null);

public sealed record ServerMessage(int Version, string Type, Role? Role = null, RoomView? Room = null,
    Guid? Id = null, string? Error = null, long? Ping = null, long? Received = null, long? Sent = null);

public static class Protocol
{
    public const int Version = 1;
    public const string RoleSwapSubProtocol = "musicsync.roleswap";
    public const int MaxMessageBytes = 4096;
    public const long MaxPositionMs = 24 * 60 * 60 * 1000;
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(12);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool IsCode(string? value) => IsHex(value, 24);
    public static bool IsToken(string? value) => IsHex(value, 64);
    private static bool IsHex(string? value, int length) =>
        value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static bool IsPrivateV4(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }

    public static Uri? ParseEndpoint(string value, bool allowLan)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/ws" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
            return null;
        if (uri.Scheme == "wss")
            return uri;
        if (uri.Scheme != "ws")
            return null;
        if (uri.IsLoopback)
            return uri;
        return allowLan && IPAddress.TryParse(uri.IdnHost, out var ip) && IsPrivateV4(ip) ? uri : null;
    }
}
