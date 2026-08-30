using System.Windows;
using Presensi.Services;
using Window = System.Windows.Window;

namespace Presensi;

public partial class SettingsWindow : Window
{
    public AppConfig Config { get; }
    public bool Saved { get; private set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        Config = config;
        JamMasukBox.Text = config.JamMasuk;
        JamPulangBox.Text = config.JamPulang;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ScheduleService.TryParseJam(JamMasukBox.Text, out _))
        {
            ShowError("Jam Masuk tidak valid - pakai format HH:mm, contoh 06:30.");
            return;
        }
        if (!ScheduleService.TryParseJam(JamPulangBox.Text, out _))
        {
            ShowError("Jam Pulang tidak valid - pakai format HH:mm, contoh 15:00.");
            return;
        }

        Config.JamMasuk = JamMasukBox.Text.Trim();
        Config.JamPulang = JamPulangBox.Text.Trim();
        AppConfig.Save(Config);
        Saved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
