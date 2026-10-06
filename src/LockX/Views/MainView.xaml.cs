using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using LockX.Core.Data;

namespace LockX.Views;

/// <summary>The unlocked vault: kind sidebar, item browser, detail pane and the full-window viewer.</summary>
public partial class MainView : UserControl
{
    readonly Vault _vault = App.Vault;
    readonly Dictionary<Guid, ItemVM> _vms = new();
    readonly ObservableCollection<ItemVM> _visible = new();
    readonly List<KindVM> _kinds = Enum.GetValues<ItemKind>().Select(k => new KindVM(k)).ToList();
    ItemKind _kind = ItemKind.Image;
    bool _syncing;
    Window? _win;

    public MainView()
    {
        InitializeComponent();
        Items.ItemsSource = _visible;
        Sidebar.ItemsSource = _kinds;
        Sidebar.SelectedIndex = 0;

        Viewer.CloseRequested += () => { Viewer.Hide(); Items.Focus(); };
        Viewer.EditNoteRequested += vm => { Viewer.Hide(); EditNote(vm); };
        Viewer.ExportRequested += vm => ExportItem(vm);

        // Window-level shortcuts so they work wherever the keyboard focus happens to be.
        var shortcuts = new[]
        {
            new KeyBinding(new RelayCommand(OnAddFiles), Key.O, ModifierKeys.Control),
            new KeyBinding(new RelayCommand(OnNewNote), Key.N, ModifierKeys.Control),
            new KeyBinding(new RelayCommand(OnLock), Key.L, ModifierKeys.Control | ModifierKeys.Shift),
            new KeyBinding(new RelayCommand(OnSettings), Key.OemComma, ModifierKeys.Control),
        };

        Loaded += (_, _) =>
        {
            _win = Window.GetWindow(this);
            if (_win != null)
                foreach (var b in shortcuts) _win.InputBindings.Add(b);
            _vault.ItemsChanged += OnVaultChanged;
            App.Session.Ticked += OnTick;
            OnVaultChanged();
            OnTick();
            Items.Focus();
        };
        Unloaded += (_, _) =>
        {
            // The visual tree is already detached here, so use the window captured in Loaded.
            if (_win != null)
                foreach (var b in shortcuts) _win.InputBindings.Remove(b);
            _vault.ItemsChanged -= OnVaultChanged;
            App.Session.Ticked -= OnTick;
        };
    }

    // ---- Model sync ---------------------------------------------------------------

    IEnumerable<ItemVM> Selected => Items.SelectedItems.Cast<ItemVM>();

    void OnVaultChanged()
    {
        if (_vault.State != VaultState.Unlocked) return;

        var live = _vault.Items;
        var liveIds = live.Select(i => i.Id).ToHashSet();
        foreach (var gone in _vms.Keys.Where(id => !liveIds.Contains(id)).ToList()) _vms.Remove(gone);
        foreach (var item in live)
        {
            if (_vms.TryGetValue(item.Id, out var vm)) vm.Refresh(item);
            else _vms[item.Id] = new ItemVM(item);
        }
        foreach (var k in _kinds) k.Count = _vault.CountOf(k.Kind);
        RebuildVisible();
    }

    void RebuildVisible()
    {
        var term = Search.Text.Trim();
        var want = _vault.ItemsOf(_kind)
            .Where(i => term.Length == 0 || i.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .Select(i => _vms[i.Id]).ToList();

        var selectedIds = Selected.Select(v => v.Id).ToHashSet();
        _syncing = true;
        if (!want.SequenceEqual(_visible))
        {
            _visible.Clear();
            foreach (var vm in want) _visible.Add(vm);
        }
        Items.SelectedItems.Clear();
        foreach (var vm in _visible.Where(v => selectedIds.Contains(v.Id))) Items.SelectedItems.Add(vm);
        _syncing = false;

        bool grid = _kind is ItemKind.Image or ItemKind.Video;
        Items.ItemTemplate = (DataTemplate)Resources[grid ? "GridTemplate" : "RowTemplate"];
        Items.ItemsPanel = (ItemsPanelTemplate)Resources[grid ? "WrapPanelTemplate" : "StackPanelTemplate"];

        UpdateEmpty();
        UpdateDetail();
    }

    void UpdateEmpty()
    {
        Empty.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_visible.Count != 0) return;
        EmptyGlyph.Text = Glyphs.For(_kind);
        bool searching = Search.Text.Trim().Length > 0;
        EmptyTitle.Text = searching ? "No matches" : $"No {_kind.Title().ToLowerInvariant()} yet";
        EmptyHint.Text = searching ? "Try a different search."
            : _kind == ItemKind.Note ? "Create a note with the New note button."
            : "Drag files here, or use Add files.";
    }

