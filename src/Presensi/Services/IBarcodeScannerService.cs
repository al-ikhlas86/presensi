namespace Presensi.Services;

/// <summary>
/// Scanner barcode mode CDC (muncul sbg COM port virtual, BUKAN mode HID
/// keyboard-emulation) - sesuai alat yang sudah dipakai sekolah sekarang
/// (lihat catatan user: "PC TU node js dan cmd... scanner mode cdc").
/// </summary>
public interface IBarcodeScannerService : IDisposable
{
    bool IsConnected { get; }

    /// <summary>False bila port yang terbuka sudah hilang dari PC (kabel dicabut): handle lama tidak berguna lagi dan harus dibuang.</summary>
    bool PortMasihAda { get; }
    event EventHandler<string>? BarcodeScanned;
    event EventHandler<string>? ScannerError;

    void Connect(string portName, int baudRate = 9600);
    void Disconnect();
}
