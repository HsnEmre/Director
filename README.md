# 🎬 Director

**Director**, yerel yapay zekâ modellerini kullanarak hikâye planlamadan final video çıktısına kadar çok aşamalı üretim sürecini yöneten otonom bir **AI Film Production Pipeline** projesidir.

Projenin temel amacı; LLM, görsel üretim, video üretim ve ses modellerini tek bir kontrollü pipeline altında orkestre ederek uzun biçimli AI video/film üretimini mümkün olduğunca otomatik hale getirmektir.

> 🚧 **Status:** Active Development
> Director aktif olarak geliştirilmektedir.

---

## 🎯 Projenin Amacı

Geleneksel AI video üretiminde kullanıcı genellikle her sahne için ayrı ayrı:

* Hikâye yazar
* Sahne planlar
* Prompt hazırlar
* Görsel üretir
* Video üretir
* Ses oluşturur
* Çıktıları birleştirir

Director bu süreci tek bir üretim pipeline'ı altında yönetmeyi amaçlar.

```text
Project Input
     ↓
Story & Scene Planning
     ↓
Image Prompt Generation
     ↓
Video Prompt Generation
     ↓
Image Generation
     ↓
Video Generation
     ↓
Audio Generation
     ↓
Final Assembly
```

Kullanıcı temel film bilgilerini girdikten sonra Director üretim aşamalarını sıralı ve kontrollü şekilde yürütür.

---

# 🧠 Local-First AI Architecture

Director mümkün olduğunca **local AI** yaklaşımıyla tasarlanmıştır.

Temel bileşenler:

```text
Director
│
├── Ollama
│   └── Qwen
│
├── WanGP
│   ├── Image Generation
│   ├── LTX Video Generation
│   └── Audio Generation
│
├── Validation Engine
├── Repair / Retry Engine
├── Checkpoint System
│
└── Final Media Pipeline
```

Bu yaklaşım sayesinde büyük üretim işlerinin önemli bölümü kullanıcının kendi donanımında gerçekleştirilebilir.

---

# 🤖 LLM Orchestration

Hikâye ve üretim planlama aşamalarında Ollama üzerinden Qwen tabanlı modeller kullanılmaktadır.

Director LLM'e bütün üretim sorumluluğunu tek bir dev prompt içerisinde vermek yerine işlemleri kontrollü aşamalara böler.

Örnek:

```text
Project Requirements
        ↓
Qwen / Ollama
        ↓
Story
        ↓
Scene Plan
        ↓
Image Prompts
        ↓
Video Prompts
```

Her aşamanın çıktısı sonraki aşamanın girdisi haline gelir.

Bu yapı:

* Daha kontrollü üretim
* Daha kolay validation
* Hata durumunda yalnızca ilgili aşamanın yeniden çalıştırılması
* Token kullanımının kontrol edilmesi
* Structured output doğrulaması

gibi avantajlar sağlar.

---

# 📖 Story & Scene Planning

Pipeline'ın ilk önemli aşamalarından biri hikâyenin oluşturulması ve sahnelere ayrılmasıdır.

Director:

```text
Project Input
     ↓
Story
     ↓
Scene 001
Scene 002
Scene 003
...
Scene N
```

şeklinde üretim planı oluşturur.

Her sahne bağımsız bir üretim birimi olarak ele alınır.

Bu sayede uzun filmler tek seferde üretilmek yerine kontrollü sahne parçaları halinde işlenebilir.

---

# 🖼️ Image Prompt Pipeline

Sahne planı tamamlandıktan sonra her sahne için görsel üretim prompt'ları hazırlanır.

Her sahne:

```text
Scene
  ↓
Positive Image Prompt
  ↓
Negative Image Prompt
```

yapısına dönüştürülür.

Director daha sonra bu prompt'ları kullanarak sahnelerin başlangıç görsellerini üretir.

---

# 🎥 Video Prompt Pipeline

Görsel prompt aşamasından bağımsız olarak video üretimi için özel prompt'lar hazırlanır.

```text
Scene
  ↓
Video Positive Prompt
  ↓
Video Negative Prompt
```

Video prompt'larında yalnızca sahnenin görünümü değil;

* Kamera hareketleri
* Karakter hareketleri
* Çevresel hareketler
* Sahne dinamizmi
* Görsel süreklilik
* Temporal davranış

gibi video üretimine özel bilgiler de ele alınabilir.

---

# 🎞️ Sequential Scene Generation

Director sahneleri kontrollü biçimde sırayla üretir.

```text
Scene 001
   ↓
Image
   ↓
Video
   ↓

Scene 002
   ↓
Image
   ↓
Video
   ↓

Scene 003
   ↓
...
```

Bu yaklaşım özellikle uzun AI film üretimlerinde GPU/RAM kaynaklarının kontrollü kullanılmasına yardımcı olur.

---

# 🔄 Character & Scene Continuity

Uzun biçimli AI video üretiminin en önemli problemlerinden biri **continuity**'dir.

