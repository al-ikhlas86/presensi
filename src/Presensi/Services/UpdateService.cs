using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Auto-update lewat GitHub REST API (repo publik, tanpa token), DIRANCANG ULANG 2026-11-27 (sama dengan Keuangan/Data Master, disesuaikan untuk KIOSK
/// tanpa petugas). Sebelumnya: popup pembuka menahan kiosk sampai unduhan 114 MB selesai (di jaringan lambat berarti presensi tidak jalan), seluruh berkas
/// di memori, tidak bisa dilanjutkan, tidak diperiksa keutuhannya. Sekarang tiga tahap terpisah:
///   1. UNDUH di latar belakang (kiosk tetap berjalan): berkas .part di %LocalAppData%\Presensi\update, DILANJUTKAN (Range) bila putus, macet 60 dtk dianggap
///      putus, ulang sampai 4x, lalu cek ukuran + SHA-256 (digest rilis GitHub) + isi zip.
///   2. SIAPKAN: ekstrak ke staging; baru "siap" setelah penanda siap.json ditulis.
///   3. TERAPKAN tanpa menunggu klik (kiosk tak berpenjaga): saat aplikasi dibuka (cepat, tanpa jaringan) ATAU saat kiosk sepi (tanpa presensi 10 menit dan
///      jendela daftar wajah tertutup). Penyalinan memakai robocopy dari staging; kegagalan dibatasi 2x/24 jam supaya tidak berputar.
/// Kode ini dikompilasi untuk net48 DAN net8: hindari API yang tak ada di net48 (SHA256.HashData, Convert.ToHexString, string.Contains(StringComparison), Math.Clamp).
/// </summary>
public static class UpdateService
{
    // Pesan status untuk banner di MainWindow (null/kosong = sembunyikan). Unduhan berjalan di latar belakang: banner kecil saja, tidak menutupi kamera.
    public static event Action<string?>? StatusChanged;

    // Kemajuan unduhan 0-100 (null = tahap tanpa persentase). Dipakai jendela "memasang pembaruan" saat startup.
    public static event Action<double?>? ProgressChanged;

    // Pembaruan sudah diunduh lengkap dan siap (dipasang otomatis saat kiosk sepi atau saat aplikasi dibuka lagi).
    public static event Action<string>? PembaruanSiap;

#if NET48
    private const string AssetName = "Presensi-net48.zip";
#else
    private const string AssetName = "Presensi-net8.zip";
#endif
    private const string ApiLatestReleaseUrl = "https://api.github.com/repos/al-ikhlas86/presensi/releases/latest";
    private const string ApiAssetUrlTemplate = "https://api.github.com/repos/al-ikhlas86/presensi/releases/assets/{0}";

    private const int MaksPercobaan = 2;
    private const int MaksUlangUnduh = 4;
    private static readonly TimeSpan BatasMacet = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim KunciPeriksa = new SemaphoreSlim(1, 1);
    private static long _aktivitasTicks = DateTime.UtcNow.Ticks;

    // true = sesi ini jangan memeriksa/menerapkan update (aplikasi dibuka ulang helper setelah penyalinan gagal).
    public static bool LewatiSesiIni { get; set; }

    private sealed class StatusPercobaan
    {
        public string Target { get; set; } = "";
        public int Percobaan { get; set; }
        public DateTime TerakhirUtc { get; set; }
    }

    private sealed class Siap
    {
        public string Versi { get; set; } = "";
        public string Staging { get; set; } = "";
    }

    private static string DataLokal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Presensi");
    private static string AkarUpdate => Path.Combine(DataLokal, "update");
    private static string BerkasStatus => Path.Combine(DataLokal, "update_state.json");
    private static string BerkasSiap => Path.Combine(AkarUpdate, "siap.json");

    private static StatusPercobaan? BacaStatus()
    {
        try { return File.Exists(BerkasStatus) ? JsonSerializer.Deserialize<StatusPercobaan>(File.ReadAllText(BerkasStatus)) : null; }
        catch { return null; }
    }

    private static void TulisStatus(StatusPercobaan s)
    {
        try { Directory.CreateDirectory(DataLokal); File.WriteAllText(BerkasStatus, JsonSerializer.Serialize(s)); }
        catch { /* gagal menyimpan status tidak boleh menghentikan update */ }
    }

    private static void HapusStatus()
    {
        try { if (File.Exists(BerkasStatus)) File.Delete(BerkasStatus); }
        catch { /* abaikan */ }
    }

    private static Siap? BacaSiap()
    {
        try
        {
            if (!File.Exists(BerkasSiap)) return null;
            var s = JsonSerializer.Deserialize<Siap>(File.ReadAllText(BerkasSiap));
            return s != null && Directory.Exists(s.Staging) && File.Exists(Path.Combine(s.Staging, "Presensi.exe")) ? s : null;
        }
        catch { return null; }
    }

    private static Version Terpasang() => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    private static bool Ditahan(Version target)
    {
        var st = BacaStatus();
        return st != null && st.Target == target.ToString() && st.Percobaan >= MaksPercobaan && (DateTime.UtcNow - st.TerakhirUtc) < TimeSpan.FromHours(24);
    }

    /// <summary>Versi pembaruan yang sudah diunduh lengkap dan siap diterapkan (lebih baru dari yang terpasang), atau null.</summary>
    public static string? VersiSiap()
    {
        var siap = BacaSiap();
        return siap != null && Version.TryParse(NormalizeVersion(siap.Versi), out var v) && v > Terpasang() ? siap.Versi : null;
    }

    /// <summary>Dipanggil tiap ada presensi: kiosk dianggap sibuk, pembaruan menunggu sepi.</summary>
    public static void CatatAktivitas() => Interlocked.Exchange(ref _aktivitasTicks, DateTime.UtcNow.Ticks);

    /// <summary>Pembaruan siap, tidak ada presensi selama <paramref name="sepi"/>, dan penerapan tidak sedang ditahan.</summary>
    public static bool SiapDanSepi(TimeSpan sepi)
    {
        if (LewatiSesiIni || VersiSiap() == null) return false;
        if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _aktivitasTicks), DateTimeKind.Utc) < sepi) return false;
        var siap = BacaSiap();
        return siap != null && Version.TryParse(NormalizeVersion(siap.Versi), out var v) && !Ditahan(v);
    }

    private static void BersihkanSisaUpdate()
    {
        try { if (Directory.Exists(AkarUpdate)) Directory.Delete(AkarUpdate, true); }
        catch { /* masih terkunci: dicoba lagi lain kali */ }
    }

    /// <summary>Tahap 1+2 di LATAR BELAKANG. Tidak pernah melempar; mengirim <see cref="PembaruanSiap"/> bila ada pembaruan yang siap diterapkan.</summary>
    public static async Task PeriksaDanSiapkanAsync(AppConfig config)
    {
        if (LewatiSesiIni) return;
        if (!await KunciPeriksa.WaitAsync(0).ConfigureAwait(false)) { Log.Info("[Update] Pemeriksaan lain masih berjalan - dilewati."); return; }
        string? siap = null;
        try { siap = await PeriksaCoreAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Gagal cek/unduh (tidak ada internet, GitHub tak terjangkau, aplikasi ditutup di tengah unduhan) TIDAK BOLEH mengganggu kiosk:
            // dicatat, dicoba lagi di siklus berikutnya, dan .part dilanjutkan, bukan mulai dari nol.
            Log.Error("[Update] Gagal cek/siapkan update (diabaikan, aplikasi tetap jalan seperti biasa)", ex);
        }
        finally
        {
            KunciPeriksa.Release();
            StatusChanged?.Invoke(null);
            ProgressChanged?.Invoke(null);
        }
        if (siap != null) PembaruanSiap?.Invoke(siap);
    }

    private static async Task<string?> PeriksaCoreAsync()
    {
        var installed = Terpasang();
        Log.Info($"[Update] Cek rilis terbaru via GitHub API (versi terpasang {installed})");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Presensi-AlIkhlas86-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        string releaseJson;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            releaseJson = await HttpGetStringAsync(http, ApiLatestReleaseUrl, cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(releaseJson);
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(NormalizeVersion(tagName.TrimStart('v', 'V')), out var remote))
        {
            Log.Warn($"[Update] Tag rilis \"{tagName}\" tidak bisa dibaca sbg versi - dilewati.");
            return null;
        }
        if (remote <= installed)
        {
            Log.Info($"[Update] Sudah versi terbaru (terpasang {installed}, rilis {remote}).");
            HapusStatus();
            BersihkanSisaUpdate();
            return null;
        }
        var tujuan = remote.ToString();

        var sudahSiap = BacaSiap();
        if (sudahSiap != null && sudahSiap.Versi == tujuan)
        {
            Log.Info($"[Update] Pembaruan {tujuan} sudah diunduh dan siap diterapkan.");
            return tujuan;
        }
        if (Ditahan(remote))
        {
            Log.Warn($"[Update] Update ke {tujuan} sudah dicoba {MaksPercobaan}x dan gagal diterapkan - ditahan 24 jam. Lihat update_helper.log.");
            return null;
        }

        long assetId = 0, assetSize = 0;
        string? digest = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            assetId = asset.GetProperty("id").GetInt64();
            assetSize = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
            if (asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String) digest = dg.GetString();
            break;
        }
        if (assetId == 0)
        {
            Log.Warn($"[Update] Rilis {tagName} tidak punya asset \"{AssetName}\" - dilewati.");
            return null;
        }

        BersihkanVersiLain(tujuan);
        Directory.CreateDirectory(AkarUpdate);
        var partPath = Path.Combine(AkarUpdate, $"Presensi-{tujuan}.zip.part");
        var zipPath = Path.Combine(AkarUpdate, $"Presensi-{tujuan}.zip");
        var staging = Path.Combine(AkarUpdate, $"staging-{tujuan}");

        Log.Info($"[Update] Update ditemukan: {installed} -> {remote} ({assetSize / 1024.0 / 1024.0:F0} MB). Mengunduh di latar belakang, asset id={assetId}...");
        await UnduhDenganUlangAsync(http, assetId, assetSize, partPath, remote.ToString(3)).ConfigureAwait(false);

        StatusChanged?.Invoke($"Memeriksa keutuhan pembaruan {remote.ToString(3)}...");
        ProgressChanged?.Invoke(null);
        await Task.Run(() => Verifikasi(partPath, assetSize, digest)).ConfigureAwait(false);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        File.Move(partPath, zipPath);

        StatusChanged?.Invoke($"Menyiapkan pembaruan {remote.ToString(3)}...");
        await Task.Run(() => Ekstrak(zipPath, staging)).ConfigureAwait(false);
        File.Delete(zipPath);

        File.WriteAllText(BerkasSiap, JsonSerializer.Serialize(new Siap { Versi = tujuan, Staging = staging }));
        Log.Info($"[Update] Pembaruan {tujuan} siap diterapkan (staging: {staging}).");
        return tujuan;
    }

    // net48: HttpClient.GetStringAsync(url, CancellationToken) tidak ada; pakai SendAsync.
    private static async Task<string> HttpGetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static void BersihkanVersiLain(string versiTujuan)
    {
        try
        {
            if (!Directory.Exists(AkarUpdate)) return;
            foreach (var entri in Directory.EnumerateFileSystemEntries(AkarUpdate).ToList())
            {
                var nama = Path.GetFileName(entri);
                if (nama == "siap.json" || nama.IndexOf(versiTujuan, StringComparison.Ordinal) >= 0) continue;
                try { if (Directory.Exists(entri)) Directory.Delete(entri, true); else File.Delete(entri); } catch { /* terkunci: biarkan */ }
            }
        }
        catch { /* pembersihan tidak boleh menggagalkan update */ }
    }

    // ---- tahap 1: unduh (bisa dilanjutkan) ----

    private static async Task UnduhDenganUlangAsync(HttpClient http, long assetId, long assetSize, string partPath, string versi)
    {
        Exception? terakhir = null;
        for (var percobaan = 1; percobaan <= MaksUlangUnduh; percobaan++)
        {
            try { await UnduhSekaliAsync(http, assetId, assetSize, partPath, versi).ConfigureAwait(false); return; }
            catch (Exception ex)
            {
                terakhir = ex;
                Log.Warn($"[Update] Unduhan terputus (percobaan {percobaan}/{MaksUlangUnduh}): {ex.Message}. Dilanjutkan dari byte yang sudah ada.");
                StatusChanged?.Invoke("Unduhan pembaruan terputus, mencoba lagi... (kiosk tetap berjalan)");
                await Task.Delay(TimeSpan.FromSeconds(3 * percobaan * percobaan)).ConfigureAwait(false);
            }
        }
        throw new IOException("Unduhan pembaruan gagal berulang kali.", terakhir);
    }

    private static async Task UnduhSekaliAsync(HttpClient http, long assetId, long assetSize, string partPath, string versi)
    {
        long ada = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (assetSize > 0 && ada > assetSize) { File.Delete(partPath); ada = 0; }
        if (assetSize > 0 && ada == assetSize) return; // unduhan sebelumnya sudah lengkap

        using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiAssetUrlTemplate, assetId));
        req.Headers.Accept.ParseAdd("application/octet-stream");
        if (ada > 0) req.Headers.Range = new RangeHeaderValue(ada, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partPath); // berkas sebagian tidak cocok: mulai dari nol di percobaan berikut
            throw new IOException("Berkas sebagian tidak cocok dengan server, mulai ulang.");
        }
        resp.EnsureSuccessStatusCode();
        var lanjut = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!lanjut) ada = 0; // server mengabaikan Range: seluruh berkas dikirim ulang
        var total = assetSize > 0 ? assetSize : ada + (resp.Content.Headers.ContentLength ?? 0);

        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var dst = new FileStream(partPath, lanjut ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true))
        {
            var buffer = new byte[256 * 1024];
            var jumlah = ada;
            var persenTerakhir = -1;
            while (true)
            {
                // Macet (tidak ada byte masuk 60 dtk) dianggap putus: ditangkap pemanggil lalu dilanjutkan dari byte yang sudah ada.
                using (var macet = new CancellationTokenSource(BatasMacet))
                {
                    var n = await src.ReadAsync(buffer, 0, buffer.Length, macet.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await dst.WriteAsync(buffer, 0, n).ConfigureAwait(false);
                    jumlah += n;
                }
                if (total <= 0) continue;
                var persen = (int)Math.Min(100, jumlah * 100 / total);
                if (persen == persenTerakhir) continue;
                persenTerakhir = persen;
                ProgressChanged?.Invoke(persen);
                StatusChanged?.Invoke($"Mengunduh pembaruan {versi}: {persen}% ({jumlah / 1024.0 / 1024.0:F0} dari {total / 1024.0 / 1024.0:F0} MB) - kiosk tetap berjalan");
            }
            await dst.FlushAsync().ConfigureAwait(false);
            if (total > 0 && jumlah < total) throw new IOException($"Unduhan terpotong ({jumlah} dari {total} byte).");
        }
    }

    private static void Verifikasi(string partPath, long assetSize, string? digest)
    {
        var ukuran = new FileInfo(partPath).Length;
        if (assetSize > 0 && ukuran != assetSize)
        {
            File.Delete(partPath);
            throw new IOException($"Ukuran unduhan tidak sesuai ({ukuran} dari {assetSize} byte); dibuang, akan diunduh ulang.");
        }
        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string hash;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(partPath))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            if (!hash.Equals(digest.Substring("sha256:".Length), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                throw new IOException("SHA-256 unduhan tidak cocok dengan rilis; dibuang, akan diunduh ulang.");
            }
            Log.Info("[Update] SHA-256 unduhan cocok.");
        }
    }

    private static void Ekstrak(string zipPath, string staging)
    {
        bool lengkap;
        using (var zip = ZipFile.OpenRead(zipPath))
            lengkap = zip.Entries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), "Presensi.exe", StringComparison.OrdinalIgnoreCase));
        if (!lengkap)
        {
            File.Delete(zipPath);
            throw new IOException("Isi zip pembaruan tidak lengkap (Presensi.exe tidak ada); dibuang.");
        }
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        // Overload 2-argumen SENGAJA: staging baru dibuat (kosong) dan overload 3-argumen tidak ada di net48.
        ZipFile.ExtractToDirectory(zipPath, staging);
    }

    // ---- tahap 3: terapkan ----

    /// <summary>
    /// Dipanggil saat aplikasi dibuka (tanpa jaringan, cepat): bila ada pembaruan yang sudah diunduh lengkap, terapkan SEKARANG.
    /// true = penerapan dimulai (Shutdown() sudah diminta; pemanggil berhenti).
    /// </summary>
    public static bool TerapkanBilaSiap()
    {
        if (LewatiSesiIni) return false;
        var siap = BacaSiap();
        if (siap == null) return false;
        if (!Version.TryParse(NormalizeVersion(siap.Versi), out var v) || v <= Terpasang())
        {
            BersihkanSisaUpdate(); // sudah terpasang atau penanda rusak
            return false;
        }
        if (Ditahan(v)) { Log.Warn($"[Update] Penerapan {siap.Versi} ditahan (gagal berulang). Lihat update_helper.log."); return false; }
        Log.Info($"[Update] Pembaruan {siap.Versi} sudah diunduh sebelumnya: diterapkan saat aplikasi dibuka.");
        return TerapkanSekarang();
    }

    /// <summary>Menerapkan pembaruan yang sudah siap: helper menyalin dari staging setelah aplikasi tertutup lalu membuka aplikasi baru.</summary>
    public static bool TerapkanSekarang()
    {
        var siap = BacaSiap();
        if (siap == null) { Log.Warn("[Update] Tidak ada pembaruan siap untuk diterapkan."); return false; }

        // Catat percobaan SEBELUM menerapkan: bila penyalinan gagal dan aplikasi lama terbuka lagi, percobaan ini tetap terhitung.
        var sebelumnya = BacaStatus();
        TulisStatus(new StatusPercobaan { Target = siap.Versi, Percobaan = (sebelumnya != null && sebelumnya.Target == siap.Versi ? sebelumnya.Percobaan : 0) + 1, TerakhirUtc = DateTime.UtcNow });

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath = Path.Combine(installDir, "Presensi.exe");
        var stagingDir = siap.Staging;
        var pid = Process.GetCurrentProcess().Id;
        var scriptPath = Path.Combine(Path.GetTempPath(), "presensi-update.bat");
        // robocopy (BUKAN xcopy; /IS = tetap timpa berkas yang tampak sama): mengulang otomatis bila berkas terkunci (/R:15 /W:2) dan KODE KELUARNYA diperiksa
        // (>= 8 = gagal). Semua langkah dicatat ke update_helper.log; bila gagal, aplikasi lama dibuka dengan --update-gagal (sesi itu tidak mencoba lagi).
        var script =
            "@echo off\r\n" +
            "set \"LOGDIR=%LOCALAPPDATA%\\Presensi\\logs\"\r\n" +
            "if not exist \"%LOGDIR%\" mkdir \"%LOGDIR%\"\r\n" +
            "set \"LOG=%LOGDIR%\\update_helper.log\"\r\n" +
            $"echo [%date% %time%] helper mulai, menunggu PID {pid} keluar >> \"%LOG%\"\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if not errorlevel 1 (\r\n" +
            "    ping 127.0.0.1 -n 2 >NUL\r\n" +
            "    goto wait\r\n" +
            ")\r\n" +
            $"robocopy \"{stagingDir}\" \"{installDir}\" /E /IS /R:15 /W:2 /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1\r\n" +
            "set \"RC=%ERRORLEVEL%\"\r\n" +
            "echo [%date% %time%] robocopy kode keluar %RC% >> \"%LOG%\"\r\n" +
            "if %RC% GEQ 8 goto gagal\r\n" +
            $"rmdir /S /Q \"{stagingDir}\"\r\n" +
            $"del /F /Q \"{BerkasSiap}\" 2>NUL\r\n" +
            "echo [%date% %time%] update diterapkan, membuka aplikasi baru >> \"%LOG%\"\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "goto selesai\r\n" +
            ":gagal\r\n" +
            "echo [%date% %time%] PENYALINAN GAGAL, berkas masih terkunci - membuka aplikasi lama >> \"%LOG%\"\r\n" +
            $"start \"\" \"{exePath}\" --update-gagal\r\n" +
            ":selesai\r\n" +
            "(goto) 2>NUL & del \"%~f0\"\r\n"; // idiom: helper menghapus dirinya sendiri tanpa pesan galat
        File.WriteAllText(scriptPath, script);
        Log.Info("[Update] Helper penutup+pasang disiapkan - aplikasi akan tertutup sebentar lalu terbuka lagi otomatis dgn versi baru.");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        // Tutup app SEKARANG - helper di atas menunggu PID ini keluar (via tasklist) sebelum menimpa file.
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        return true;
    }

    // "1.1.0" -> "1.1.0.0" - System.Version butuh >=2 bagian, dibuat selalu 4 bagian supaya perbandingan dgn versi assembly (SELALU 4 bagian) konsisten.
    private static string NormalizeVersion(string v)
    {
        var parts = v.Trim().Split('.');
        var padded = parts.Concat(Enumerable.Repeat("0", Math.Max(0, 4 - parts.Length))).Take(4);
        return string.Join(".", padded);
    }
}
