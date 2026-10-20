# Şakir

MSI MEG CoreLiquid S280/S360'ın pompa başlığındaki 2.4" LCD'yi süren Windows
uygulaması. MSI Center'ın 10 görsel limitini ve sabit şablonlarını tamamen
atlar.

Doğrulandı: `S280`, VID `0x0DB0` / PID `0x75B6`, firmware `0x1B`.

---

## Ne yapar

**Sahneler kendiliğinden değişir** — MSI Center'da olmayan asıl özellik:

| Durum | Ekran |
|---|---|
| Oyun çalışıyor (PUBG / CS2) | Oyunun kendi görseli üzerinde GPU/CPU sensör paneli |
| Müzik çalıyor | Albüm kapağı tam ekran + parça bilgisi + canlı ilerleme |
| Boşta | Gösterge paneli (CPU ring, GPU, RAM, saat) |

Sahneyi elle sabitleyebilir veya yansıtma moduna alabilirsin.

**Yansıtma (mirror): belirli bir pencereyi seç**

Arayüzdeki açılır listeden hangi pencerenin yansıtılacağını seçiyorsun. Seçilen
pencere `PrintWindow(PW_RENDERFULLCONTENT)` ile yakalanıyor, yani **başka
pencerelerin altında kalsa bile** panele düşmeye devam ediyor — ekranda açık
tutmak gerekmiyor.

Seçim `sakir.config.json`'a süreç adı + başlık olarak yazılıyor (pencere
tanıtıcıları uygulama kapanınca geçersiz olduğu için), ve hedef uygulama
kapanıp açılırsa kendiliğinden yeniden bulunuyor.

İki gerçek sınır:

- **Simge durumuna küçültülmüş pencere yakalanamaz** — çizecek içeriği yoktur.
  Panelde "waiting for &lt;uygulama&gt;" görünür.
- Donanım hızlandırmalı veya DRM korumalı bazı yüzeyler siyah döner. Böyle bir
  uygulamayla karşılaşırsan çözüm `Windows.Graphics.Capture` API'sine geçmek,
  ama o epey Direct3D bağlantı kodu gerektiriyor.

"Whole screen" seçeneği eski davranışı korur (ekran bölgesi kopyalar).

**Diğerleri**

- Canlı önizleme — panelde ne varsa pencerede aynısı
- Kare hızı (1-10) ve parlaklık ayarı
- Tepsi ikonu; pencereyi kapatmak uygulamayı kapatmaz
- Oturum açılışında otomatik başlatma (yükseltilmiş görev olarak)
- Ayarlar `sakir.config.json` dosyasında, exe'nin yanında — klasörü
  kopyalarsan ayarlar da gider

---

## Kurulum ve çalıştırma