Director pipeline'ında sahneler birbirinden tamamen bağımsız değerlendirilmez.

Sistem mümkün olduğunca:

* Karakter görünümünün korunması
* Önceki sahne bağlamının dikkate alınması
* Mekân sürekliliği
* Hikâye sürekliliği
* Sahne geçişlerinin tutarlılığı

üzerinde çalışır.

İlk sahne özel bir başlangıç sahnesi olarak değerlendirilir ve önceki sahne bağımlılığı bulunmaz.

---

# 🎥 WanGP Integration

Director'ın medya üretim katmanı WanGP ile entegre çalışacak şekilde geliştirilmiştir.

```text
Director
    ↓
WanGP
    ↓
Image / Video / Audio Models
```

WanGP bağlantısı üzerinden üretim görevleri oluşturulabilir ve tamamlanan medya çıktıları Director proje yapısına aktarılabilir.

---

# 🎬 LTX Video Generation

Video üretim pipeline'ında LTX tabanlı modeller kullanılabilmektedir.

Mevcut geliştirme ortamında kullanılan modellerden biri:

```text
LTX 2.x
22B Distilled
GGUF Quantized
```

yapısındaki video modelidir.

Director:

```text
Generated Image
      +
Video Prompt
      ↓
LTX
      ↓
Scene Video
```

akışını yönetir.

Start-image tabanlı video üretimi sayesinde oluşturulan sahne görselleri video üretiminin başlangıç noktası olarak kullanılabilir.

---

# 🔊 Audio Pipeline

Director yalnızca görüntü üretimini değil, ses üretim aşamalarını da pipeline içerisinde yönetmek üzere tasarlanmıştır.

Planlanan/tanımlanan yapı:

```text
Scene
  ↓
Dialogue / Narration
  ↓
Audio Generation
  ↓
Scene Audio
  ↓
Final Mix
```

Ses üretimi video üretiminden ayrı bir aşama olarak ele alınır.

Bu sayede:

* Diyalog
* Anlatıcı
* Karakter sesi
* Sahne sesi

gibi bileşenler kontrollü biçimde işlenebilir.

---

# 🛡️ Validation Engine

Director'ın önemli parçalarından biri AI çıktılarının doğrudan doğru kabul edilmemesidir.

Pipeline çıktıları çeşitli validation aşamalarından geçirilebilir.

Örneğin:

```text
LLM Response
     ↓
Parse
     ↓
Validate
     ↓
Valid?
 ┌───┴────┐
Yes       No
 ↓         ↓
Next     Repair
Stage      ↓
          Retry
```

Bu özellikle structured JSON üretimlerinde önemlidir.

---

# 🔧 Repair Pipeline

Model cevabı beklenen formatta değilse Director doğrudan bütün üretimi iptal etmek yerine ilgili çıktıyı onarmaya çalışabilir.

```text
Invalid Output
      ↓
Repair Prompt
      ↓
LLM
      ↓
Validate Again
```

Repair işlemleri mümkün olduğunca küçük ve hedefli tutulur.

Amaç bütün hikâyeyi tekrar üretmek yerine yalnızca hatalı bölümü düzeltmektir.

---

# 🔁 Retry & Fallback

AI modellerinde zaman zaman:

* JSON parse hataları
* Token limitleri
* Eksik cevaplar
* Model timeout
* Media generation failure
* Geçersiz çıktı
* Eksik dosya

gibi problemler oluşabilir.

Director bu nedenle retry/fallback mantığı içermektedir.

```text
Generate
   ↓
Validate
   ↓
Failure
   ↓
Retry
   ↓
Repair
   ↓
Fallback
```

Bu yapı otonom pipeline'ın tek bir model hatasında tamamen durmasını engellemeyi amaçlar.

---

# 💾 Checkpoint Architecture

Uzun AI film üretimleri saatler sürebileceği için pipeline'ın sıfırdan başlaması istenmez.

Director üretim aşamalarını checkpoint mantığıyla takip eder.

Örneğin:

```text
✓ Story
✓ Scene Planning
✓ Image Prompts
✓ Video Prompts
✓ Scene 001 Image
✓ Scene 001 Video
✓ Scene 002 Image
✗ Scene 002 Video
```

Bir hata oluştuğunda sistem mümkün olduğunca kaldığı noktadan devam edebilir.

---

# 🗂️ Project Asset Structure

Her film projesinin medya çıktıları proje bazlı olarak saklanır.

Örnek yapı:

```text
Projects
└── ProjectId
    └── scenes
        ├── Scene-001
        │   ├── image
        │   ├── video
        │   └── audio
        │
        ├── Scene-002
        │   ├── image
        │   ├── video
        │   └── audio
        │
        └── ...
```

Bu yapı medya üretiminin sahne bazlı takip edilmesini kolaylaştırır.

---

# 🗄️ Data Layer

Director'ın uygulama verileri .NET tarafında Entity Framework Core kullanılarak yönetilmektedir.

Mevcut geliştirme ortamında:

```text
.NET
   ↓
Entity Framework Core
   ↓
SQL Server LocalDB
```

