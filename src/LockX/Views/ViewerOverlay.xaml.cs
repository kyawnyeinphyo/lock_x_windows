using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LockX.Core.Data;
using LockX.Services;

namespace LockX.Views;

/// <summary>Full-window viewer: zoomable images, streamed video, notes, and a hand-off for other files.</summary>
public partial class ViewerOverlay : UserControl
{
    IReadOnlyList<ItemVM> _items = Array.Empty<ItemVM>();
    int _index;
    int _loadToken;
    StreamServer? _server;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _playing, _programmatic;
    Point _panStart, _panOrigin;
    bool _panning;

    public event Action? CloseRequested;
    public event Action<ItemVM>? EditNoteRequested;
    public event Action<ItemVM>? ExportRequested;

    ItemVM? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    public ViewerOverlay()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => OnClockTick();
        App.Vault.Locking += OnVaultLocking;
        Unloaded += (_, _) => App.Vault.Locking -= OnVaultLocking;
    }

    void OnVaultLocking() => Dispatcher.Invoke(() => { Cleanup(); Visibility = Visibility.Collapsed; });

    public void Show(IReadOnlyList<ItemVM> items, ItemVM current)
    {
        _items = items;
        _index = Math.Max(0, items.ToList().FindIndex(i => i.Id == current.Id));
        Visibility = Visibility.Visible;
        Focus();
        Load();
    }

    public void Hide()
    {
        Cleanup();
        Visibility = Visibility.Collapsed;
    }

    // ---- Loading ------------------------------------------------------------------

    async void Load()
    {
        int token = ++_loadToken;
        var vm = Current;
        Cleanup();
        if (vm == null) { CloseRequested?.Invoke(); return; }
        var item = vm.Item;

        Title.Text = item.Name;
        Position.Text = _items.Count > 1 ? $"{_index + 1} of {_items.Count}" : "";
        Subtitle.Text = SubtitleFor(item);
        EditButton.Visibility = item.Kind == ItemKind.Note ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = NextButton.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = "Decrypting…";
        Status.Visibility = Visibility.Visible;

        try
        {
            switch (item.Kind)
            {
                case ItemKind.Image:
                {
                    var bytes = await App.Vault.GetDataAsync(item);
                    var bmp = await Task.Run(() =>
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.StreamSource = new MemoryStream(bytes);
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        return b;
                    });
                    if (token != _loadToken) return;
                    ResetZoom();
                    Picture.Source = bmp;
                    ImagePanel.Visibility = Visibility.Visible;
                    break;
                }
                case ItemKind.Video:
                {
                    _server = new StreamServer(App.Vault.OpenReader(item), item.Name);
                    VideoPanel.Visibility = Visibility.Visible;
                    Player.Volume = Volume.Value;
                    Player.Source = _server.Url;
                    Player.Play();
                    SetPlaying(true);
                    _clock.Start();
                    break;
                }
                case ItemKind.Note:
                {
                    var text = await Task.Run(() => App.Vault.NoteText(item));
                    if (token != _loadToken) return;
                    NoteText.Text = text.Length == 0 ? "Empty note" : text;
                    NotePanel.Visibility = Visibility.Visible;
                    break;
                }
                default:
                    ShowFilePanel(item, "This file type can't be previewed inside Lock X.");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            Cleanup();
            ShowFilePanel(item, item.Kind == ItemKind.Image
                ? "Windows can't display this image format here."
                : ex.Message);
        }
        if (token == _loadToken) Status.Visibility = Visibility.Collapsed;
    }

    static string SubtitleFor(VaultItem item)
    {
        var parts = new List<string>();
        if (item.DurationText is { } d) parts.Add(d);
        if (item.PixelWidth is { } w && item.PixelHeight is { } h) parts.Add($"{w} × {h}");
        parts.Add(item.SizeText);
        return string.Join(" · ", parts);
    }

    void ShowFilePanel(VaultItem item, string message)
    {
        FileGlyph.Text = Glyphs.For(item.Kind);
        FileName.Text = item.Name;
        FileMessage.Text = message;
        FilePanel.Visibility = Visibility.Visible;
        Status.Visibility = Visibility.Collapsed;
    }

    void Cleanup()
    {
        _clock.Stop();
        SetPlaying(false);
        try { Player.Stop(); Player.Source = null; } catch { }
        _server?.Dispose();
        _server = null;
        Picture.Source = null;
        NoteText.Text = "";
        ImagePanel.Visibility = VideoPanel.Visibility = NotePanel.Visibility = FilePanel.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Collapsed;
        Seek.Value = 0;
        Elapsed.Text = Total.Text = "0:00";
    }

    // ---- Navigation ---------------------------------------------------------------

    void Step(int delta)
    {
        if (_items.Count < 2) return;
        _index = (_index + delta + _items.Count) % _items.Count;
        Load();
    }

    void OnPrev(object sender, RoutedEventArgs e) => Step(-1);
    void OnNext(object sender, RoutedEventArgs e) => Step(+1);

    void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Current is { } vm) EditNoteRequested?.Invoke(vm);
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        if (Current is { } vm) ExportRequested?.Invoke(vm);
    }

    async void OnOpenExternally(object sender, RoutedEventArgs e)
    {
        if (Current is not { } vm) return;
        try { await TempFiles.ExportAndOpenAsync(App.Vault, vm.Item); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lock X", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CloseRequested?.Invoke(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(+1); break;
            case Key.Space when VideoPanel.Visibility == Visibility.Visible: TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- Image zoom / pan ---------------------------------------------------------

    void ResetZoom()
    {
        Zoom.ScaleX = Zoom.ScaleY = 1;
        Pan.X = Pan.Y = 0;
        FitButton.Visibility = Visibility.Collapsed;
    }

    void OnFit(object sender, RoutedEventArgs e) => ResetZoom();

    void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        double old = Zoom.ScaleX;
        double next = Math.Clamp(old * Math.Pow(1.15, e.Delta / 120.0), 1, 16);
        if (Math.Abs(next - old) < 1e-6) return;
        var p = e.GetPosition(ImagePanel);
        double k = next / old;
        Zoom.ScaleX = Zoom.ScaleY = next;
        Pan.X = p.X - (p.X - Pan.X) * k;
        Pan.Y = p.Y - (p.Y - Pan.Y) * k;
        ClampPan();
        FitButton.Visibility = next > 1.001 ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void ClampPan()
    {
        double s = Zoom.ScaleX;
        if (s <= 1.0001) { Pan.X = Pan.Y = 0; return; }
        Pan.X = Math.Clamp(Pan.X, -ImagePanel.ActualWidth * (s - 1), 0);
        Pan.Y = Math.Clamp(Pan.Y, -ImagePanel.ActualHeight * (s - 1), 0);
    }

    void OnImageDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ResetZoom(); return; }
        if (Zoom.ScaleX <= 1.0001) return;
        _panning = true;
        _panStart = e.GetPosition(ImagePanel);
        _panOrigin = new Point(Pan.X, Pan.Y);
        ImagePanel.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    void OnImageMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(ImagePanel);
        Pan.X = _panOrigin.X + (p.X - _panStart.X);
        Pan.Y = _panOrigin.Y + (p.Y - _panStart.Y);
        ClampPan();
    }

    void OnImageUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImagePanel.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    // ---- Video --------------------------------------------------------------------

    static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayGlyph.Text = playing ? "" : "";   // pause : play
    }

    void TogglePlay()
    {
        if (_playing) { Player.Pause(); SetPlaying(false); }
        else { Player.Play(); SetPlaying(true); }
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();
    void OnPlayerClick(object sender, MouseButtonEventArgs e) => TogglePlay();

    void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        var d = Player.NaturalDuration.TimeSpan;
        Seek.Maximum = Math.Max(1, d.TotalSeconds);
        Total.Text = Clock(d);
    }

    void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Player.Pause();
        SetPlaying(false);
    }

    void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var vm = Current;
        Cleanup();
        if (vm != null)
            ShowFilePanel(vm.Item, "Windows can't play this video format. Try opening it with another app, or export it.");
    }

    void OnClockTick()
    {
        if (_playing) App.Session?.NoteActivity();   // watching a film is activity
        if (!Player.NaturalDuration.HasTimeSpan) return;
        _programmatic = true;
        Seek.Value = Player.Position.TotalSeconds;
        _programmatic = false;
        Elapsed.Text = Clock(Player.Position);
    }

    void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_programmatic || !Player.NaturalDuration.HasTimeSpan) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        Elapsed.Text = Clock(Player.Position);
    }

    void OnVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Player != null) Player.Volume = e.NewValue;
    }
}
