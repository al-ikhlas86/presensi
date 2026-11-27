using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenCvSharp;
using Presensi.Logging;
using Presensi.Services;
using Window = System.Windows.Window;

namespace Presensi;

/// <summary>
/// Pendaftaran wajah massal di kiosk: petugas memilih kelompok (kelas siswa, guru, atau pegawai), lalu mendaftarkan orangnya satu per satu. Gaya CINCIN (2026-11-27):
/// orang cukup menaruh wajah di dalam lingkaran, tiga foto depan (front_1..front_3) diambil otomatis dan cincin terisi hijau; tidak ada lagi tengok kiri/kanan.
/// Mode "lanjut otomatis": begitu satu orang lengkap, orang berikutnya (urut abjad, yang sudah lengkap dilewati) langsung dimulai tanpa klik; sebelum itu orang sebelumnya
/// harus menyingkir dari kamera supaya wajahnya tidak ikut terdaftar sebagai orang berikutnya. Ambang kualitas PERSIS sama dengan pendaftaran lewat HP dan web (yaw 12 derajat,
/// wajah >= 120 px, blur >= 30, lalu letak/ukuran di lingkaran dan ditahan sebentar); validasi akhir tetap di server. Frame datang dari MainWindow (thread kamera) lewat <see cref="TerimaFrameMentah"/>;
/// semua pembaruan UI lewat Dispatcher.
/// </summary>
public partial class DaftarWajahWindow : Window
{
    private static readonly TimeSpan JedaAnalisis = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan JedaSetelahSampel = TimeSpan.FromMilliseconds(900);
    private const double TahanMs = 700;    // posisi pas harus bertahan sekian ms (penilaian: Services/GeometriWajah)
    private const int TotalFoto = 3;       // foto depan per orang: front_1..front_3
    private const int JumlahTik = 60;

    private const string PetunjukAwal = "Taruh wajah di dalam lingkaran";

