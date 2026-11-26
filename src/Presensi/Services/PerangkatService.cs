using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Presensi.Logging;

namespace Presensi.Services;

public enum StatusPerangkat
{
    /// <summary>Belum terdaftar / belum ada jawaban server.</summary>
    Belum,
    /// <summary>Sudah mengajukan izin, menunggu Admin IT menyetujui (menu Perangkat &gt; Absensi).</summary>
    Menunggu,
    /// <summary>Disetujui: memakai token perangkat.</summary>
    Aktif,
    /// <summary>Akses dicabut Admin IT (bisa diaktifkan lagi).</summary>
    Dicabut,
    /// <summary>Diblokir Admin IT: tidak bisa mendaftar lagi sampai blokir dibuka.</summary>
    Diblokir,
    /// <summary>Server sudah mencatat perangkat ini tetapi token-nya hilang dari PC (mis. appsettings.json terhapus).</summary>
    TerdaftarTokenHilang,
}

/// <summary>
/// Pendaftaran mandiri perangkat kiosk ke server pusat (2026-11-26), sama polanya dengan Keuangan/Data Master: aplikasi
/// mengajukan izin sendiri (tanpa token yang diketik manual), Admin IT menyetujui di menu Perangkat &gt; Absensi sambil
/// menentukan katalog dan nama tampilan. Sebelum disetujui, token perangkat belum berlaku (server menjawab 401
/// MENUNGGU_PERSETUJUAN).
///
/// Peralihan dari instalasi lama dijaga: selama BELUM disetujui, kiosk yang masih punya KioskToken lama tetap memakainya (presensi
/// di sekolah tidak terputus). Begitu disetujui, aplikasi otomatis beralih ke token perangkat. DeviceUid hanya pengenal
/// (mencegah pendaftaran ganda dari PC yang sama), BUKAN kredensial.
/// </summary>
public sealed class PerangkatService : IDisposable
{
    private static readonly TimeSpan JedaCekAktif = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan JedaCekMenunggu = TimeSpan.FromSeconds(20);
    // Pendaftaran dibatasi server (10/jam, global): kegagalan yang tidak akan sembuh sendiri jangan diulang rapat.
    private static readonly TimeSpan JedaUlangDaftar = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan JedaUlangTerhenti = TimeSpan.FromMinutes(30);

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly bool _punyaHttp;
    private System.Threading.Timer? _timer;
    private int _sibuk;
    private DateTime _berikutnyaUtc = DateTime.MinValue;

    public StatusPerangkat Status { get; private set; } = StatusPerangkat.Belum;
    public event EventHandler? StatusBerubah;

