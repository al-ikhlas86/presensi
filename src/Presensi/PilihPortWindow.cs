using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Presensi.Services;
using Window = System.Windows.Window;

namespace Presensi;

/// <summary>Dialog kecil memilih port scanner bila ada lebih dari satu perangkat serial USB. Dibuat lewat kode karena hanya berisi daftar dan dua tombol.</summary>
public sealed class PilihPortWindow : Window
{
    private readonly ListBox _daftar = new();
    private readonly IReadOnlyList<ScannerPortFinder.PortInfo> _port;

    public PilihPortWindow(IReadOnlyList<ScannerPortFinder.PortInfo> port)
    {
        _port = port;
        Title = "Pilih Scanner";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x1F));

        _daftar.ItemsSource = port.Select(p => p.VidPid is null ? p.PortName : $"{p.PortName}  ({p.VidPid})").ToList();
        _daftar.SelectedIndex = 0;
        _daftar.MaxHeight = 220;

        var ok = new Button { Content = "Sambungkan", Padding = new Thickness(14, 6, 14, 6), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var batal = new Button { Content = "Batal", Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
        ok.Click += (_, _) => { if (_daftar.SelectedIndex >= 0) DialogResult = true; };
        var tombol = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        tombol.Children.Add(ok);
        tombol.Children.Add(batal);

        var isi = new StackPanel { Margin = new Thickness(16) };
        isi.Children.Add(new TextBlock
        {
            Text = "Ada lebih dari satu perangkat serial USB. Pilih yang merupakan scanner kartu (cabut-colok scanner untuk melihat mana yang muncul).",
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        isi.Children.Add(_daftar);
        isi.Children.Add(tombol);
        Content = isi;
    }

    /// <summary>Menampilkan dialog; mengembalikan port terpilih, atau null bila dibatalkan.</summary>
    public ScannerPortFinder.PortInfo? Tampilkan() => ShowDialog() == true && _daftar.SelectedIndex >= 0 ? _port[_daftar.SelectedIndex] : null;
}
