using System.Windows.Controls;
using System.Windows.Media;

namespace LockX.Views;

public partial class PasswordStrengthBar : UserControl
{
    static readonly Brush Red = Freeze(Color.FromRgb(0xE5, 0x48, 0x4D));
    static readonly Brush Orange = Freeze(Color.FromRgb(0xF7, 0x8A, 0x1E));
    static readonly Brush Yellow = Freeze(Color.FromRgb(0xE6, 0xB4, 0x00));
    static readonly Brush Green = Freeze(Color.FromRgb(0x2E, 0xA4, 0x4F));
    static readonly Brush Idle = Freeze(Color.FromArgb(0x33, 0x80, 0x80, 0x80));

    static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public PasswordStrengthBar() => InitializeComponent();

    public static int Score(string password)
    {
        int s = 0;
        if (password.Length >= 8) s++;
        if (password.Length >= 14) s++;
        if (password.Any(char.IsDigit)) s++;
        if (password.Any(char.IsUpper)) s++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) s++;
        return Math.Min(s, 4);
    }

    public void SetPassword(string password)
    {
        int score = Score(password);
        var color = score <= 1 ? Red : score == 2 ? Orange : score == 3 ? Yellow : Green;
        for (int i = 0; i < Bars.Children.Count; i++)
            ((Border)Bars.Children[i]).Background = i < score ? color : Idle;
        Bars.Opacity = password.Length == 0 ? 0 : 1;
    }
}