    void OnTick()
    {
        var s = App.Session.SecondsUntilLock;
        Countdown.Text = s is { } n ? $"{n / 60}:{n % 60:00}" : "";
    }

    // ---- Selection / detail -------------------------------------------------------

    void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Sidebar.SelectedItem is not KindVM k || k.Kind == _kind) return;
        _kind = k.Kind;
        Items.SelectedItems.Clear();
        Viewer.Hide();
        RebuildVisible();
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_vault.State == VaultState.Unlocked) RebuildVisible();
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) UpdateDetail();
    }

    void UpdateDetail()
    {
        int n = Items.SelectedItems.Count;
        DeleteButton.IsEnabled = n > 0;
        ExportButton.IsEnabled = n == 1;

        if (n != 1)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            NoSelection.Visibility = Visibility.Visible;
            NoSelection.Text = n == 0 ? "Select an item" : $"{n} items selected";
            return;
        }

        var vm = (ItemVM)Items.SelectedItem;
        var item = vm.Item;
        NoSelection.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        DetailName.Text = item.Name;
        PosterImage.Source = vm.Thumbnail;
        PosterGlyph.Text = Glyphs.For(item.Kind);
        PosterGlyph.Visibility = vm.Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
        PosterPlay.Visibility = item.Kind == ItemKind.Video ? Visibility.Visible : Visibility.Collapsed;
        EditButton.Visibility = item.Kind == ItemKind.Note ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Content = item.Kind switch
        {
            ItemKind.Image => "View",
            ItemKind.Video => "Play",
            _ => "Open",
        };

        var facts = new List<(string, string)> { ("Kind", item.Kind.ToString()), ("Size", item.SizeText) };
        if (item.PixelWidth is { } w && item.PixelHeight is { } h) facts.Add(("Dimensions", $"{w} × {h}"));
        if (item.DurationText is { } d) facts.Add(("Duration", d));
        facts.Add(("Added", item.AddedAt.ToLocalTime().ToString("g")));
        facts.Add(("Modified", item.ModifiedAt.ToLocalTime().ToString("g")));

        Facts.Children.Clear();
        Facts.RowDefinitions.Clear();
        for (int i = 0; i < facts.Count; i++)
        {
            Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = facts[i].Item1, Margin = new Thickness(0, 3, 0, 3) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var value = new TextBlock { Text = facts[i].Item2, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            Facts.Children.Add(label);
            Facts.Children.Add(value);
        }
    }

    // ---- Opening ------------------------------------------------------------------

    void OpenViewer(ItemVM vm)
    {
        App.Session.NoteActivity();
        Viewer.Show(_visible.ToList(), vm);
    }

    void OnItemsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d)?.DataContext is ItemVM vm)
            OpenViewer(vm);
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    void OnItemsKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Items.SelectedItem is ItemVM vm: OpenViewer(vm); break;
            case Key.Delete: OnDelete(sender, e); break;
            case Key.F2 when Items.SelectedItem is ItemVM vm: RenameItem(vm); break;
            default: return;
        }
        e.Handled = true;
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Items.SelectedItem is ItemVM vm) OpenViewer(vm);
    }

    void OnPosterClick(object sender, MouseButtonEventArgs e) => OnOpen(sender, e);

    // Right-click selects the item under the pointer (unless it is already part of the selection),
    // so the menu always acts on what the user sees highlighted.
    void OnItemsRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d)?.DataContext is ItemVM vm
            && !Items.SelectedItems.Contains(vm))
        {
            Items.SelectedItems.Clear();
            Items.SelectedItems.Add(vm);
        }
    }

    void OnMenuOpen(object s, RoutedEventArgs e) { if (Items.SelectedItem is ItemVM vm) OpenViewer(vm); }
    void OnMenuRename(object s, RoutedEventArgs e) { if (Items.SelectedItem is ItemVM vm) RenameItem(vm); }
    void OnMenuExport(object s, RoutedEventArgs e) { if (Items.SelectedItems.Count == 1) ExportItem((ItemVM)Items.SelectedItem); }
    void OnMenuDelete(object s, RoutedEventArgs e) => OnDelete(s, e);

    // ---- Toolbar actions ----------------------------------------------------------

    void OnLock() => _vault.Lock();
    void OnLock(object sender, RoutedEventArgs e) => _vault.Lock();

    void OnSettings() => new SettingsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    void OnSettings(object sender, RoutedEventArgs e) => OnSettings();

    void OnAddFiles()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Add files to the vault", CheckFileExists = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = ImportAsync(dlg.FileNames);
    }
    void OnAddFiles(object sender, RoutedEventArgs e) => OnAddFiles();

    void OnNewNote() => EditNote(null);
    void OnNewNote(object sender, RoutedEventArgs e) => OnNewNote();

    void EditNote(ItemVM? vm)
    {
        App.Session.NoteActivity();
        string title = "", body = "";
        if (vm != null)
        {
            try { title = vm.Name; body = _vault.NoteText(vm.Item); }
            catch (Exception ex) { ShowError(ex.Message); return; }
        }
        var dlg = new NoteEditorWindow(title, body, vm == null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (vm != null) _vault.UpdateNote(vm.Item, dlg.NoteTitle, dlg.NoteBody);
            else
            {
                var created = _vault.CreateNote(dlg.NoteTitle, dlg.NoteBody);
                Sidebar.SelectedItem = _kinds.First(k => k.Kind == ItemKind.Note);
                Items.SelectedItems.Clear();
                if (_vms.TryGetValue(created.Id, out var cvm)) Items.SelectedItems.Add(cvm);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    void OnEditNote(object sender, RoutedEventArgs e)
    {
        if (Items.SelectedItem is ItemVM vm && vm.Item.Kind == ItemKind.Note) EditNote(vm);
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        var chosen = Selected.ToList();
        if (chosen.Count == 0) return;
        App.Session.NoteActivity();
        var text = chosen.Count == 1 ? "Delete this item?" : $"Delete {chosen.Count} items?";
        var ok = MessageBox.Show(Window.GetWindow(this), text + "\n\nThe encrypted copy is removed permanently. This cannot be undone.",
                                 "Lock X", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            Viewer.Hide();
            _vault.Delete(chosen.Select(v => v.Id));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        if (Items.SelectedItems.Count == 1) ExportItem((ItemVM)Items.SelectedItem);
    }

    async void ExportItem(ItemVM vm)
    {
        App.Session.NoteActivity();
        var dlg = new SaveFileDialog
        {
            FileName = vm.Name,
            Title = "Export (the exported file is NOT encrypted)",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await _vault.ExportAsync(vm.Item, dlg.FileName); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    void RenameItem(ItemVM vm)
    {
        var dlg = new PromptWindow("Rename", "Name", vm.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Value.Trim().Length > 0) _vault.Rename(vm.Item, dlg.Value);
    }

    // ---- Import -------------------------------------------------------------------

    async Task ImportAsync(IEnumerable<string> paths)
    {
        App.Session.NoteActivity();
        var files = Expand(paths).ToList();
        if (files.Count == 0) return;

        Busy.Visibility = Visibility.Visible;
        var failures = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            BusyText.Text = files.Count == 1 ? "Encrypting…" : $"Encrypting {i + 1} of {files.Count}…";
            BusyBar.Value = (double)i / files.Count;
            try { await _vault.ImportFileAsync(files[i]); }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(files[i])}: {ex.Message}"); }
            // Imported files land in the section that matches their type.
            App.Session.NoteActivity();
        }
        Busy.Visibility = Visibility.Collapsed;
        if (failures.Count > 0) ShowError(string.Join("\n", failures));
    }

    /// <summary>Folders dropped on the window are imported one level deep.</summary>
    static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> children;
                try { children = Directory.EnumerateFiles(p).Where(f => (File.GetAttributes(f) & FileAttributes.Hidden) == 0).ToList(); }
                catch { continue; }
                foreach (var c in children) yield return c;
            }
            else if (File.Exists(p)) yield return p;
        }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && Busy.Visibility != Visibility.Visible;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void OnDragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = ImportAsync(paths);
    }

    void ShowError(string message) =>
        MessageBox.Show(Window.GetWindow(this), message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
}

sealed class RelayCommand : ICommand
{
    readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run();
}
