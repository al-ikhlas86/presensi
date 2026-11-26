using System.Net.Http;
using System.Text;
using System.Text.Json;
using Presensi.Logging;

namespace Presensi.Services;

public sealed class KelasWajah
{
    public string Kunci { get; set; } = "";
    public string Nama { get; set; } = "";
    public int Jumlah { get; set; }
    public override string ToString() => $"{Nama}  ({Jumlah} siswa)";
}

public sealed class SiswaWajah
{
    public int Id { get; set; }
    public string Nama { get; set; } = "";
    public string? Nis { get; set; }
    /// <summary>belum | sebagian | lengkap (menurut sampel wajah yang tersimpan di server).</summary>
    public string Status { get; set; } = "belum";
    public string Tampil => $"{(Status == "lengkap" ? "✓" : Status == "sebagian" ? "◐" : "○")}  {Nama}" + (string.IsNullOrWhiteSpace(Nis) ? "" : $"  ({Nis})");
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
}

/// <summary>
/// Pendaftaran wajah SISWA massal dari kiosk: daftar kelas dan siswa, analisis pose, dan pengiriman sampel (endpoint /api/kiosk/wajah/*, token kiosk perangkat).
/// Server hanya mengizinkan siswa dari unit katalog perangkat ini. Semua panggilan gagal-lunak: galat jaringan menjadi pesan, bukan pengecualian.
/// </summary>
public sealed class WajahSiswaApiClient
{
    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _baseUrl;
    private readonly string _token;

    public WajahSiswaApiClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
    }

    public async Task<HasilDaftar<List<KelasWajah>>> DaftarKelasAsync()
    {
        var (ok, pesan, data) = await KirimAsync(HttpMethod.Get, "wajah/kelas", null).ConfigureAwait(false);
        if (!ok || data is null) return new HasilDaftar<List<KelasWajah>> { Berhasil = false, Pesan = pesan };
        var hasil = new List<KelasWajah>();
        if (data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                hasil.Add(new KelasWajah { Kunci = Teks(el, "kunci") ?? "", Nama = Teks(el, "nama") ?? "-", Jumlah = Angka(el, "jumlah") });
            }
        }
        return new HasilDaftar<List<KelasWajah>> { Berhasil = true, Data = hasil };
    }

    public async Task<HasilDaftar<List<SiswaWajah>>> DaftarSiswaAsync(string kunciKelas)
    {
        var (ok, pesan, data) = await KirimAsync(HttpMethod.Get, "wajah/siswa?kelas=" + Uri.EscapeDataString(kunciKelas), null).ConfigureAwait(false);
        if (!ok || data is null) return new HasilDaftar<List<SiswaWajah>> { Berhasil = false, Pesan = pesan };
        var hasil = new List<SiswaWajah>();
        if (data.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                hasil.Add(new SiswaWajah { Id = Angka(el, "id"), Nama = Teks(el, "nama") ?? "-", Nis = Teks(el, "nis"), Status = Teks(el, "status") ?? "belum" });
            }
        }
        return new HasilDaftar<List<SiswaWajah>> { Berhasil = true, Data = hasil };
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
        }
        return new HasilDaftar<AnalisisWajah> { Berhasil = true, Data = a };
    }

    /// <summary>Mengirim satu sampel (angle: front | left | right). Berhasil = diterima face-service (sampel ditolak karena kualitas = false + pesan).</summary>
    public async Task<HasilDaftar<bool>> KirimSampelAsync(int siswaId, string angle, byte[] jpeg)
    {
        var (ok, pesan, _) = await KirimAsync(HttpMethod.Post, "wajah/sampel", new { siswa_id = siswaId, angle, image_base64 = Convert.ToBase64String(jpeg) }).ConfigureAwait(false);
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