Gereken: MSI Center kurulu olmalı (uygulama onun DLL'lerini kullanır).

```bash
cd src\Sakir
dotnet build -c Release
```

Çıktı: `bin\Release\net8.0-windows10.0.19041.0\win-x86\Sakir.exe`

Donanımı ve sensörleri arayüz açmadan kontrol etmek için:

```bash
Sakir.exe --selftest
```

Komut satırı seçenekleri: `--tray` (pencere açmadan tepsiye), `--selftest`.

### Yönetici hakkı

Uygulama normal yetkiyle çalışır ve her şey çalışır — **tek istisna CPU
sıcaklığı**. LibreHardwareMonitor onu okumak için çekirdek sürücüsü yüklüyor,
bu da yükseltme gerektiriyor. Yükseltilmemişken arayüz "CPU temp: needs admin"
der ve bir "Restart as administrator" düğmesi gösterir.

"Start with Windows" seçeneği görevi `/RL HIGHEST` ile kaydeder, yani oturum
açılışında UAC sormadan yükseltilmiş başlar. Görevi oluşturmak için uygulamayı
bir kez yönetici olarak çalıştırman gerekebilir.

### Oyun arka planları

Öncelik sırası:

1. `backgrounds\pubg.jpg` / `cs2.jpg` (ve `*-logo.png`) — kendi görselin
2. Steam'in kendi önbelleği: `Steam\appcache\librarycache\<appid>\` altındaki
   `library_hero.jpg` ve `logo.png`. İndirme yok, oyunlar kuruluysa zaten diskte
3. Prosedürel tema (gradyan + motif)

Yeni oyun eklemek için `Games\GameCatalog.cs` içindeki listeye süreç adı, Steam
app id ve renkleri ekle.

---

## Disk yazımı: sıfır

MSI'nin `UploadImage_Dynamic` metodu **dosya yolu** alıyor, yani her kare diske
yazılıyordu — 10 fps'te ~109 GB/gün. IL'ini sökünce dosyanın aslında tesadüfi
olduğu görüldü:

```
UploadImage_Dynamic(dosya, yon):
    if (!Path.GetExtension(dosya).Contains(".png")) return false   <- PNG zorunlu
    node = (yon 90|270) ? "M7-S0-O-R1" + ext : "M7-S0-O-R0" + ext
    UpdateFile(dosya, node):
        GetFileBuffer(dosya, out buf, out len)    <- sadece baytlari geri okuyor
        HMI_SendFileNode(len, node);  Thread.Sleep(1)
        HMI_StartSendData(buf, len);  Thread.Sleep(1)   <- byte[] aliyor
```

`HMI_StartSendData` bellek tamponu aldığı için PNG'yi `MemoryStream`'e kodlayıp
son iki adımı doğrudan çağırıyoruz. **Diske hiçbir şey yazılmıyor.** `M7` =
display mode 7 (Dynamic), `R1` = 90/270 rotasyon.

JPEG denendi ve reddedildi: kontrol uzantı üzerinden yapılıyor, içerik değil.

## FPS

MSI kendi dedektörünü gönderiyor ve `DetectFPS` sınıfının IL'i mekanizmayı
açıkça gösteriyor:

- `GetFPS()` → `<sistem sürücüsü>\MSI\MSI Center\Coreliquid\FPS.txt` içindeki
  `[FPS] FPS=n` değerini okur (`-1` = oyun yok)
- `CheckDetectFPS()` → `DetectFPS` adlı süreci arar
- `Initialization()` → yoksa `Coreliquid\DetectFPS.exe`'yi başlatır

Aynısı yapılıyor. Dedektör **yalnızca oyun sahnesi FPS gösterecekken**
başlatılıyor — çalışan oyunlara bağlanarak ölçüm yaptığı için boşuna açık
tutmak doğru olmaz.

## Kare atlama ve nabız

Değişmeyen kare gönderilmiyor; oyun sahnesinde sensörler saniyede bir
değiştiğinden 10 karenin ~9'u atlanıyor.

Ama **hiç göndermemek de olmuyor**: akış saniyede bire düştüğünde panelin kendi
saklı döngüsüne geri döndüğü gözlendi. Bu yüzden 400 ms'den uzun boşluk
bırakılmıyor — değişiklik olmasa bile bir kare gönderiliyor (`beats=` sayacı).

Ayrıca 20 saniyede bir `GetDeviceInfo` ile **okuma** yapılıyor. Sebebi: panel
USB'den düşüp yeniden numaralandığında eski tanıtıcıya yazmak "başarılı"
dönmeye devam ediyor, yani uygulama boşluğa yazdığını fark etmiyor. Yalnızca
geri okuma bunu yakalayabiliyor; yakalanınca cihaz yeniden açılıyor.

## Yazı boyutu (TEXT SIZE)

Arayüzdeki kaydırıcı 1.00x – 1.40x arası. Yalnızca fontları büyütmek çakışmaya
yol açardı, çünkü düzenler piksel koordinatlarıyla yerleştirilmiş. Bunun yerine
`Graphics.ScaleTransform` uygulanıyor ve mantıksal tuval küçültülüyor:

```
ScaleTransform(s, s);  ctx.Width = 320/s;  ctx.Height = 240/s;
```

Sahne normal koordinatlarıyla çiziyor, sonuç panele büyütülmüş olarak
düşüyor — **yazı da düzen de birlikte** büyüdüğü için çakışma olmuyor, ve GDI+
glifleri son boyutunda rasterize ettiği için metin keskin kalıyor.

Bunun bedeli daha az içerik sığması. Sahneler buna göre uyarlandı:

- Dikey konumlar `H`'den türetiliyor, sabit piksel değil
- Alt bloklar **alt kenara** sabitleniyor (oransal değil), yoksa altta ölü bant
  kalıyor
- Dev sayıların punto boyutu `H` ile orantılı
- Sığmayan satırlar çiziliyor yerine **düşürülüyor** (`if (ry + 18 > bottomTop) break;`)

## Mimari

```
Device\CoreLiquidDevice.cs   MSI'nin API_Coreliquid.dll'ini reflection ile surer
Sensors\SystemSensors.cs     CPU yuk/RAM (Win32), CPU sicaklik (LibreHardwareMonitor)
Sensors\GpuSensors.cs        GPU sensorleri (MSI'nin ADLX.dll / NDA.dll'i)
Media\NowPlayingProvider.cs  Windows SMTC - baslik, sanatci, kapak, pozisyon
Games\GameCatalog.cs         Oyun profilleri + Steam gorselleri
Scenes\*.cs                  Dashboard / NowPlaying / Game / Mirror
Core\PanelService.cs         Kare dongusu + otomatik sahne secimi
Core\AppConfig.cs            JSON ayarlar
Core\Autostart.cs            Gorev Zamanlayici kaydi
```

---

## Bilinmesi gerekenler

1. **x86 zorunlu.** MSI'nin `API_Coreliquid.dll` ve `MsiHid.dll` dosyaları
   32-bit. Proje `win-x86` self-contained yayınlanıyor çünkü makinede x86 .NET
   masaüstü runtime'ı yok.

2. **Çalışma dizini MSI Center kökü olmalı, sensörlerden ÖNCE.** MSI'nin bütün
   P/Invoke'ları native DLL'leri göreli yolla arıyor (`Coreliquid\MsiHid.dll`,
   `Coreliquid\ADLX.dll`). `CoreLiquidDevice.EnsureWorkingDirectory()` bunu
   yapar ve sensör katmanından önce çağrılmalı — sırayı kaçırınca GPU sessizce
   "unavailable" olur.

3. **`Assembly.LoadFrom` kullan**, `Load(byte[])` değil. `C_S360_DeviceControl`
   constructor'ı kendi geçici dosya yollarını assembly'nin `Location`'ından
   türetiyor; byte[] ile yüklenince boş kalır ve hata verir.

4. **Framebuffer 320x240 yatay** ve ölçeklenmiyor. Daha dar bir bitmap
   gönderirsen kalan sütunlarda önceki kare durur.

5. **`SwitchMode_Dynamic` HER karede çağrılmalı.** Panel yüklenen tamponu ancak
   o çağrıda ekrana alır. Optimize edip atlamak (100 ms'lik upload'a karşı
   0.6 ms kazanç) ekranı ilk karede dondurur — üstelik upload başarı dönmeye
   devam ettiği için hata hiçbir yerde görünmez.

6. **Cihaz-tarafı video decode yok.** `UploadVideo_Dynamic` bu firmware'de her
   koşulda `False` döner. Hareketli içerik için tek yol `mirror` sahnesi.

7. **USB tavanı ~10 fps** (kare başına ~100 ms upload). Sensörler daha yavaş
   tempoda örneklenir, yoksa kare hızını onlar belirler.

8. **Render döngüsünde bloke olabilecek çağrı olmamalı.** Üç ayrı iş parçacığı
   var ve bu tasarım tercihi değil, yaşanmış bir hatanın sonucu:

   - **render** — sadece çizer, hiçbir sürücüye dokunmaz
   - **sensors** — LibreHardwareMonitor ve ADLX çağrıları (çekirdek sürücüsüne
     gider, uzun sürebilir veya takılabilir)
   - **push** — panele HID yazma

   Panele yazma render döngüsünün içindeyken uygulama donuyordu: **başka bir
   süreç cihazı tutuyorsa HID yazması hiç geri dönmüyor ve hata da vermiyor.**
   Sonuç, hiçbir yerde iz bırakmayan bir kilitlenmeydi — panel son karede
   kalıyor, fps 0'a düşüyor, log temiz görünüyordu. Şimdi yazma ayrı iş
   parçacığında; takılırsa arayüz "PANEL NOT RESPONDING" der, render devam eder.

9. **Tek örnek kuralı (`Local\Sakir.SingleInstance` mutex).** İki örnek
   cihaz için yarışır ve yukarıdaki kilitlenmeyi tetikler. Asıl donma da bundan
   çıkmıştı: "Restart as administrator" eski süreç cihazı bırakmadan yenisini
   başlatıyordu. Yeni örnek mutex'i 5 saniye bekler, böylece yükseltme devri
   sorunsuz olur.

10. **`sakir.log`** exe'nin yanında. Arka planda çalışan bir uygulamada
    bakılacak konsol olmadığı için şart; tekrarlayan hatalar boğmasın diye
    kısıtlanmış (throttled) yazılır.

11. **`GetDisplayMode` canlı ekran durumunu VERMEZ.** Cihazın saklı mod
    profilini döndürür. Dynamic modda kareler kusursuz basılırken saatlerce
    `0 (HWmonitor)` okur ve `SwitchMode_Dynamic` başarılı olduktan sonra da
    değişmez. Panelin bizim karelerimizi gösterip göstermediğine bu değerle
    karar verme — tek güvenilir sinyal push'un başarılı olmasıdır. (Döndürdüğü
    `slideShow` / `count` alanları gerçekten saklı profile ait: örneğin 4
    donanım ekranının 2 saniyede bir dönmesi.)

    Bu satır bir hata avında yanlış ize sürükledi; log'dan bilerek çıkarıldı.

12. Performans sayaçları yerine Win32 (`GetSystemTimes`, `GlobalMemoryStatusEx`)
    kullanılıyor — sayaç adları yerelleştirilmiş olduğu için Türkçe Windows'ta
    İngilizce adlarla çalışmaz.

13. **Cihaza aynı anda iki şey erişmesin.** Uygulama çalışırken `msi-lcd`
    klasöründeki PowerShell tanı script'lerini çalıştırmak push hatalarına yol
    açar. Uygulama bunu 15 ardışık hatadan sonra kendi kendine toparlıyor
    (~4 saniye), ama önce uygulamayı tepsiden kapatmak daha temiz.

---

## Durum

Çalışan ve doğrulanan: cihaz bağlantısı, dört sahne, otomatik geçiş, canlı
önizleme, CPU sıcaklığı (yükseltilmişken, 48 C okundu), GPU sensörleri (AMD
ADLX), SMTC müzik verisi + albüm kapağı, Steam görselleri, tepsi ikonu, ayar
kalıcılığı.

Henüz yok: yansıtma bölgesi için görsel seçici (şu an config dosyasından),
oyun temaları için arayüz editörü, NVIDIA yolunun gerçek donanımda testi
(kod var, bu makine AMD).
