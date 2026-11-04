using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamAchievementUnlocker;

internal static class DesktopTheme
{
    public static bool IsDark(string mode)
    {
        if (mode != "System") return mode == "Dark";
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }
    public static void Apply(Control root, string mode)
    {
        bool dark = IsDark(mode);
        Color background = dark ? Color.Black : Color.FromArgb(246, 247, 251);
        Color surface = dark ? Color.Black : Color.White;
        Color foreground = dark ? Color.FromArgb(233, 236, 242) : Color.FromArgb(31, 36, 55);
        void Paint(Control c)
        {
            c.BackColor = c is TextBoxBase or ListBox or ComboBox or Button ? surface : background;
            c.ForeColor = foreground;
            if (c is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = surface;
                combo.ForeColor = foreground;
            }
            NativeTheme.Apply(c, dark);
            if (c is ThemeTabControl tabs) tabs.Dark = dark;
            if (c is ThemeComboBox themedCombo) themedCombo.Dark = dark;
            if (c is ThemeTextBox themedText) themedText.Dark = dark;
            if (c is ThemeButton themedButton) themedButton.Dark = dark;
            if (c is Button b) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = dark ? Color.FromArgb(78, 84, 101) : Color.FromArgb(185, 190, 205); }
            if (c is DataGridView grid)
            {
                grid.BackgroundColor = surface; grid.GridColor = dark ? Color.FromArgb(48, 48, 48) : Color.LightGray;
                grid.DefaultCellStyle.BackColor = surface; grid.DefaultCellStyle.ForeColor = foreground;
                grid.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(38, 38, 38) : Color.FromArgb(225, 228, 250);
                grid.DefaultCellStyle.SelectionForeColor = foreground;
                grid.AlternatingRowsDefaultCellStyle.BackColor = dark ? Color.FromArgb(8, 8, 8) : Color.FromArgb(249, 250, 253);
                grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? Color.FromArgb(17, 17, 17) : Color.FromArgb(233, 235, 244);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = foreground;
                grid.RowHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
                grid.RowHeadersDefaultCellStyle.SelectionForeColor = foreground;
                grid.BorderStyle = BorderStyle.None;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                foreach (Control child in grid.Controls) NativeTheme.Apply(child, dark);
                grid.Invalidate(); return;
            }
            foreach (Control child in c.Controls) Paint(child);
        }
        Paint(root);
    }
}
