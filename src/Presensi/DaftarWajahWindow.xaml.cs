using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Presensi.Logging;
using Presensi.Services;
using Window = System.Windows.Window;

namespace Presensi;

/// <summary>
/// Pendaftaran wajah siswa massal di kiosk: petugas memilih kelas, lalu mendaftarkan siswa satu per satu (tiga sudut: depan, kiri, kanan).
/// Ambang kualitas dan sampel PERSIS sama dengan pendaftaran lewat HP (yaw 12 derajat, wajah >= 120 px, blur >= 30, tahan 200 ms); validasi akhir tetap di server.
/// Frame datang dari MainWindow (thread kamera) lewat <see cref="TerimaFrameMentah"/>; semua pembaruan UI lewat Dispatcher.
/// </summary>
public partial class DaftarWajahWindow : Window
{
    private const double YawTarget = 12;
    private const double UkuranMin = 120;
    private const double BlurMin = 30;
    private static readonly TimeSpan JedaAnalisis = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan JedaSetelahSampel = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan TahanKondisi = TimeSpan.FromMilliseconds(200);

    private static readonly (string Sudut, string Petunjuk)[] Langkah =
    {
        ("front", "Hadap lurus ke kamera"),
        ("left", "Tengok sedikit ke kiri"),
        ("right", "Tengok sedikit ke kanan"),
    };

    private readonly WajahSiswaApiClient _api;
    private SiswaWajah? _siswa;            // siswa yang sedang didaftarkan (diatur di UI thread sebelum _aktif = true)
    private int _langkah;                  // indeks Langkah berikutnya
    private volatile bool _aktif;
    private volatile bool _tertutup;
    private int _sibuk;                    // 0/1: satu analisis/pengiriman sekaligus
    private DateTime _terakhirAnalisis = DateTime.MinValue;
    private DateTime _kondisiSejak = DateTime.MinValue;
    private DateTime _jedaHingga = DateTime.MinValue;

    public DaftarWajahWindow(WajahSiswaApiClient api)
    {
        _api = api;
        InitializeComponent();
        Loaded += async (_, _) => await MuatKelasAsync();
    }

    // ---- frame dari kamera (thread kamera; Mat dibuang oleh pemanggil) ----

    public void TerimaPreview(BitmapSource bitmap)
    {
        if (_tertutup) return;
        Dispatcher.BeginInvoke(() => KameraImage.Source = bitmap);
    }

    public void TerimaFrameMentah(Mat frame)
    {
        if (!_aktif || _tertutup) return;
        var sekarang = DateTime.UtcNow;
        if (sekarang < _jedaHingga || sekarang - _terakhirAnalisis < JedaAnalisis) return;
        if (Interlocked.CompareExchange(ref _sibuk, 1, 0) != 0) return;
        _terakhirAnalisis = sekarang;
        Cv2.ImEncode(".jpg", frame, out byte[] jpeg);
        _ = ProsesAsync(jpeg);
    }

    private async Task ProsesAsync(byte[] jpeg)
    {
        try
        {
            var siswa = _siswa;
            if (siswa == null || _langkah >= Langkah.Length) return;
            var (sudut, petunjuk) = Langkah[_langkah];

            var analisis = await _api.AnalisisAsync(jpeg).ConfigureAwait(false);
            if (!_aktif || _tertutup) return;
            if (!analisis.Berhasil || analisis.Data is null) { _kondisiSejak = DateTime.MinValue; Tampil(analisis.Pesan ?? "Gagal menghubungi server, mencoba lagi...", null); return; }
            var a = analisis.Data;
            if (!a.WajahTerdeteksi) { _kondisiSejak = DateTime.MinValue; Tampil("Wajah tidak terdeteksi - posisikan di tengah bingkai", null); return; }
            if (a.JumlahWajah > 1) { _kondisiSejak = DateTime.MinValue; Tampil("Terdeteksi lebih dari 1 wajah - hanya satu siswa di depan kamera", null); return; }

            var sudutOk = sudut == "front" ? Math.Abs(a.Yaw) <= YawTarget : sudut == "left" ? a.Yaw <= -YawTarget : a.Yaw >= YawTarget;
            var ukuranOk = a.UkuranWajah >= UkuranMin;
            var blurOk = a.Blur is null || a.Blur >= BlurMin;
            if (!(sudutOk && ukuranOk && blurOk))
            {
                _kondisiSejak = DateTime.MinValue;
                var saran = new System.Collections.Generic.List<string>();
                if (!sudutOk) saran.Add(petunjuk);
                if (!ukuranOk) saran.Add("Dekatkan wajah");
                if (!blurOk) saran.Add("Diam sebentar");
                Tampil(string.Join(" • ", saran), a.Yaw);
                return;
            }

            Tampil("Tahan posisi...", a.Yaw);
            if (_kondisiSejak == DateTime.MinValue) { _kondisiSejak = DateTime.UtcNow; return; }
            if (DateTime.UtcNow - _kondisiSejak < TahanKondisi) return;
            _kondisiSejak = DateTime.MinValue;

            var kirim = await _api.KirimSampelAsync(siswa.Id, sudut, jpeg).ConfigureAwait(false);
            _jedaHingga = DateTime.UtcNow + JedaSetelahSampel;
            if (!_aktif || _tertutup) return;
            if (!kirim.Berhasil)
            {
                Tampil("Sampel ditolak: " + (kirim.Pesan ?? "coba lagi") + " - ulangi posisi", a.Yaw);
                return;
            }
            _langkah++;
            if (_langkah >= Langkah.Length)
            {
                _aktif = false;
                await await Dispatcher.InvokeAsync(() => SiswaSelesaiAsync(siswa));
            }
            else
            {
                Tampil("Sampel diterima. Berikutnya: " + Langkah[_langkah].Petunjuk, null);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Wajah] Gagal memproses frame pendaftaran", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _sibuk, 0);
        }
    }

