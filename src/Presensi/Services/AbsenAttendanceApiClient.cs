using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Implementasi SUNGGUHAN IAttendanceApiClient - menggantikan
/// StubAttendanceApiClient. Rantai pemanggilan (SEMUA sudah diuji end-to-
/// end nyata sampai ke database sebelum kelas ini ditulis, lihat commit
/// Absen "Tambah endpoint token kiosk desktop" & Mobile-app "Tambah proxy
/// kiosk desktop"):
///
///   Desktop (kelas ini) --X-KIOSK-TOKEN--> Mobile-app backend
///   (/api/kiosk/*) --X-MOBILE-APP-TOKEN--> Absen (/api/mobile/kiosk/*)
///   --AttendanceCheckinService--> attendance_logs
///
/// Desktop TIDAK PERNAH memanggil Absen langsung & TIDAK PERNAH menyimpan
/// token Absen - cuma pegang token kiosk sendiri (KioskToken di bawah).
/// </summary>
public sealed class AbsenAttendanceApiClient : IAttendanceApiClient
{
    private readonly HttpClient _http;

    /// <summary>Base URL Mobile-app backend, TANPA trailing slash. Produksi: https://alikhlas86.duckdns.org/mobile-api</summary>
    public string BaseUrl { get; set; } = "https://alikhlas86.duckdns.org/mobile-api";

    /// <summary>Token kiosk perangkat ini - HARUS sama dgn KIOSK_API_TOKEN di .env Mobile-app backend.</summary>
    public string KioskToken { get; set; } = "";

    public AbsenAttendanceApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<AttendanceResult> IdentifyFaceAsync(byte[] jpegBytes, AttendanceMode mode, CancellationToken ct = default)
    {
        var imageBase64 = Convert.ToBase64String(jpegBytes);
        var payload = new { image_base64 = imageBase64, mode = ModeToString(mode) };
        return await PostAsync("face-identify", payload, ct).ConfigureAwait(false);
    }

    public async Task<AttendanceResult> SubmitBarcodeAsync(string barcode, AttendanceMode mode, CancellationToken ct = default)
    {
        var payload = new { identifier = barcode, mode = ModeToString(mode) };
        return await PostAsync("checkin", payload, ct).ConfigureAwait(false);
    }

    private static string ModeToString(AttendanceMode mode) => mode == AttendanceMode.Masuk ? "masuk" : "pulang";

    private async Task<AttendanceResult> PostAsync(string path, object payload, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/kiosk/{path}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-KIOSK-TOKEN", KioskToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            // ReadAsStringAsync() TANPA CancellationToken - overload dgn ct
            // cuma ada di .NET 5+, net48 tidak punya (beda BCL antar target).
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? personName = null;
            if (root.TryGetProperty("person_name", out var pn)) personName = pn.GetString();
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("nama", out var nama)) personName = nama.GetString();

            if (!success)
            {
                Log.Warn($"[Kiosk] {path} ditolak: {message}");
            }

            return new AttendanceResult { Success = success, PersonName = personName, Message = message };
        }
        catch (Exception ex)
        {
            Log.Error($"[Kiosk] Gagal memanggil {path}", ex);
            return new AttendanceResult { Success = false, Message = "Tidak bisa menghubungi server. Cek koneksi internet." };
        }
    }
}
