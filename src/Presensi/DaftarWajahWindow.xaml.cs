using System;
using System.Collections.Generic;
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
/// Pendaftaran wajah massal di kiosk: petugas memilih kelompok (kelas siswa, guru, atau pegawai), lalu mendaftarkan orangnya satu per satu (tiga sudut: depan, kiri, kanan).
/// Mode "lanjut otomatis": begitu satu orang lengkap, orang berikutnya (urut abjad, yang sudah lengkap dilewati) langsung dimulai tanpa klik; sebelum itu orang sebelumnya
/// harus menyingkir dari kamera supaya wajahnya tidak ikut terdaftar sebagai orang berikutnya. Ambang kualitas PERSIS sama dengan pendaftaran lewat HP (yaw 12 derajat,
/// wajah >= 120 px, blur >= 30, tahan 200 ms); validasi akhir tetap di server. Frame datang dari MainWindow (thread kamera) lewat <see cref="TerimaFrameMentah"/>;
/// semua pembaruan UI lewat Dispatcher.
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

    private readonly WajahApiClient _api;
    private OrangWajah? _orang;            // orang yang sedang didaftarkan (diatur di UI thread sebelum _aktif = true)
    private string _jenis = "siswa";       // jenis kelompok yang sedang dikerjakan: siswa | guru | pegawai
    private string? _orangTerakhir;        // "jenis:id" orang yang terakhir dimulai; berganti orang = wajib ada jeda kosong
    private int _langkah;                  // indeks Langkah berikutnya
    private volatile bool _aktif;
    private volatile bool _tertutup;
    private volatile bool _tungguKosong;   // menunggu kamera kosong (orang sebelumnya menyingkir) sebelum orang berikutnya dimulai
    private int _sibuk;                    // 0/1: satu analisis/pengiriman sekaligus
    private DateTime _terakhirAnalisis = DateTime.MinValue;
    private DateTime _kondisiSejak = DateTime.MinValue;
    private DateTime _jedaHingga = DateTime.MinValue;

    public DaftarWajahWindow(WajahApiClient api)
    {
        _api = api;
        InitializeComponent();
        Loaded += async (_, _) => await MuatKelompokAsync();
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
            var orang = _orang;
            var jenis = _jenis;
            if (orang == null || _langkah >= Langkah.Length) return;
            var (sudut, petunjuk) = Langkah[_langkah];

            var analisis = await _api.AnalisisAsync(jpeg).ConfigureAwait(false);
            if (!_aktif || _tertutup) return;
            if (!analisis.Berhasil || analisis.Data is null) { _kondisiSejak = DateTime.MinValue; Tampil(analisis.Pesan ?? "Gagal menghubungi server, mencoba lagi...", null); return; }
            var a = analisis.Data;

            // Orang sebelumnya baru selesai: tunggu kamera kosong dulu, supaya wajahnya tidak terdaftar atas nama orang berikutnya.
            if (_tungguKosong)
            {
                _kondisiSejak = DateTime.MinValue;
                if (a.WajahTerdeteksi) { Tampil($"Menunggu {orang.Nama} maju. Orang sebelumnya silakan menyingkir dari kamera.", null); return; }
                _tungguKosong = false;
                Tampil($"{orang.Nama}, silakan maju - {petunjuk}", null);
                return;
            }

            if (!a.WajahTerdeteksi) { _kondisiSejak = DateTime.MinValue; Tampil("Wajah tidak terdeteksi - posisikan di tengah bingkai", null); return; }
            if (a.JumlahWajah > 1) { _kondisiSejak = DateTime.MinValue; Tampil("Terdeteksi lebih dari 1 wajah - hanya satu orang di depan kamera", null); return; }

            var sudutOk = sudut == "front" ? Math.Abs(a.Yaw) <= YawTarget : sudut == "left" ? a.Yaw <= -YawTarget : a.Yaw >= YawTarget;
            var ukuranOk = a.UkuranWajah >= UkuranMin;
            var blurOk = a.Blur is null || a.Blur >= BlurMin;
            if (!(sudutOk && ukuranOk && blurOk))
            {
                _kondisiSejak = DateTime.MinValue;
                var saran = new List<string>();
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

            var kirim = await _api.KirimSampelAsync(jenis, orang.Id, sudut, jpeg).ConfigureAwait(false);
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
                await await Dispatcher.InvokeAsync(() => OrangSelesaiAsync(orang));
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
            LangkahText.Text = _orang is null || !_aktif ? "" : $"{_orang.Nama} - langkah {Math.Min(_langkah + 1, Langkah.Length)} dari {Langkah.Length}";
        });
    }

    // ---- daftar kelompok / orang ----

    private async Task MuatKelompokAsync()
    {
        InfoText.Text = "Memuat daftar kelompok...";
        var hasil = await _api.DaftarKelompokAsync();
        if (_tertutup) return;
        if (!hasil.Berhasil || hasil.Data is null) { InfoText.Text = hasil.Pesan ?? "Gagal memuat kelompok."; return; }
        KelompokCombo.ItemsSource = hasil.Data;
        InfoText.Text = hasil.Data.Count == 0
            ? "Belum ada orang aktif untuk perangkat ini."
            : "Hijau = lengkap, kuning = sebagian, abu-abu = belum. Orang tua tetap bisa melihat status dan mendaftar ulang dari aplikasi HP.";
    }

    private async void KelompokCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KelompokCombo.SelectedItem is KelompokWajah kelompok) await MuatOrangAsync(kelompok);
    }

    private async Task MuatOrangAsync(KelompokWajah kelompok)
    {
        var hasil = await _api.DaftarOrangAsync(kelompok.Kunci);
        // Pengguna bisa saja sudah pindah kelompok selama menunggu - abaikan hasil basi.
        if (_tertutup || KelompokCombo.SelectedItem != kelompok) return;
        if (!hasil.Berhasil || hasil.Data is null) { InfoText.Text = hasil.Pesan ?? "Gagal memuat daftar."; return; }
        _jenis = kelompok.Jenis;
        OrangList.ItemsSource = hasil.Data;
        var lengkap = hasil.Data.Count(o => o.Status == "lengkap");
        var sebagian = hasil.Data.Count(o => o.Status == "sebagian");
        kelompok.Jumlah = hasil.Data.Count;
        kelompok.Lengkap = lengkap;
        RingkasanText.Text = $"Lengkap {lengkap} • Sebagian {sebagian} • Belum {hasil.Data.Count - lengkap - sebagian} (dari {hasil.Data.Count})";
    }

    private void OrangList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        MulaiButton.IsEnabled = !_aktif && OrangList.SelectedItem is OrangWajah;
    }

    // ---- kendali ----

    private void Mulai_Click(object sender, RoutedEventArgs e)
    {
        if (OrangList.SelectedItem is not OrangWajah orang || _aktif) return;
        if (orang.Status == "lengkap"
            && MessageBox.Show(this, $"{orang.Nama} sudah terdaftar lengkap. Daftarkan ulang (menggantikan yang lama)?", "Sudah lengkap", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        MulaiUntuk(orang);
    }

    private void MulaiUntuk(OrangWajah orang)
    {
        var kunci = $"{_jenis}:{orang.Id}";
        _tungguKosong = _orangTerakhir != null && _orangTerakhir != kunci; // berganti orang: tunggu kamera kosong dulu
        _orangTerakhir = kunci;
        _orang = orang;
        _langkah = 0;
        _kondisiSejak = DateTime.MinValue;
        _jedaHingga = DateTime.MinValue;
        KelompokCombo.IsEnabled = false;
        OrangList.IsEnabled = false;
        MulaiButton.IsEnabled = false;
        BerhentiButton.IsEnabled = true;
        PetunjukText.Text = _tungguKosong ? $"Menunggu {orang.Nama} maju. Orang sebelumnya silakan menyingkir dari kamera." : Langkah[0].Petunjuk;
        LangkahText.Text = $"{orang.Nama} - langkah 1 dari {Langkah.Length}";
        YawText.Text = "";
        _aktif = true; // terakhir: thread kamera mulai memproses setelah status siap
    }

    private void Berhenti_Click(object sender, RoutedEventArgs e) => Selesai("Dihentikan. Pilih orang lalu tekan Mulai.");

    private void Selesai(string pesan)
    {
        _aktif = false;
        KelompokCombo.IsEnabled = true;
        OrangList.IsEnabled = true;
        BerhentiButton.IsEnabled = false;
        MulaiButton.IsEnabled = OrangList.SelectedItem is OrangWajah;
        PetunjukText.Text = pesan;
        YawText.Text = "";
        LangkahText.Text = "";
    }

    /// <summary>Tiga sampel diterima: muat ulang status, lalu (mode otomatis) mulai orang berikutnya yang belum lengkap sesuai urutan abjad.</summary>
    private async Task OrangSelesaiAsync(OrangWajah orang)
    {
        Selesai($"✓ {orang.Nama} selesai didaftarkan.");
        if (KelompokCombo.SelectedItem is not KelompokWajah kelompok) return;
        await MuatOrangAsync(kelompok);
        if (_tertutup || _aktif || OrangList.ItemsSource is not List<OrangWajah> baru) return;
        var idx = baru.FindIndex(o => o.Id == orang.Id);
        var berikut = baru.Skip(idx + 1).FirstOrDefault(o => o.Status != "lengkap") ?? baru.FirstOrDefault(o => o.Status != "lengkap");
        if (berikut is null)
        {
            PetunjukText.Text = $"✓ {orang.Nama} selesai. Semua di kelompok ini sudah lengkap.";
            OrangList.SelectedItem = baru.FirstOrDefault(o => o.Id == orang.Id);
            return;
        }
        if (berikut.Id == orang.Id)
        {
            // Server belum mencatat tiga sampel orang ini: jangan mengulang diam-diam tanpa akhir.
            PetunjukText.Text = $"{orang.Nama} belum tercatat lengkap di server. Periksa koneksi lalu daftarkan ulang.";
            OrangList.SelectedItem = berikut;
            return;
        }
        OrangList.SelectedItem = berikut;
        if (LanjutOtomatisCheck.IsChecked == true) MulaiUntuk(berikut);
        else PetunjukText.Text = $"✓ {orang.Nama} selesai. Pilih orang berikutnya.";
    }

    private void Tutup_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _aktif = false;
        _tertutup = true;
    }
}
