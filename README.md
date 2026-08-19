# TRLG Engine

Sıfır NuGet bağımlılığı ile çalışan, kendi HTML/CSS benzeri şablon dilini
(**TRLG** / **TRSS**) yorumlayan; native SQLite3 C API'yi doğrudan P/Invoke
ile kullanan ve ham `Socket` üzerinden HTTP sunan bir deneysel web motoru.

- Türkçe anahtar kelimelerle yazılan bir şablon dili (TRLG)
- Türkçe anahtar kelimelerle yazılan bir CSS lehçesi (TRSS)
- Native `sqlite3` C API'sine doğrudan P/Invoke bağlantısı (NuGet paketi yok)
- Elle yazılmış minimal HTTP sunucusu (ASP.NET Core kullanılmıyor)

## Gereksinimler

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Native SQLite3 kütüphanesi (işletim sistemine göre):
  - **Windows:** [sqlite.org/download.html](https://www.sqlite.org/download.html) →
    "Precompiled Binaries for Windows" → `sqlite-dll-win-x64-*.zip` indir,
    içindeki `sqlite3.dll` dosyasını proje klasörüne (`.csproj` ile aynı yere) koy.
  - **Linux (Debian/Ubuntu):** `sudo apt install libsqlite3-0`
  - **macOS:** genelde sistemde hazır gelir.

`sqlite3.dll` / `.so` / `.dylib` dosyaları platforma özgü oldukları için
`.gitignore` ile repodan hariç tutulmuştur — her geliştirici kendi işletim
sistemi için indirmelidir.

## Kurulum ve Çalıştırma

```bash
git clone <bu-repo-url>
cd TrlgCompleteEngine
dotnet build
dotnet run
```

Sunucu `http://localhost:8080/` adresinde ayağa kalkar. İlk çalıştırmada
`wwwroot/` klasörü ve örnek `.trlg` / `.trss` dosyaları otomatik oluşturulur
(bu klasör de `.gitignore` içinde — kodun kendisi runtime'da üretiyor).

## TRLG Şablon Dili — Hızlı Referans

```
[! Bu bir yorum satırıdır, ekrana basılmaz !]

[stil src="stil.trss"]

[baslik renk="#00ff88"]Başlık Metni[/baslik]

[kutu genislik="450"]
    [yazi]Merhaba {{degisken}}[/yazi]
    [link hedef="/sayfa"]Bağlantı[/link]
[/kutu]

[kosul sart="{{stok}} == 0"]
    [yazi renk="#ff4d4d"]Tükendi![/yazi]
[/kosul]

[dongu liste="urunler" sayfa-boyutu="5"]
    [yazi]{{adi}} - {{fiyat}}[/yazi]
[/dongu]

[form hedef="/urun-ekle" metod="POST"]
    [girdi tip="text" ad="adi" yerlesim="Ürün adı"]
    [alan ad="aciklama" yerlesim="Açıklama..."]
    [secim ad="kategori"]
        [secenek deger="1"]Elektronik[/secenek]
        [secenek deger="2"]Giyim[/secenek]
    [/secim]
    [buton tip="submit"]Kaydet[/buton]
[/form]
```

### Etiketler

| Etiket | Açıklama |
|---|---|
| `[stil src="..."]` | Sayfaya TRSS stil dosyası bağlar |
| `[baslik]` `[yazi]` | Başlık / paragraf metni (`renk` attribute'u destekler) |
| `[kutu]` | `<div>` konteyneri (`genislik` attribute'u) |
| `[link hedef="..."]` | `<a href>` |
| `[buton tip="submit|button"]` | `<button>` |
| `[form hedef="..." metod="POST"]` | `<form>` |
| `[girdi tip="..." ad="..."]` | `<input>` — `tip`: `text`, `sayi`, `eposta`, `sifre`, `gizli`, `onay` (checkbox), `tekli-secim` (radio) |
| `[alan ad="..."]` | `<textarea>` |
| `[secim ad="..."]` / `[secenek deger="..."]` | `<select>` / `<option>` |
| `[kosul sart="a == b"]` | Koşullu render (`==`, `!=`, `>`, `<`, `>=`, `<=`) |
| `[dongu liste="..." sayfa-boyutu="N"]` | Liste döngüsü, opsiyonel sayfalama (`?sayfa=2`) |

### Kaçış Karakterleri

Metin içinde literal `[` veya `]` yazmak için `\[` ve `\]` kullanılabilir.

### TRSS (Stil Dili)

CSS'in Türkçe anahtar kelimeli hali (`arka-plan`, `renk`, `ic-bosluk`,
`kenarlik`, `yuvarlama`, `hizalama` vb.) — derleme sırasında standart CSS'e
çevrilir. `url(...)` ve tırnak içi string değerler kelime-değiştirmeden
korunur.

## Özellikler

- **Native SQLite** — tek kalıcı bağlantı + lock ile thread-safe erişim
- **XSS koruması** — tüm dinamik veri HTML-encode edilerek basılır
- **Oturum (session) desteği** — `trlg_sid` cookie'si ile
- **Statik dosya sunumu** — `wwwroot/static/` altındaki dosyalar `/static/...` yolundan servis edilir
- **Es zamanlı bağlantı sınırı** — `SemaphoreSlim` ile aşırı yüklenmeye karşı koruma

## Bilinen Sınırlamalar

- Tek dosyalık, üretim (production) ortamı için TLS/HTTPS desteği yok
- SQL parametreleri prepared statement ile bağlanıyor (SQL injection'a karşı güvenli),
  ancak şema migration mekanizması yok
- Oturumlar bellekte tutuluyor — sunucu yeniden başlatıldığında sıfırlanır

## Lisans

Henüz belirtilmedi — bir `LICENSE` dosyası eklemek istersen
[choosealicense.com](https://choosealicense.com/) üzerinden seçebilirsin.