kullanılmaktadır.

Film projeleri, sahneler, üretim durumları ve pipeline bilgileri kalıcı olarak saklanabilir.

---

# 🖥️ Application

Director masaüstü odaklı bir .NET uygulamasıdır.

Teknoloji stack'inin temel parçaları:

* C#
* .NET 8
* WPF
* Entity Framework Core
* SQL Server LocalDB
* Ollama
* Qwen
* WanGP
* LTX
* Local AI Models

---

# ⚙️ Example Autonomous Pipeline

Director'ın hedeflediği uçtan uca autonomous workflow:

```text
USER
 │
 │ Project Requirements
 ▼
DIRECTOR
 │
 ├── Story Generation
 │
 ├── Scene Planning
 │
 ├── Image Prompt Generation
 │
 ├── Video Prompt Generation
 │
 ├── Image Generation
 │
 ├── Scene Validation
 │
 ├── Video Generation
 │
 ├── Audio Generation
 │
 ├── Final Mix
 │
 └── Final Assembly
 │
 ▼
FINAL VIDEO
```

Her aşama ayrı olarak takip edilir ve gerektiğinde retry/repair uygulanabilir.

---

# 📺 Real Output

Director pipeline'ının geliştirme ve test süreçlerinde üretilen içerikler gerçek video üretim senaryolarında kullanılmaktadır.

Projeyle ilişkili test/use-case çalışmalarından biri çocuk hikâyeleri ve animasyon üretim pipeline'ıdır.

Bu kullanım senaryosu:

```text
Story
 ↓
Scenes
 ↓
Images
 ↓
Videos
 ↓
Audio
 ↓
Final Episode
```

şeklindeki uzun üretim zincirinin gerçek içerik üzerinde test edilmesini sağlamaktadır.

---

# 🔐 Privacy & Local Processing

Director'ın önemli tasarım hedeflerinden biri üretim süreçlerinin mümkün olduğunca kullanıcının kendi sisteminde çalışabilmesidir.

```text
Local LLM
Local Video Model
Local Image Generation
Local Database
Local Media Storage
```

Bu yaklaşım:

* API bağımlılığını azaltabilir
* Büyük medya üretimlerinde maliyet kontrolü sağlayabilir
* Model seçimi üzerinde daha fazla kontrol sunabilir
* Yerel medya işleme senaryolarına imkân verir

---

# 🚀 Development Goals

Director aktif geliştirme aşamasındadır.

Planlanan ve geliştirilmeye devam edilen alanlar:

* Daha güçlü character continuity
* Scene continuity iyileştirmeleri
* Daha gelişmiş validation
* Otomatik repair stratejileri
* Retry/fallback iyileştirmeleri
* Model-independent generation adapters
* Daha gelişmiş progress tracking
* Production log sistemi
* Media recovery
* Audio pipeline iyileştirmeleri
* Final assembly otomasyonu
* Uzun biçimli autonomous generation
* Daha güçlü checkpoint/resume sistemi

---

# 💻 Development Hardware

Director local AI modelleriyle çalıştığı için güçlü GPU ve yüksek sistem belleğinden faydalanır.

Development/test sistemi:

```text
GPU: NVIDIA GeForce RTX 5080
VRAM: 16 GB

CPU: AMD Ryzen 9 9900X

RAM: 64 GB

Storage: NVMe SSD
```

Büyük modellerde quantization ve CPU/RAM offload teknikleri kullanılabilir.

---

# ⚠️ Current Limitations

AI video üretimi doğası gereği bazı sınırlamalara sahiptir:

* Character consistency her üretimde garanti edilemez.
* Uzun video üretimleri yüksek GPU/RAM kullanabilir.
* Quantized modeller kalite/performans trade-off'u oluşturabilir.
* LLM structured output'ları zaman zaman repair gerektirebilir.
* Büyük context'lerde token limitleri oluşabilir.
* Video generation süreleri kullanılan modele ve donanıma bağlıdır.

Director bu problemlerin mümkün olduğunca pipeline seviyesinde yönetilmesi üzerine geliştirilmektedir.

---

# 📌 Project Status

Director şu anda **aktif geliştirme ve gerçek üretim testleri** aşamasındadır.

Temel sistem:

```text
LLM Planning
     ↓
Scene Pipeline
     ↓
Prompt Generation
     ↓
Image Generation
     ↓
Video Generation
     ↓
Validation / Recovery
     ↓
Audio
     ↓
Final Assembly
```

mimarisi etrafında geliştirilmektedir.

Projenin nihai hedefi, kullanıcıdan alınan yaratıcı girdileri mümkün olduğunca az manuel müdahaleyle tamamlanmış uzun biçimli AI video içeriğine dönüştürebilen **otonom bir yerel AI film üretim sistemi** oluşturmaktır.

---

## 🔒 Repository

This repository is currently **private**.

The project contains active development work, experimental AI orchestration logic and local model integrations.

---

## 📄 License

No public license has been assigned.

All rights reserved.
