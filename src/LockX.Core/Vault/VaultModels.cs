using System.Text.Json.Serialization;

namespace LockX.Core.Data;

[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind { Image, Video, File, Note }

public static class ItemKindInfo
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico", ".dng", ".cr2", ".nef", ".arw" };

    static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts", ".flv", ".ts" };

    public static string Title(this ItemKind kind) => kind switch
    {
        ItemKind.Image => "Images",
        ItemKind.Video => "Videos",
        ItemKind.File => "Files",
        _ => "Notes",
    };

    /// <summary>Buckets an imported file into one of the four sections.</summary>
    public static ItemKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExt.Contains(ext)) return ItemKind.Image;
        if (VideoExt.Contains(ext)) return ItemKind.Video;
        return ItemKind.File;
    }
}

public sealed class VaultItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ItemKind Kind { get; set; }
    public long ByteSize { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public bool HasThumbnail { get; set; }
    public double? Duration { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }

    [JsonIgnore]
    public string SizeText => FormatSize(ByteSize);

    [JsonIgnore]
    public string? DurationText
    {
        get
        {
            if (Duration is not { } d || double.IsNaN(d) || double.IsInfinity(d) || d <= 0) return null;
            var t = TimeSpan.FromSeconds(Math.Round(d));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[i]}";
    }
}

/// <summary>Everything needed to recover the master key with Windows Hello. Inert on any other PC.</summary>
public sealed class DeviceWrap
{
    public string KeyName { get; set; } = "";
    public byte[] Challenge { get; set; } = Array.Empty<byte>();
    public byte[] SealedMasterKey { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Public, unencrypted vault metadata. Holds only the material needed to unwrap the
/// master key, never the key itself and never any item content.
/// </summary>
public sealed class VaultMeta
{
    public int Version { get; set; } = 1;
    public string VaultId { get; set; } = Guid.NewGuid().ToString("N");
    public byte[] KdfSalt { get; set; } = Array.Empty<byte>();
    public int KdfRounds { get; set; }
    /// <summary>Master key sealed with the password-derived key.</summary>
    public byte[] PasswordWrappedKey { get; set; } = Array.Empty<byte>();
    /// <summary>Present only while Windows Hello unlock is enrolled.</summary>
    public DeviceWrap? DeviceWrap { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int AutoLockSeconds { get; set; } = 300;
    public bool LockOnHide { get; set; } = true;

    [JsonIgnore]
    public bool BiometricsEnabled => DeviceWrap != null;
}

public sealed record MediaInfo(double? Duration, int? Width, int? Height, byte[]? ThumbnailPng);

/// <summary>Reads dimensions/duration and makes a thumbnail from the plaintext file while we still have it.</summary>
public interface IMediaInspector
{
    Task<MediaInfo?> InspectAsync(string path, ItemKind kind);
}

/// <summary>Windows Hello (or any device-bound authenticator) as a second way to unwrap the master key.</summary>
public interface IDeviceUnlock
{
    Task<bool> IsAvailableAsync();
    Task<DeviceWrap> EnrollAsync(string vaultId, byte[] masterKey);
    Task<byte[]> UnwrapAsync(DeviceWrap wrap);
    Task RemoveAsync(DeviceWrap wrap);
}

public sealed class UnlockCancelledException : Exception
{
    public UnlockCancelledException() : base("Authentication was cancelled.") { }
}