    private void Tampil(string petunjuk, double? yaw)
    {
        if (_tertutup) return;
        Dispatcher.BeginInvoke(() =>
        {
            PetunjukText.Text = petunjuk;
            YawText.Text = yaw is null ? "" : $"Sudut kepala: {yaw:0}°";
            LangkahText.Text = _siswa is null || !_aktif ? "" : $"{_siswa.Nama} - langkah {Math.Min(_langkah + 1, Langkah.Length)} dari {Langkah.Length}";
        });
    }

    // ---- daftar kelas / siswa ----

    private async Task MuatKelasAsync()
    {
        InfoText.Text = "Memuat daftar kelas...";
        var hasil = await _api.DaftarKelasAsync();
        if (_tertutup) return;
        if (!hasil.Berhasil || hasil.Data is null) { InfoText.Text = hasil.Pesan ?? "Gagal memuat kelas."; return; }
        KelasCombo.ItemsSource = hasil.Data;
        InfoText.Text = hasil.Data.Count == 0
            ? "Belum ada kelas berisi siswa aktif untuk perangkat ini."
            : "Tanda: ✓ lengkap, ◐ sebagian, ○ belum. Orang tua tetap bisa melihat status dan mendaftar ulang dari aplikasi HP.";
    }

    private async void KelasCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KelasCombo.SelectedItem is KelasWajah kelas) await MuatSiswaAsync(kelas, null);
    }

    private async Task MuatSiswaAsync(KelasWajah kelas, int? pilihId)
    {
        var hasil = await _api.DaftarSiswaAsync(kelas.Kunci);
        // Pengguna bisa saja sudah pindah kelas selama menunggu - abaikan hasil basi.
        if (_tertutup || KelasCombo.SelectedItem != kelas) return;
        if (!hasil.Berhasil || hasil.Data is null) { InfoText.Text = hasil.Pesan ?? "Gagal memuat siswa."; return; }
        SiswaList.ItemsSource = hasil.Data;
        if (pilihId is int id) SiswaList.SelectedItem = hasil.Data.FirstOrDefault(s => s.Id == id);
    }

    private void SiswaList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        MulaiButton.IsEnabled = !_aktif && SiswaList.SelectedItem is SiswaWajah;
    }

    // ---- kendali ----

    private void Mulai_Click(object sender, RoutedEventArgs e)
    {
        if (SiswaList.SelectedItem is not SiswaWajah siswa || _aktif) return;
        _siswa = siswa;
        _langkah = 0;
        _kondisiSejak = DateTime.MinValue;
        _jedaHingga = DateTime.MinValue;
        KelasCombo.IsEnabled = false;
        SiswaList.IsEnabled = false;
        MulaiButton.IsEnabled = false;
        BerhentiButton.IsEnabled = true;
        PetunjukText.Text = Langkah[0].Petunjuk;
        LangkahText.Text = $"{siswa.Nama} - langkah 1 dari {Langkah.Length}";
        _aktif = true; // terakhir: thread kamera mulai memproses setelah status siap
    }

    private void Berhenti_Click(object sender, RoutedEventArgs e) => Selesai("Dihentikan. Pilih siswa lalu tekan Mulai.");

    private void Selesai(string pesan)
    {
        _aktif = false;
        KelasCombo.IsEnabled = true;
        SiswaList.IsEnabled = true;
        BerhentiButton.IsEnabled = false;
        MulaiButton.IsEnabled = SiswaList.SelectedItem is SiswaWajah;
        PetunjukText.Text = pesan;
        YawText.Text = "";
        LangkahText.Text = "";
    }

    /// <summary>Tiga sampel diterima: muat ulang status kelas, lalu pilih siswa berikutnya yang belum lengkap.</summary>
    private async Task SiswaSelesaiAsync(SiswaWajah siswa)
    {
        Selesai($"✓ {siswa.Nama} selesai didaftarkan. Pilih siswa berikutnya.");
        if (KelasCombo.SelectedItem is not KelasWajah kelas) return;
        await MuatSiswaAsync(kelas, null);
        if (SiswaList.ItemsSource is not System.Collections.Generic.List<SiswaWajah> baru) return;
        var idx = baru.FindIndex(s => s.Id == siswa.Id);
        var berikut = baru.Skip(idx + 1).FirstOrDefault(s => s.Status != "lengkap") ?? baru.FirstOrDefault(s => s.Status != "lengkap");
        if (berikut is null) PetunjukText.Text = $"✓ {siswa.Nama} selesai. Semua siswa di kelas ini sudah lengkap.";
        SiswaList.SelectedItem = berikut ?? baru.FirstOrDefault(s => s.Id == siswa.Id);
    }

    private void Tutup_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _aktif = false;
        _tertutup = true;
    }
}
