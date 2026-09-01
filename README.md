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
- [x] Absen Masuk/Pulang **OTOMATIS** berdasar jam PC + jadwal yang diatur
      lewat tombol "Pengaturan..." (`Services/ScheduleService.cs`) - tidak
      ada lagi toggle manual, sebelum jam Pulang = Masuk, jam Pulang ke
      atas = Pulang.
- [x] Nada "berhasil" (bukan suara robot, cukup 2 nada pendek - `Services/SoundService.cs`)
- [x] Scan barcode via scanner mode CDC (serial port) - `Services/SerialBarcodeScannerService.cs`
- [x] **Presensi sungguhan** - identifikasi wajah & checkin barcode
      terhubung ke server produksi (`Services/AbsenAttendanceApiClient.cs`),
      via Mobile-app backend, yang meneruskan ke Absen. Sudah diuji nyata
      sampai tersimpan di `attendance_logs`.
- [x] Ikon aplikasi - logo resmi SDIT Al-Ikhlas / YAI 86.
- [x] **Kelola Filter** (tombol "Kelola Filter..." di sebelah dropdown
      filter) - upload/hapus filter sendiri, **2 jenis** (lihat bagian
      "Bikin Filter Sendiri" di bawah). Filter "Normal" bawaan tidak bisa
      dihapus. 3 contoh stiker (`Astronot`, `Kacamata Kumis`,
      `Telinga Kucing`) otomatis tersedia begitu pertama kali dibuka -
      boleh dihapus/diganti kapan saja. Sengaja BELUM ada tema
      Ramadan/Kemerdekaan dulu (nunggu keputusan lanjut).

## Bikin Filter Sendiri

Ada **2 jenis filter**, bedanya di cara nempelnya:

### 1. Bingkai statis (`.png`)
File PNG biasa dgn latar **transparan**, diam di 1 posisi menutupi seluruh
layar (spt bingkai foto) - TIDAK mengikuti posisi wajah. Cocok utk
border/watermark/ucapan di pinggir layar.

1. Buat kanvas baru, latar TRANSPARAN (jangan diisi warna).
2. Gambar elemen di PINGGIR kanvas, HINDARI bagian tengah (situ tempat
   wajah orang muncul).
3. Export PNG (bukan JPG - JPG tidak punya transparansi).
4. "Kelola Filter..." > "Upload Filter..." > pilih file `.png`.

### 2. Stiker nempel wajah (`.stiker`)
Ikut posisi & ukuran wajah yang terdeteksi kamera secara real-time (pakai
deteksi wajah [dlib](http://dlib.net/), lihat `Filters/FaceTracker.cs`) -
cocok utk helm/kacamata/topi/telinga dsb, spt astronot/kacamata kumis/
telinga kucing yang sudah disediakan. **Bukan cuma gambar PNG polos** -
`.stiker` sebenarnya file **ZIP yang di-rename** (contoh gampang bikinnya:
buat `.zip` biasa, baru ganti akhiran `.zip` jadi `.stiker`), isinya WAJIB
2 file:

```
nama_apa_saja.stiker  (ZIP)
├── sticker.png     <- gambar stiker, PNG transparan
└── manifest.json   <- info penempatan, lihat contoh di bawah
```

Contoh `manifest.json` (angka boleh disesuaikan coba-coba sampai pas):
```json
{
  "DisplayName": "Nama Filter yang Muncul di Dropdown",
  "WidthRatio": 1.4,
  "AnchorYRatio": -0.3,
  "OffsetXRatio": 0.0,
  "AnchorLandmark": "box"
}
```
- `WidthRatio` - lebar stiker relatif thd lebar wajah (1.0 = sama lebar).
- `AnchorYRatio` - posisi vertikal TITIK TENGAH stiker, satuan "tinggi
  wajah" dihitung dari titik jangkar dasar (lihat `AnchorLandmark`). 0 =
  pas di titik jangkar, **negatif** = di ATAS-nya (helm/topi/telinga, mis.
  -0.6 s.d -0.8), **positif kecil** = di bawahnya (kacamata, mis. 0.0 s.d
  0.1).
- `OffsetXRatio` - geser kiri/kanan, biasanya `0` (tengah) sudah pas.
- `AnchorLandmark` (opsional, default `"box"`) - titik jangkar dasar
  SEBELUM `AnchorYRatio`/`OffsetXRatio` diterapkan: `"box"` (dari kotak
  wajah kasar, satu-satunya pilihan di PC net48/Windows 7), `"eyes"` (titik
  tengah kedua mata - presisi utk kacamata), `"eyebrows"` (titik tengah
  alis - presisi utk topi/helm/telinga). Cuma berlaku di net8.0-windows
  SAAT model landmark sudah siap (lihat catatan di bawah) - otomatis
  fallback ke `"box"` di net48 atau selama model belum selesai diunduh.

Lihat 3 contoh nyata di `Assets/SampleFilters/*.stiker` (buka pakai
7-Zip/WinRAR kalau mau intip isinya) - dibuat sendiri via script
Python+Pillow, BUKAN diunduh dari internet (filter Instagram/TikTok asli
proprietary, tidak bisa/boleh diambil dan dipakai di luar aplikasi
mereka).

**Soal akurasi (2026-09-01)**: net8.0-windows (PC Windows 10/11) sekarang
PAKAI 68-titik landmark wajah asli ([dlib](http://dlib.net/) model resmi)
utk 2 hal - (1) stiker ikut MIRING saat kepala miring (bukan nempel lurus
terus), (2) titik jangkar presisi lewat `AnchorLandmark` di atas. Model
(~64MB terkompresi) TIDAK dibundel ke instalasi/update - baru diunduh
sendiri saat filter stiker PERTAMA KALI benar2 dipilih & dirender (bukan
selalu di setiap startup), disimpan permanen di `%LocalAppData%\Presensi\
models\`, dipakai lagi selamanya sesudah itu (lihat
`Filters/LandmarkModelService.cs`). PC net48/Windows 7 TIDAK dapat fitur
ini sama sekali (tetap deteksi kotak wajah polos) - lebih berat di CPU,
sengaja dibatasi ke PC modern saja.

**Kebutuhan tambahan di PC**: deteksi wajah pakai library native
([dlib](http://dlib.net/) via `DlibDotNet`) yang butuh **Visual C++
Redistributable 2017 (x64)** terpasang di PC. Kebanyakan PC Windows 10/11
sudah punya ini bawaan/dari aplikasi lain, tapi **PC Windows 7 lawas perlu
dicek manual** - kalau filter stiker error/tidak muncul di PC tertentu,
ini kemungkinan besar penyebabnya (unduh dari situs resmi Microsoft).

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

## Auto-update (2026-09-01)

App cek rilis terbaru sendiri lewat GitHub REST API (repo ini privat,
jadi BUKAN lewat URL publik biasa yang selalu 404 tanpa kredensial - lihat
`Services/UpdateService.cs`), pakai token GitHub (`GithubToken` di
`appsettings.json`, scope Fine-grained "Contents: Read-only" KHUSUS repo
ini). Kalau ada rilis baru: unduh zip ke folder sementara, tutup app,
timpa file lewat helper `cmd.exe` (bukan PowerShell - kompatibel Windows 7
tanpa WMF terbaru), buka lagi otomatis - TANPA installer, TANPA sentuhan
manual. `appsettings.json` TIDAK PERNAH ikut tertimpa oleh update (tidak
ada di dalam zip rilis).

## Yang BELUM (sengaja, lihat komentar di kode)

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
