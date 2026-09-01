using System.Windows;
using Window = System.Windows.Window;

namespace Presensi;

/// <summary>
/// Popup kecil ditampilkan SEBELUM Presensi (MainWindow) sungguhan dibuka -
/// diminta user 2026-09-01 supaya kalau app dibuka SAAT ada update yang lagi
/// diunduh (mis. sesi sebelumnya ditutup di tengah unduhan), yang muncul
/// PERTAMA cukup popup kecil "mohon tunggu", BUKAN langsung jendela kamera
/// besar - baru pindah ke Presensi sungguhan setelah beres. Lihat
/// App.xaml.cs (RunStartupSequenceAsync) utk alur lengkapnya.
/// </summary>
public partial class UpdateCheckWindow : Window
{
    public UpdateCheckWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string? text)
    {
        StatusText.Text = string.IsNullOrEmpty(text) ? "Memeriksa pembaruan..." : text;
    }
}
