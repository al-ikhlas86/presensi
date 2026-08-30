# Presensi (Al-Ikhlas 86)

Aplikasi desktop kiosk untuk presensi wajah + scan barcode - pengganti web
Absen untuk PC pos satpam/TU, supaya tidak perlu lagi "diakalin" pakai
hotkey/CDC/CMD/Node.js buat standby di layar browser. Presensi tetap
tercatat di latar belakang meski preview kamera dimatikan atau user sedang
memakai aplikasi lain (Excel dsb).

**Status saat ini: SUDAH terhubung ke server sungguhan (Absen, lewat
Mobile-app backend) - diuji end-to-end nyata sampai ke database.** Lihat
bagian "Konfigurasi" di bawah sebelum menjalankan di PC sekolah.

## Kenapa 2 target build (net48 + net8.0-windows)?

Sekolah ini masih punya PC Windows 7. .NET modern (8+) tidak bisa jalan
sama sekali di situ, jadi 1 codebase ini di-*multi-target*:

| Target | Windows minimum | Dipakai untuk |
|---|---|---|
| `net48` | Windows 7 SP1 | PC lama |
| `net8.0-windows` | Windows 10 versi 1607+ | PC modern |

Satu solusi Visual Studio, satu `.csproj`, kode 99% sama - CI menghasilkan
2 installer terpisah tiap build (lihat `.github/workflows/build.yml`).

## Fitur

- [x] Preview kamera (bisa dimatikan tanpa menghentikan presensi di latar
      belakang - lihat `Services/CameraService.cs`)
- [x] Filter dekoratif di preview (murni tampilan, TIDAK PERNAH menyentuh
      frame yang dikirim ke pengenalan wajah - lihat `Filters/`)
- [x] Absen Masuk/Pulang (toggle mode di UI)
- [x] Nada "berhasil" (bukan suara robot, cukup 2 nada pendek - `Services/SoundService.cs`)
- [x] Scan barcode via scanner mode CDC (serial port) - `Services/SerialBarcodeScannerService.cs`
- [x] **Presensi sungguhan** - identifikasi wajah & checkin barcode
      terhubung ke server produksi (`Services/AbsenAttendanceApiClient.cs`),
      via Mobile-app backend, yang meneruskan ke Absen. Sudah diuji nyata
      sampai tersimpan di `attendance_logs`.

## Konfigurasi (WAJIB sebelum dipakai di PC sekolah)

Salin `src/Presensi/appsettings.example.json` jadi `appsettings.json` di
folder yang SAMA dengan `Presensi.exe` (bukan di source code), isi
`KioskToken` dengan token dari server (minta ke Admin IT - tersimpan di
`.env` Mobile-app backend sbg `KIOSK_API_TOKEN`). **Jangan pernah commit
`appsettings.json` yang sudah berisi token asli** (sudah di-`.gitignore`).

Kalau `appsettings.json` belum ada / `KioskToken` masih kosong, aplikasi
otomatis jalan di **mode uji** (`StubAttendanceApiClient` - selalu bilang
"berhasil" palsu, tidak benar-benar mengirim presensi) supaya tetap bisa
dicoba tanpa token dulu.

## Yang BELUM (sengaja, lihat komentar di kode)

- Belum ada installer/auto-updater (Velopack) - baru `dotnet publish` polos.
- Belum ada ikon aplikasi (`ApplicationIcon` sengaja dikosongkan di `.csproj`).
- Belum ada UI pemilihan kamera/COM-port yang proper (masih ambil device
  pertama yang ketemu).

## Build & jalankan lokal

Butuh .NET SDK 8+ terpasang (Visual Studio 2022 dengan workload ".NET
desktop development" sudah termasuk ini).

```
dotnet build src/Presensi/Presensi.csproj -f net8.0-windows
dotnet run --project src/Presensi/Presensi.csproj -f net8.0-windows
```

Ganti `net8.0-windows` jadi `net48` untuk uji target Windows lama (build
tetap bisa dari mesin Windows 10/11 manapun, tidak perlu PC Windows 7
sungguhan untuk COMPILE-nya - cuma untuk MENJALANKAN hasil buildnya).

## Build resmi (CI, hasil buat dipasang ke PC sekolah)

Compile TIDAK dilakukan di laptop manapun - 100% di server GitHub
(`windows-latest` runner), supaya laptop dev tidak lag/lama seperti build
APK Android sebelumnya. Trigger manual saja (`workflow_dispatch`), jalankan
dari tab **Actions** repo ini, atau minta AI assistant menjalankannya
(`gh workflow run build.yml`). Hasil build ada di tab **Actions** > run
yang dipilih > **Artifacts**.
