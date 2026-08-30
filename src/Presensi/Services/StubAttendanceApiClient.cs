using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// SEMENTARA - selalu "berhasil" dgn nama palsu, TIDAK PERNAH benar-benar
/// menghubungi Absen. Dipakai supaya alur UI penuh (kamera -> deteksi ->
/// "berhasil" -> bunyi -> reset) bisa diuji end-to-end SEKARANG, sebelum
/// endpoint asli di Absen (berbasis token, bukan sesi login browser)
/// dirancang & dibangun. GANTI dgn implementasi HTTP asli begitu endpoint
/// itu sudah disepakati - cukup buat class baru yang implement
/// IAttendanceApiClient, tidak perlu menyentuh MainWindow sama sekali
/// (lihat cara MainWindow menerima IAttendanceApiClient lewat constructor).
/// </summary>
public sealed class StubAttendanceApiClient : IAttendanceApiClient
{
    public Task<AttendanceResult> IdentifyFaceAsync(byte[] jpegBytes, AttendanceMode mode, CancellationToken ct = default)
    {
        Log.Warn($"[STUB] IdentifyFaceAsync dipanggil ({jpegBytes.Length} bytes, mode={mode}) - belum terhubung ke Absen sungguhan.");
        return Task.FromResult(new AttendanceResult
        {
            Success = true,
            PersonName = "(mode uji - belum terhubung ke server)",
            Message = $"Contoh: {mode} tercatat pukul {DateTime.Now:HH:mm}.",
        });
    }

    public Task<AttendanceResult> SubmitBarcodeAsync(string barcode, AttendanceMode mode, CancellationToken ct = default)
    {
        Log.Warn($"[STUB] SubmitBarcodeAsync dipanggil (barcode={barcode}, mode={mode}) - belum terhubung ke Absen sungguhan.");
        return Task.FromResult(new AttendanceResult
        {
            Success = true,
            PersonName = $"Barcode {barcode} (mode uji)",
            Message = $"Contoh: {mode} tercatat pukul {DateTime.Now:HH:mm}.",
        });
    }
}
