using System.IO.Ports;
using System.Text;
using Presensi.Logging;

namespace Presensi.Services;

/// <summary>
/// Scanner CDC kirim data sbg baris teks lewat serial port (persis kirim
/// lewat keyboard, diakhiri Enter/CRLF) - implementasi ini buffer per-baris
/// via DataReceived, BUKAN baca per-karakter manual, supaya tahan terhadap
/// data yang datang terpecah-pecah antar event (umum terjadi di serial port
/// asli, beda dari asumsi "1 event = 1 barcode utuh" yang naif).
/// </summary>
public sealed class SerialBarcodeScannerService : IBarcodeScannerService
{
    private SerialPort? _port;
    private readonly StringBuilder _buffer = new();

    public bool IsConnected => _port?.IsOpen == true;

    public event EventHandler<string>? BarcodeScanned;
    public event EventHandler<string>? ScannerError;

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public void Connect(string portName, int baudRate = 9600)
    {
        Disconnect();
        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                NewLine = "\r\n",
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            _port.DataReceived += OnDataReceived;
            _port.ErrorReceived += (_, e) => ScannerError?.Invoke(this, $"Serial error: {e.EventType}");
            _port.Open();
            Log.Info($"Scanner terhubung di {portName}@{baudRate}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Gagal membuka port scanner {portName}", ex);
            ScannerError?.Invoke(this, $"Gagal membuka port {portName}: {ex.Message}");
            _port = null;
        }
    }

    public void Disconnect()
    {
        if (_port is null) return;
        try
        {
            _port.DataReceived -= OnDataReceived;
            if (_port.IsOpen) _port.Close();
        }
        catch { /* abaikan - ini shutdown path, port mungkin sudah lepas fisik */ }
        finally
        {
            _port.Dispose();
            _port = null;
            _buffer.Clear();
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port is null) return;
        try
        {
            var chunk = _port.ReadExisting();
            _buffer.Append(chunk);

            // Barcode dianggap SELESAI begitu ketemu baris penuh (CR/LF) -
            // sisa buffer (kalau ada, jarang tapi mungkin) disimpan utk
            // digabung dgn potongan berikutnya.
            string combined = _buffer.ToString();
            var lines = combined.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length - 1; i++)
            {
                var code = lines[i].Trim();
                if (code.Length > 0) BarcodeScanned?.Invoke(this, code);
            }
            _buffer.Clear();
            _buffer.Append(lines[lines.Length - 1]); // sisa setelah baris terakhir yg belum tentu lengkap
        }
        catch (Exception ex)
        {
            Log.Error("Error membaca data scanner", ex);
        }
    }

    public void Dispose() => Disconnect();
}
