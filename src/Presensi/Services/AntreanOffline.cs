using System.IO;
using System.Text.Json;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Antrean scan kartu saat internet putus (2026-11-28). Scan yang tidak bisa dikirim disimpan di berkas lokal beserta WAKTU SCAN ASLI, lalu dikirim ulang berurutan
/// begitu internet kembali; server memakai waktu asli itu dan menolak yang sudah tercatat (jadi pengiriman ulang aman). Berkas ditulis ulang secara atomik tiap
/// perubahan (antrean kecil), sehingga PC mati mendadak tidak merusak/menghilangkan antrean. Entri yang lebih tua dari batas server dibuang (server menolaknya).
/// Pengenalan WAJAH tidak masuk antrean: butuh server (face-service), jadi saat offline petugas memakai kartu.
/// </summary>
public sealed class AntreanOffline
{
    public sealed class Entri
    {
        public string Kode { get; set; } = "";
        public string Mode { get; set; } = "masuk";
        public DateTime WaktuUtc { get; set; }
    }

    // Server menolak scan > 3 hari; dibuang sedikit lebih awal supaya tidak ditolak di tengah jalan.
    private static readonly TimeSpan MaksUmur = TimeSpan.FromHours(70);
    private const int MaksEntri = 5000;

    private readonly object _kunci = new();
    private readonly string _jalur;
    private List<Entri> _daftar = new();

    public AntreanOffline(string? jalur = null)
    {
        _jalur = jalur ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Presensi", "antrean-offline.json");
        Muat();
    }

    public int Jumlah { get { lock (_kunci) return _daftar.Count; } }

    public void Tambah(string kode, AttendanceMode mode, DateTime waktuUtc)
    {
        lock (_kunci)
        {
            _daftar.Add(new Entri { Kode = kode, Mode = mode == AttendanceMode.Pulang ? "pulang" : "masuk", WaktuUtc = waktuUtc });
            if (_daftar.Count > MaksEntri) _daftar.RemoveRange(0, _daftar.Count - MaksEntri); // jangan tumbuh tanpa batas bila offline berminggu-minggu
            Simpan();
        }
    }

    /// <summary>Entri tertua (urutan scan dipertahankan: masuk selalu terkirim sebelum pulang).</summary>
    public Entri? Tertua()
    {
        lock (_kunci) return _daftar.Count > 0 ? _daftar[0] : null;
    }

    public void Hapus(Entri entri)
    {
        lock (_kunci)
        {
            if (_daftar.Remove(entri)) Simpan();
        }
    }

    /// <summary>Membuang entri yang sudah terlalu tua untuk diterima server; mengembalikan jumlah yang dibuang.</summary>
    public int BuangKedaluwarsa()
    {
        lock (_kunci)
        {
            var batas = DateTime.UtcNow - MaksUmur;
            var dibuang = _daftar.RemoveAll(e => e.WaktuUtc < batas);
            if (dibuang > 0)
            {
                Log.Warn($"[Antrean] {dibuang} scan kedaluwarsa (lebih dari 70 jam) dibuang tanpa terkirim.");
                Simpan();
            }
            return dibuang;
        }
    }

    private void Muat()
    {
        lock (_kunci)
        {
            try
            {
                if (!File.Exists(_jalur)) return;
                var terbaca = JsonSerializer.Deserialize<List<Entri>>(File.ReadAllText(_jalur));
                _daftar = (terbaca ?? new List<Entri>()).Where(e => !string.IsNullOrWhiteSpace(e.Kode)).OrderBy(e => e.WaktuUtc).ToList();
            }
            catch (Exception ex)
            {
                // Berkas rusak: simpan sebagai .rusak (untuk diperiksa) dan mulai kosong, jangan menghentikan aplikasi.
                Log.Error("[Antrean] berkas antrean rusak, dipindahkan dan dimulai kosong", ex);
                try { File.Move(_jalur, _jalur + ".rusak-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { /* abaikan */ }
                _daftar = new List<Entri>();
            }
        }
    }

    private void Simpan()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_jalur)!);
            var sementara = _jalur + ".tmp";
            File.WriteAllText(sementara, JsonSerializer.Serialize(_daftar));
            if (File.Exists(_jalur)) File.Delete(_jalur);
            File.Move(sementara, _jalur);
        }
        catch (Exception ex)
        {
            Log.Error("[Antrean] gagal menyimpan antrean offline", ex);
        }
    }
}
