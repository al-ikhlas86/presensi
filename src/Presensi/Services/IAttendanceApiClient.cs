namespace Presensi.Services;

public enum AttendanceMode { Masuk, Pulang }

public sealed class AttendanceResult
{
    // "set" biasa, BUKAN "init" (C# 9+) - net48 (.NET Framework) tidak
    // punya tipe penanda System.Runtime.CompilerServices.IsExternalInit
    // yang dibutuhkan compiler utk fitur itu, gagal compile di target itu.
    public bool Success { get; set; }
    public string? PersonName { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Jembatan ke backend presensi SUNGGUHAN (Absen). BELUM diimplementasikan
/// sungguhan - lihat StubAttendanceApiClient. Endpoint asli yang akan
/// dipanggil nanti (dikonfirmasi dari kode Absen yang sudah ada,
/// PresensiController.php) perlu tambahan endpoint baru berbasis TOKEN
/// (pola sama dgn api/mobile/* yang sudah dipakai Mobile-app backend) -
/// endpoint /presensi/face-identify yang ADA SEKARANG pakai sesi login
/// browser + CSRF, tidak cocok dipanggil dari aplikasi desktop tanpa
/// browser. Ini didiskusikan lagi sebelum diimplementasikan sungguhan -
/// SENGAJA belum ditulis sekarang supaya perubahan besar di Absen tidak
/// tercampur dgn pembangunan fondasi aplikasi desktop ini.
/// </summary>
public interface IAttendanceApiClient
{
    Task<AttendanceResult> IdentifyFaceAsync(byte[] jpegBytes, AttendanceMode mode, CancellationToken ct = default);
    Task<AttendanceResult> SubmitBarcodeAsync(string barcode, AttendanceMode mode, CancellationToken ct = default);
}
