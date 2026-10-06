using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LockX.Views;

public partial class NoteEditorWindow : Window
{
    public string NoteTitle => TitleBox.Text;
    public string NoteBody => BodyBox.Text;

    public NoteEditorWindow(string title, string body, bool isNew)
    {
        InitializeComponent();
        Title = isNew ? "New note" : "Edit note";
        Heading.Text = Title;
        TitleBox.Text = title;
        BodyBox.Text = body;
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnSave(this, new RoutedEventArgs())), Key.S, ModifierKeys.Control));
        PreviewKeyDown += (_, _) => App.Session?.NoteActivity();
        // A note is private: closing the vault also closes any open editor.
        App.Vault.Locking += CloseOnLock;
        Closed += (_, _) => App.Vault.Locking -= CloseOnLock;
        Loaded += (_, _) => (isNew ? TitleBox : BodyBox).Focus();
    }

    void CloseOnLock() => Dispatcher.Invoke(() => { DialogResult = false; });

    void OnBodyChanged(object sender, TextChangedEventArgs e) => Count.Text = $"{BodyBox.Text.Length:N0} characters";

    void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
