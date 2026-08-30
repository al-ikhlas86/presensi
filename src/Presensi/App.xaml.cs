using System.Windows;
using System.Windows.Threading;

namespace Presensi;

public partial class App : Application
{
    // Kiosk ini WAJIB jalan berhari-hari tanpa restart (dipasang di PC pos
    // satpam/TU, dinyalakan pagi lalu dibiarkan) - 1 exception tak
    // tertangani TIDAK BOLEH mematikan seluruh aplikasi begitu saja
    // (persis keluhan lama soal web absen yang harus "diakalin" biar tidak
    // perlu dipantau terus). Exception dicatat ke log, aplikasi tetap
    // hidup - lebih baik 1 fitur sempat error drpd seluruh kiosk mati dan
    // presensi anak-anak berhenti total sampai ada yang sadar & restart manual.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.Log.Error("UI thread exception (ditahan, aplikasi tetap jalan)", e.Exception);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logging.Log.Error("Background thread exception", e.ExceptionObject as Exception);
    }
}
