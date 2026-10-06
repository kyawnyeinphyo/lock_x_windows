using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LockX.Core.Data;
using Windows.Storage;

namespace LockX.Services;

/// <summary>
/// Reads dimensions / duration and renders a thumbnail from the plaintext source
/// while we still have it: afterwards the only copy is encrypted.
/// </summary>
public sealed class MediaInspector : IMediaInspector
{
    const int MaxPixel = 512;

    public async Task<MediaInfo?> InspectAsync(string path, ItemKind kind)
    {
        switch (kind)
        {
            case ItemKind.Image:
            {
                var (w, h, png) = DecodeImage(path);
                png ??= ShellThumbnail(path);
                return new MediaInfo(null, w, h, png);
            }
            case ItemKind.Video:
            {
                double? duration = null; int? w = null, h = null;
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var props = await file.Properties.GetVideoPropertiesAsync();
                    if (props.Duration > TimeSpan.Zero) duration = props.Duration.TotalSeconds;
                    if (props.Width > 0) w = (int)props.Width;
                    if (props.Height > 0) h = (int)props.Height;
                }
                catch { /* best effort */ }
                return new MediaInfo(duration, w, h, ShellThumbnail(path));
            }
            default:
                return null;
        }
    }

    /// <summary>Decodes with WPF (jpeg/png/gif/bmp/tiff/ico, plus HEIC/WebP when the Store codecs are present).</summary>
    static (int? w, int? h, byte[]? png) DecodeImage(string path)
    {
        int? w = null, h = null;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            w = decoder.Frames[0].PixelWidth;
            h = decoder.Frames[0].PixelHeight;
        }
        catch { return (null, null, null); }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (w >= h) bmp.DecodePixelWidth = Math.Min(MaxPixel, w!.Value);
            else bmp.DecodePixelHeight = Math.Min(MaxPixel, h!.Value);
            bmp.EndInit();
            bmp.Freeze();
            return (w, h, EncodePng(bmp));
        }
        catch { return (w, h, null); }
    }

    static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ---- Windows shell thumbnails (videos, exotic images) ------------------------

    static byte[]? ShellThumbnail(string path)
    {
        IntPtr hbitmap = IntPtr.Zero;
        object? factoryObj = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factoryObj) != 0 || factoryObj == null)
                return null;
            var factory = (IShellItemImageFactory)factoryObj;
            // THUMBNAILONLY: fail rather than hand back a generic file icon.
            int hr = factory.GetImage(new SIZE { cx = MaxPixel, cy = MaxPixel }, SIIGBF.BIGGERSIZEOK | SIIGBF.THUMBNAILONLY, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty,
                                                                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return EncodePng(source);
        }
        catch { return null; }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factoryObj != null) Marshal.ReleaseComObject(factoryObj);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? shellItem);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx; public int cy; }

    [Flags]
    enum SIIGBF { RESIZETOFIT = 0, BIGGERSIZEOK = 1, MEMORYONLY = 2, ICONONLY = 4, THUMBNAILONLY = 8, INCACHEONLY = 0x10 }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
