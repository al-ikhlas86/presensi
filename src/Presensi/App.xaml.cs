using System;
using System.Windows;
using System.Windows.Threading;
using Presensi.Services;

namespace Presensi;

public partial class App : Application
{
    // Kiosk bisa jalan berhari-hari tanpa restart (lihat komentar di bawah) -
    // cek sekali saat buka SAJA tidak cukup, jadi diulang berkala supaya
    // kiosk yang jarang dimatikan tetap kebagian update tanpa perlu restart
    // manual dari siapa pun.
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckInterval };

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

        // Lihat UpdateService.cs utk alasan ini ditulis sendiri (bukan lagi
        // AutoUpdater.NET.Official) - dijalankan di background thread (Task.Run),
        // BUKAN langsung di UI thread, krn ada unduhan file besar (build
        // self-contained net8.0-windows ~100MB+) yang tidak boleh membekukan
        // tampilan kiosk selama proses cek/unduh berlangsung.
        _ = System.Threading.Tasks.Task.Run(UpdateService.CheckAndApplyAsync);
        _updateTimer.Tick += (_, _) => _ = System.Threading.Tasks.Task.Run(UpdateService.CheckAndApplyAsync);
        _updateTimer.Start();
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
