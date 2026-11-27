using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>Kelompok yang bisa didaftarkan wajahnya: kelas siswa, guru satu unit, atau pegawai satu unit (jenis: siswa | guru | pegawai).</summary>
public sealed class KelompokWajah : INotifyPropertyChanged
{
    private int _lengkap;
    public string Kunci { get; set; } = "";
    public string Nama { get; set; } = "";
    public string Jenis { get; set; } = "siswa";
    public int Jumlah { get; set; }
    public int Lengkap
    {
        get => _lengkap;
        set
        {
            if (_lengkap == value) return;
            _lengkap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lengkap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tampil)));
        }
    }
    public string Tampil => $"{Nama}  ({Lengkap}/{Jumlah} lengkap)";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class OrangWajah
{
    public int Id { get; set; }
    public string Nama { get; set; } = "";
    public string? Nis { get; set; }
    /// <summary>belum | sebagian | lengkap (menurut sampel wajah yang tersimpan di server).</summary>
    public string Status { get; set; } = "belum";
    public string Tampil => Nama + (string.IsNullOrWhiteSpace(Nis) ? "" : $"  ({Nis})");
    public string Tanda => Status == "lengkap" ? "✓" : Status == "sebagian" ? "◐" : "○";
    public string Keterangan => Status == "lengkap" ? "lengkap" : Status == "sebagian" ? "sebagian" : "belum";
}

public sealed class HasilDaftar<T>
{
    public bool Berhasil { get; set; }
    public string? Pesan { get; set; }
    public T? Data { get; set; }
}

public sealed class AnalisisWajah
{
    public bool WajahTerdeteksi { get; set; }
    public int JumlahWajah { get; set; }
    public double Yaw { get; set; }
    public double UkuranWajah { get; set; }
    public double? Blur { get; set; }
    /// <summary>Pusat dan lebar kotak wajah sebagai pecahan 0..1 dari frame (null = server lama belum mengirim).</summary>
    public double? Cx { get; set; }
    public double? Cy { get; set; }
    public double? Lebar { get; set; }
}

/// <summary>
/// Pendaftaran wajah massal dari kiosk: daftar kelompok dan orang, analisis pose, dan pengiriman sampel (endpoint /api/kiosk/wajah/*, token kiosk perangkat).
/// Server hanya mengizinkan orang dari unit katalog perangkat ini. Semua panggilan gagal-lunak: galat jaringan menjadi pesan, bukan pengecualian.
/// </summary>
public sealed class WajahApiClient
{
    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _baseUrl;
    private readonly string _token;

    public WajahApiClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
    }

    public async Task<HasilDaftar<List<KelompokWajah>>> DaftarKelompokAsync()
    {
        var (ok, pesan, data) = await KirimAsync(HttpMethod.Get, "wajah/kelompok", null).ConfigureAwait(false);
        if (!ok || data is null) return new HasilDaftar<List<KelompokWajah>> { Berhasil = false, Pesan = pesan };
        var hasil = new List<KelompokWajah>();
        if (data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                hasil.Add(new KelompokWajah
                {
                    Kunci = Teks(el, "kunci") ?? "", Nama = Teks(el, "nama") ?? "-", Jenis = Teks(el, "jenis") ?? "siswa", Jumlah = Angka(el, "jumlah"), Lengkap = Angka(el, "lengkap"),
                });
            }
        }
        return new HasilDaftar<List<KelompokWajah>> { Berhasil = true, Data = hasil };
    }

    public async Task<HasilDaftar<List<OrangWajah>>> DaftarOrangAsync(string kunciKelompok)
    {
        var (ok, pesan, data) = await KirimAsync(HttpMethod.Get, "wajah/orang?kelompok=" + Uri.EscapeDataString(kunciKelompok), null).ConfigureAwait(false);
        if (!ok || data is null) return new HasilDaftar<List<OrangWajah>> { Berhasil = false, Pesan = pesan };
        var hasil = new List<OrangWajah>();
        if (data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                hasil.Add(new OrangWajah { Id = Angka(el, "id"), Nama = Teks(el, "nama") ?? "-", Nis = Teks(el, "nis"), Status = Teks(el, "status") ?? "belum" });
            }
        }
        return new HasilDaftar<List<OrangWajah>> { Berhasil = true, Data = hasil };
    }

    public async Task<HasilDaftar<AnalisisWajah>> AnalisisAsync(byte[] jpeg)
    {
        var (ok, pesan, data) = await KirimAsync(HttpMethod.Post, "wajah/analisis", new { image_base64 = Convert.ToBase64String(jpeg) }).ConfigureAwait(false);
        if (!ok || data is null) return new HasilDaftar<AnalisisWajah> { Berhasil = false, Pesan = pesan };
        var a = new AnalisisWajah();
        if (data.Value.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            a.WajahTerdeteksi = d.TryGetProperty("face_detected", out var fd) && fd.ValueKind == JsonValueKind.True;
            a.JumlahWajah = Angka(d, "face_count");
            a.Yaw = Desimal(d, "yaw") ?? 0;
            a.UkuranWajah = Desimal(d, "face_size") ?? 0;
            a.Blur = Desimal(d, "blur");
            a.Cx = Desimal(d, "cx");
            a.Cy = Desimal(d, "cy");
            a.Lebar = Desimal(d, "w");
        }
        return new HasilDaftar<AnalisisWajah> { Berhasil = true, Data = a };
    }

    /// <summary>Mengirim satu sampel (jenis: siswa | guru | pegawai; angle: front | left | right; nomor 1..3 = front_1..front_3). Berhasil = diterima face-service; ditolak karena kualitas = false + pesan.</summary>
    public async Task<HasilDaftar<bool>> KirimSampelAsync(string jenis, int id, string angle, int nomor, byte[] jpeg)
    {
        var (ok, pesan, _) = await KirimAsync(HttpMethod.Post, "wajah/sampel", new { jenis, id, angle, nomor, image_base64 = Convert.ToBase64String(jpeg) }).ConfigureAwait(false);
        return new HasilDaftar<bool> { Berhasil = ok, Pesan = pesan, Data = ok };
    }

    private async Task<(bool Ok, string? Pesan, JsonElement? Isi)> KirimAsync(HttpMethod metode, string jalur, object? badan)
    {
        try
        {
            using var req = new HttpRequestMessage(metode, $"{_baseUrl}/api/kiosk/{jalur}");
            if (badan != null) req.Content = new StringContent(JsonSerializer.Serialize(badan), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("X-KIOSK-TOKEN", _token);
            req.Headers.TryAddWithoutValidation("X-App-Version", AppVersionInfo.Current);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var teks = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(teks);
            var root = doc.RootElement.Clone();
            var sukses = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (sukses && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "rejected")
            {
                sukses = false; // face-service menjawab 200 walau sampel ditolak (kualitas/sudut kurang)
            }
            if (sukses) return (true, null, root);
            string? pesan = Teks(root, "message");
            if (pesan is null && root.TryGetProperty("data", out var dd) && dd.ValueKind == JsonValueKind.Object) pesan = Teks(dd, "message") ?? Teks(dd, "reason") ?? Teks(dd, "detail");
            return (false, pesan ?? $"Ditolak server (HTTP {(int)resp.StatusCode}).", root);
        }
        catch (Exception ex)
        {
            Log.Error($"[Wajah] Gagal memanggil {jalur}", ex);
            return (false, "Tidak bisa menghubungi server. Cek koneksi internet.", null);
        }
    }

    private static string? Teks(JsonElement el, string nama) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(nama, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Angka(JsonElement el, string nama) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(nama, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double? Desimal(JsonElement el, string nama) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(nama, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
