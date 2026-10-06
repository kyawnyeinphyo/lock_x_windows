using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LockX.Core.Data;

namespace LockX.Views;

/// <summary>View-model wrapper around a vault item; decrypts its thumbnail lazily and caches it.</summary>
public sealed class ItemVM : INotifyPropertyChanged
{
    ImageSource? _thumb;
    bool _thumbLoaded;

    public ItemVM(VaultItem item) => Item = item;

    public VaultItem Item { get; private set; }
    public Guid Id => Item.Id;
    public string Name => Item.Name;
    public string Subtitle => $"{Item.SizeText} · {Item.ModifiedAt.ToLocalTime():g}";
    public string? DurationText => Item.DurationText;
    public Visibility DurationVisibility => DurationText == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlayBadgeVisibility => Item.Kind == ItemKind.Video ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

    public string Glyph => Glyphs.For(Item.Kind);

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbLoaded) return _thumb;
            _thumbLoaded = true;
            var png = App.Vault.GetThumbnailPng(Item);
            if (png == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(png);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _thumb = bmp;
            }
            catch { _thumb = null; }
            return _thumb;
        }
    }

    public void Refresh(VaultItem item)
    {
        if (!ReferenceEquals(item, Item) || item.HasThumbnail != (_thumb != null)) { _thumbLoaded = false; _thumb = null; }
        Item = item;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Glyphs
{
    // Segoe Fluent Icons / Segoe MDL2 Assets code points.
    public static string For(ItemKind kind) => kind switch
    {
        ItemKind.Image => "",
        ItemKind.Video => "",
        ItemKind.Note => "",
        _ => "",
    };
}

public sealed class KindVM : INotifyPropertyChanged
{
    int _count;
    public KindVM(ItemKind kind) => Kind = kind;
    public ItemKind Kind { get; }
    public string Title => Kind.Title();
    public string Glyph => Glyphs.For(Kind);
    public int Count
    {
        get => _count;
        set { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