    public PerangkatService(AppConfig config, HttpClient? http = null)
    {
        _config = config;
        _punyaHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Token perangkat, hanya bila sudah DISETUJUI (selain itu null: pakai jalur lain / tampilkan status).</summary>
    public string? TokenPerangkat => Status == StatusPerangkat.Aktif && !string.IsNullOrWhiteSpace(_config.DeviceToken) ? _config.DeviceToken : null;

    /// <summary>Perangkat ini ditolak keras oleh Admin IT: token lama bersama juga TIDAK boleh dipakai perangkat ini.</summary>
    public bool Terlarang => Status == StatusPerangkat.Diblokir || Status == StatusPerangkat.Dicabut;

    public string Pesan() => Status switch
    {
        StatusPerangkat.Menunggu => "Perangkat ini menunggu persetujuan Admin IT (menu Perangkat > Absensi).",
        StatusPerangkat.Aktif => "Perangkat disetujui.",
        StatusPerangkat.Dicabut => "Akses perangkat ini dicabut oleh Admin IT.",
        StatusPerangkat.Diblokir => "Perangkat ini diblokir oleh Admin IT.",
        StatusPerangkat.TerdaftarTokenHilang => "Perangkat ini sudah terdaftar tetapi tokennya hilang. Minta Admin IT menghapus perangkat ini di menu Perangkat, lalu buka ulang aplikasi.",
        _ => "Mendaftarkan perangkat ke server...",
    };

    public void Mulai()
    {
        SiapkanUid();
        _timer = new System.Threading.Timer(_ => _ = SiklusAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(20));
    }

    /// <summary>Pengenal stabil per PC (dari MachineGuid Windows), disimpan sekali. Tidak memuat data pribadi.</summary>
    private void SiapkanUid()
    {
        if (!string.IsNullOrWhiteSpace(_config.DeviceUid)) return;
        string sumber;
        try
        {
            using var dasar = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var kunci = dasar.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            sumber = kunci?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        }
        catch (Exception ex)
        {
            Log.Warn($"MachineGuid tidak terbaca ({ex.Message}); memakai nama komputer sebagai dasar pengenal.");
            sumber = Environment.MachineName;
        }
        var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes("presensi|" + sumber));
        var hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 20);
        _config.DeviceUid = "PC-" + hex;
        AppConfig.Save(_config);
    }

    private void Atur(StatusPerangkat baru)
    {
        if (Status == baru) return;
        Status = baru;
        Log.Info($"[Perangkat] status: {baru}");
        StatusBerubah?.Invoke(this, EventArgs.Empty);
    }

    private async Task SiklusAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _sibuk, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow < _berikutnyaUtc) return;
            if (string.IsNullOrWhiteSpace(_config.DeviceToken)) await DaftarAsync().ConfigureAwait(false);
            else await CekStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Perangkat] siklus gagal (server tak terjangkau?): {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _sibuk, 0);
        }
    }

    private async Task DaftarAsync()
    {
        var json = JsonSerializer.Serialize(new { label = Environment.MachineName, device_uid = _config.DeviceUid });
        using var res = await _http.PostAsync($"{_config.BaseUrl}/api/kiosk/register", new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        var kode = (int)res.StatusCode;
        if (res.StatusCode == HttpStatusCode.Created)
        {
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("data").GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("server tidak mengembalikan token");
            _config.DeviceToken = token;
            AppConfig.Save(_config);
            Atur(StatusPerangkat.Menunggu);
            _berikutnyaUtc = DateTime.UtcNow + JedaCekMenunggu;
        }
        else if (kode == 409)
        {
            Atur(StatusPerangkat.TerdaftarTokenHilang);
            _berikutnyaUtc = DateTime.UtcNow + JedaUlangTerhenti;
        }
        else if (kode == 403)
        {
            Atur(StatusPerangkat.Diblokir);
            _berikutnyaUtc = DateTime.UtcNow + JedaUlangTerhenti;
        }
        else
        {
            Log.Warn($"[Perangkat] pendaftaran belum berhasil (HTTP {kode}); dicoba lagi nanti.");
            _berikutnyaUtc = DateTime.UtcNow + JedaUlangDaftar;
        }
    }

    private async Task CekStatusAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.BaseUrl}/api/kiosk/status");
        req.Headers.TryAddWithoutValidation("X-KIOSK-TOKEN", _config.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-App-Version", AppVersionInfo.Current);
        using var res = await _http.SendAsync(req).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            Log.Warn($"[Perangkat] cek status gagal (HTTP {(int)res.StatusCode}); status lama dipertahankan.");
            _berikutnyaUtc = DateTime.UtcNow + JedaUlangDaftar;
            return;
        }
        using var doc = JsonDocument.Parse(body);
        var state = doc.RootElement.GetProperty("data").GetProperty("state").GetString();
        switch (state)
        {
            case "aktif": Atur(StatusPerangkat.Aktif); _berikutnyaUtc = DateTime.UtcNow + JedaCekAktif; break;
            case "menunggu": Atur(StatusPerangkat.Menunggu); _berikutnyaUtc = DateTime.UtcNow + JedaCekMenunggu; break;
            case "dicabut": Atur(StatusPerangkat.Dicabut); _berikutnyaUtc = DateTime.UtcNow + JedaCekMenunggu; break;
            case "diblokir": Atur(StatusPerangkat.Diblokir); _berikutnyaUtc = DateTime.UtcNow + JedaCekMenunggu; break;
            case "tidak_dikenal":
                // Permintaan ditolak/dihapus Admin IT (atau data server diganti): buang token lama, ajukan lagi pada siklus berikutnya.
                Log.Warn("[Perangkat] token tidak dikenal server (permintaan ditolak/dihapus); mengajukan izin lagi.");
                _config.DeviceToken = null;
                AppConfig.Save(_config);
                Atur(StatusPerangkat.Belum);
                _berikutnyaUtc = DateTime.UtcNow + JedaUlangDaftar;
                break;
            default:
                Log.Warn($"[Perangkat] status tak dikenal dari server: {state}");
                _berikutnyaUtc = DateTime.UtcNow + JedaUlangDaftar;
                break;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_punyaHttp) _http.Dispose();
    }
}

/// <summary>
/// Klien pengganti saat perangkat BELUM boleh presensi (menunggu persetujuan, diblokir, dicabut, atau instalasi baru tanpa token lama).
/// Tidak menghubungi server: setiap percobaan dijawab pesan status, supaya tidak ada hasil palsu seperti mode uji (Stub).
/// </summary>
public sealed class MenungguPersetujuanClient : IAttendanceApiClient
{
    private readonly Func<string> _pesan;
    public MenungguPersetujuanClient(Func<string> pesan) => _pesan = pesan;

    public Task<AttendanceResult> IdentifyFaceAsync(byte[] jpegBytes, AttendanceMode mode, CancellationToken ct = default)
        => Task.FromResult(new AttendanceResult { Success = false, Message = _pesan() });

    public Task<AttendanceResult> SubmitBarcodeAsync(string barcode, AttendanceMode mode, CancellationToken ct = default)
        => Task.FromResult(new AttendanceResult { Success = false, Message = _pesan() });
}