    private readonly WajahApiClient _api;
    private OrangWajah? _orang;            // orang yang sedang didaftarkan (diatur di UI thread sebelum _aktif = true)
    private string _jenis = "siswa";       // jenis kelompok yang sedang dikerjakan: siswa | guru | pegawai
    private string? _orangTerakhir;        // "jenis:id" orang yang terakhir dimulai; berganti orang = wajib ada jeda kosong
    private int _langkah;                  // jumlah foto yang sudah diterima untuk orang ini (0..TotalFoto)
    private volatile bool _aktif;
    private volatile bool _tertutup;
    private volatile bool _tungguKosong;   // menunggu kamera kosong (orang sebelumnya menyingkir) sebelum orang berikutnya dimulai
    private int _sibuk;                    // 0/1: satu analisis/pengiriman sekaligus
    private DateTime _terakhirAnalisis = DateTime.MinValue;
    private readonly PenahanPosisi _penahan = new(TahanMs);
    private volatile int _kotakW;          // ukuran kanvas pratinjau (diperbarui di UI thread, dibaca thread analisis)
    private volatile int _kotakH;
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
        _ = ProsesAsync(jpeg, frame.Width, frame.Height);
    }

    private async Task ProsesAsync(byte[] jpeg, int frameW, int frameH)
    {
        try
        {
            var orang = _orang;
            var jenis = _jenis;
            if (orang == null || _langkah >= TotalFoto) return;

            var analisis = await _api.AnalisisAsync(jpeg).ConfigureAwait(false);
            if (!_aktif || _tertutup) return;
            if (!analisis.Berhasil || analisis.Data is null) { _penahan.Reset(); Tampil(analisis.Pesan ?? "Gagal menghubungi server, mencoba lagi...", 0); return; }
            var a = analisis.Data;

            // Orang sebelumnya baru selesai: tunggu kamera kosong dulu, supaya wajahnya tidak terdaftar atas nama orang berikutnya.
            if (_tungguKosong)
            {
                _penahan.Reset();
                if (a.WajahTerdeteksi) { Tampil($"Menunggu {orang.Nama} maju. Orang sebelumnya silakan menyingkir dari kamera.", 0); return; }
                _tungguKosong = false;
                Tampil($"{orang.Nama}, silakan maju - {PetunjukAwal.ToLowerInvariant()}", 0);
                return;
            }

            if (!a.WajahTerdeteksi) { _penahan.Reset(); Tampil("Wajah belum terlihat - masuk ke lingkaran", 0); return; }
            if (a.JumlahWajah > 1) { _penahan.Reset(); Tampil("Hanya satu orang di dalam lingkaran", 0); return; }

            // Letak di lingkaran dinilai di koordinat TAMPILAN (GeometriWajah), hadap/dongak/miring dari pose 3D, lalu ditahan (tidak buyar karena satu frame buruk).
            var saran = GeometriWajah.Nilai(a, frameW, frameH, _kotakW, _kotakH);
            var (isi, siap) = _penahan.Catat(saran is null, DateTime.UtcNow);
            if (saran is not null) { Tampil(saran, isi); return; }
            if (!siap) { Tampil("Tahan posisi...", isi); return; }
            _penahan.Reset();

            var kirim = await _api.KirimSampelAsync(jenis, orang.Id, "front", _langkah + 1, jpeg).ConfigureAwait(false);
            _jedaHingga = DateTime.UtcNow + JedaSetelahSampel;
            if (!_aktif || _tertutup) return;
            if (!kirim.Berhasil)
            {
                Tampil("Foto ditolak: " + (kirim.Pesan ?? "coba lagi") + " - ulangi posisi", 0);
                return;
            }
            _langkah++;
            if (_langkah >= TotalFoto)
            {
                _aktif = false;
                await await Dispatcher.InvokeAsync(() => OrangSelesaiAsync(orang));
            }
            else
            {
                Tampil($"Bagus! Foto {_langkah} dari {TotalFoto} tersimpan. Tetap di lingkaran.", 0);
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

    /// <summary>Petunjuk + kemajuan menahan posisi (0..1) untuk foto berikutnya; cincin = foto tersimpan + kemajuan tahan.</summary>
    private void Tampil(string petunjuk, double tahan)
    {
        if (_tertutup) return;
        Dispatcher.BeginInvoke(() =>
        {
            PetunjukText.Text = petunjuk;
            LangkahText.Text = _orang is null || !_aktif ? "" : $"{_orang.Nama} - foto {Math.Min(_langkah + 1, TotalFoto)} dari {TotalFoto}";
            GambarCincin(_aktif ? (_langkah + tahan) / TotalFoto : 0, petunjuk);
        });
    }

    // ---- lingkaran + cincin ----

    private double _cincinProgres;
    private string? _cincinPetunjuk;

    private void CincinCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _kotakW = (int)CincinCanvas.ActualWidth;
        _kotakH = (int)CincinCanvas.ActualHeight;
        GambarCincin(_cincinProgres, _cincinPetunjuk);
    }

    private void GambarCincin(double progres, string? petunjuk)
    {
        _cincinProgres = progres;
        _cincinPetunjuk = petunjuk;
        var w = CincinCanvas.ActualWidth;
        var h = CincinCanvas.ActualHeight;
        CincinCanvas.Children.Clear();
        if (!_aktif || w < 50 || h < 50) return;

        var cx = w / 2;
        var cy = h * 0.46;
        var r = Math.Min(w, h) * 0.36;
        var terisi = (int)Math.Round(Math.Max(0, Math.Min(1, progres)) * JumlahTik);
        // Warna lingkaran: hijau bila posisi sedang bagus/ditahan, kuning bila ada saran.
        var bagus = petunjuk is not null && (petunjuk.StartsWith("Tahan") || petunjuk.StartsWith("Bagus"));
        var warna = new SolidColorBrush(bagus ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xF5, 0x9E, 0x0B));

        var lingkar = new Ellipse { Width = r * 2, Height = r * 2, Stroke = warna, StrokeThickness = 3 };
        Canvas.SetLeft(lingkar, cx - r);
        Canvas.SetTop(lingkar, cy - r);
        CincinCanvas.Children.Add(lingkar);

        var hijau = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        var abu = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < JumlahTik; i++)
        {
            var sudut = i / (double)JumlahTik * Math.PI * 2 - Math.PI / 2;
            CincinCanvas.Children.Add(new Line
            {
                X1 = cx + Math.Cos(sudut) * (r + 8), Y1 = cy + Math.Sin(sudut) * (r + 8),
                X2 = cx + Math.Cos(sudut) * (r + 20), Y2 = cy + Math.Sin(sudut) * (r + 20),
                Stroke = i < terisi ? hijau : abu, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
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
        _penahan.Reset();
        _jedaHingga = DateTime.MinValue;
        KelompokCombo.IsEnabled = false;
        OrangList.IsEnabled = false;
        MulaiButton.IsEnabled = false;
        BerhentiButton.IsEnabled = true;
        PetunjukText.Text = _tungguKosong ? $"Menunggu {orang.Nama} maju. Orang sebelumnya silakan menyingkir dari kamera." : PetunjukAwal;
        LangkahText.Text = $"{orang.Nama} - foto 1 dari {TotalFoto}";
        _aktif = true; // terakhir: thread kamera mulai memproses setelah status siap
        GambarCincin(0, PetunjukAwal);
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
        LangkahText.Text = "";
        GambarCincin(0, null);
    }

    /// <summary>Tiga foto diterima: muat ulang status, lalu (mode otomatis) mulai orang berikutnya yang belum lengkap sesuai urutan abjad.</summary>
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
