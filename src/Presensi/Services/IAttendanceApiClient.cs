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

    /// <summary>True = permintaan tidak sampai/ditunda server (offline, waktu habis, 5xx, 401, 429): scan kartu masuk antrean offline, BUKAN ditolak.</summary>
    public bool GagalJaringan { get; set; }
}

/// <summary>
/// Jembatan ke backend presensi (endpoint /api/kiosk/* berbasis token kiosk). Implementasi: AbsenAttendanceApiClient (jalur nyata) dan
/// MenungguPersetujuanClient (perangkat belum boleh presensi: hanya menampilkan status).
/// </summary>
public interface IAttendanceApiClient
{
    Task<AttendanceResult> IdentifyFaceAsync(byte[] jpegBytes, AttendanceMode mode, CancellationToken ct = default);
    /// <summary>scannedAtUtc diisi hanya saat mengirim ulang scan dari antrean offline (waktu scan asli); null = scan langsung.</summary>
    Task<AttendanceResult> SubmitBarcodeAsync(string barcode, AttendanceMode mode, DateTime? scannedAtUtc = null, CancellationToken ct = default);
}
