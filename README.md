# 🛡️ Anti-Phishing Telegram Bot

Система автоматического обнаружения фишинговых и мошеннических сообщений в Telegram на основе большой языковой модели (LLM) с модульной архитектурой и гибридным подходом к анализу контента.

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp)
![License](https://img.shields.io/badge/license-MIT-blue)

---

##  О проекте

Ежемесячная аудитория Telegram превышает 1 млрд пользователей, и мессенджер превратился в одну из основных площадок для фишинга и мошенничества. Злоумышленники используют транслит, омоглифы, поддельные ссылки, вредоносные файлы, синтезированные голосовые сообщения и изображения с текстом, чтобы обходить традиционные спам-фильтры на основе регулярных выражений.

Данный проект — Telegram-бот, который в реальном времени анализирует входящие сообщения и выносит вердикт: **phishing**, **scam**, **suspicious** или **legitimate**. Система сочетает быстрые детерминированные проверки (Google Safe Browsing, VirusTotal) с глубоким семантическим анализом через LLM.

**Точность классификации на тестовой выборке: 96,6 %.**

---

##  Возможности

-  **Анализ текста** — с предварительной нормализацией транслита и омоглифов
-  **Проверка URL** через Google Safe Browsing API
-  **Сканирование файлов** через VirusTotal API (по SHA-256, с обработкой 404)
-  **Распознавание речи** — офлайн-транскрибация голосовых сообщений (Vosk + FFmpeg)
-  **OCR изображений** — извлечение текста с картинок через PaddleOCR
-  **Парсинг контактов** (vCard) — имя, фамилия, номер телефона
-  **Классификация через LLM** — OpenRouter API (бесплатная модель `openrouter/free`)
-  **Модульная архитектура** — легко заменить LLM, мессенджер или внешние API
-  **Асинхронная обработка** — все операции I/O неблокирующие
-  **Логирование** — `ILogger<T>` с настраиваемым уровнем

---

##  Архитектура

Система построена на принципах модульности и инверсии зависимостей. Выделены три ключевых модуля:


### Модуль 1 — Приём и предобработка
- Извлечение текста, подписей, URL, файлов, голоса, фото, контактов
- Нормализация: `HomoglyphNormalizer` → `TranslitNormalizer`
- Проверка URL через `GoogleSafeBrowsingChecker`
- Сканирование файлов через `VirusTotalFileScanner`
- Транскрибация через `VoskVoiceTranscriber` (FFmpeg → WAV 16 кГц → Vosk)
- OCR через `PaddleOcrEngine` (внешний Python-скрипт)

### Модуль 2 — Формирование промпта
- Шаблон хранится в `Resources/prompt_template.txt`
- Контекст сериализуется в JSON (`WriteIndented = true`)
- Плейсхолдер `{{MESSAGE_CONTEXT}}` заменяется на JSON
- Инструкция требует ответа строго в формате JSON

### Модуль 3 — Принятие решения
- Вызов LLM через `ILlmClient`
- Параметры: `temperature = 0.1`, `max_tokens = 500`, `response_format = json_object`
- Десериализация в `PhishingDecision`
- Отправка предупреждения пользователю, если вердикт ≠ `legitimate`

---

##  Технологический стек

| Компонент | Технология |
|---|---|
| Платформа | .NET 8 (LTS), C# |
| Telegram | `Telegram.Bot` v19.0.0 |
| LLM | OpenRouter API (модель `openrouter/free`) |
| Проверка URL | Google Safe Browsing API v4 |
| Проверка файлов | VirusTotal API v3 |
| Распознавание речи | Vosk (офлайн) + FFmpeg |
| OCR | PaddleOCR (через Python-скрипт), fallback — Tesseract |
| DI | `Microsoft.Extensions.DependencyInjection` |
| Логирование | `Microsoft.Extensions.Logging` |
| JSON | `System.Text.Json` |

---

## 📋 Требования

- **.NET 8 SDK** — [скачать](https://dotnet.microsoft.com/download/dotnet/8.0)
- **FFmpeg** — для конвертации голосовых сообщений
- **Python 3.11 (рекомендованно)** с установленными `paddlepaddle` и `paddleocr` (для OCR)
- **Vosk-модель русского языка** — [скачать](https://alphacephei.com/vosk/models)
- API-ключи: Telegram Bot Token, OpenRouter, Google Safe Browsing, VirusTotal

---

##  Установка и запуск

### 1. Клонирование репозитория

```bash
git clone https://github.com/ВАШ_ЛОГИН/anti-phishing-telegram-bot.git
cd anti-phishing-telegram-bot
```

### 2. Настройка конфигурации

Открой appsettings.json и заполни "*****" собственными API ключами:

```text
{
  "TelegramBotToken": "*****",
  "OpenRouterApiKey": "*****",
  "OpenRouterModel": "openrouter/free",
  "OpenRouterHttpReferer": "http://localhost",
  "OpenRouterAppTitle": "TelegramPhishDetector",
  "GoogleSafeBrowsingApiKey": "*****",
  "VirusTotalApiKey": "*****",
  "VoskModelPath": "Resources/models/vosk",
  "TesseractDataPath": "Resources/tessdata",
  "Ocr": {
    "Primary": "Paddle",
    "FallbackToTesseract": false,
    "Paddle": {
      "PythonPath": ".venv\\Scripts\\python.exe",
      "ScriptPath": "Resources/ocr/paddle_ocr.py",
      "TimeoutSeconds": 90,
      "MinCharsForSuccess": 2
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  }
}
```
## 3. Установка PaddleOCR для распознавания текста на изображениях

Проект использует PaddleOCR через отдельный Python-скрипт:

- C# скачивает изображение из Telegram во временный файл.
- `PaddleOcrEngine` запускает Python из `.venv`.
- Скрипт `Resources/ocr/paddle_ocr.py` возвращает JSON с распознанным текстом.

Tesseract оставлен в проекте как запасной OCR-движок, но fallback можно включать/выключать настройкой `Ocr:FallbackToTesseract`.

### Требования

Нужен Python **3.11 x64**. Не используйте Python 3.14: для него `paddlepaddle` может не иметь совместимого пакета.

Проверить установленные версии Python:

```powershell
py list
```

Ожидаемый вариант:

```text
3.11[-64]  Python 3.11.x
```

### Создание виртуального окружения

Из корня проекта выполните:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --upgrade pip
.\.venv\Scripts\python.exe -m pip install -r "Resources\ocr\requirements.txt"
```

Файл `Resources/ocr/requirements.txt` уже содержит совместимые версии:

```text
paddleocr
paddlepaddle==3.0.0
numpy<2.4,>=1.24
```

Версия `paddlepaddle==3.0.0` используется, потому что более новые версии на Windows CPU могут падать с ошибками oneDNN/PIR.

### Настройка приложения

В `term paper/appsettings.json` должен быть относительный путь к Python из `.venv`:

```json
"Ocr": {
  "Primary": "Paddle",
  "FallbackToTesseract": false,
  "Paddle": {
    "PythonPath": ".venv\\Scripts\\python.exe",
    "ScriptPath": "Resources/ocr/paddle_ocr.py",
    "TimeoutSeconds": 90,
    "MinCharsForSuccess": 2
  }
}
```

`PaddleOcrEngine` умеет находить `.venv` в корне проекта даже при запуске приложения из `bin`.

### Проверка установки

Проверить импорт и инициализацию PaddleOCR:

```powershell
.\.venv\Scripts\python.exe -c "import paddle; import numpy; from paddleocr import PaddleOCR; print('paddle', paddle.__version__); print('numpy', numpy.__version__); PaddleOCR(lang='ru', use_textline_orientation=True); print('init ok')"
```

Если всё установлено правильно, в конце будет:

```text
init ok
```

Первый запуск может занять больше времени: PaddleOCR скачивает модели в `%USERPROFILE%\.paddlex\official_models`.

## 4. Загрузка Vosk-модели

Скачай русскую модель с alphacephei.com/vosk/models (например, vosk-model-small-ru-0.22) и распакуй её в папку проекта. Путь укажи в Vosk:ModelPath.

## 5. Установка FFmpeg

- Вариант A (рекомендуется): используется встроенная копия из NuGet-пакета FFmpegInstaller.Windows.x64.

- Вариант B: укажи абсолютный путь в FfmpegPath.

- Вариант C: убедись, что ffmpeg доступен в PATH.

## 6. Запуск

После установки зависимостей:

```powershell
dotnet run --project "term paper\term paper.csproj"
```

При обработке изображения в логах должны появиться строки:

```text
[OCR] Основной движок: PaddleOCR
[OCR:Paddle] Запуск: ...
[OCR:Paddle] Результат: confidence=..., lines=..., chars=..., text=...
```

### Архивирование проекта

Папку `.venv` можно удалить перед архивированием. Она тяжёлая и восстанавливается командами выше.

Перед архивированием можно выполнить:

```powershell
Remove-Item -Recurse -Force .venv
```

После распаковки проекта нужно заново создать `.venv` и установить зависимости из `Resources\ocr\requirements.txt`.

##  Результаты тестирования

Тестирование проводилось на **24 сообщениях**, покрывающих все основные типы угроз.

| Категория | Тестов | Верно | Ошибок | Accuracy |
|---|---:|---:|---:|---:|
| Легитимные | 5 | 5 | 0 | 100 % |
| Фишинг (ссылки) | 5 | 4 | 1 | 80 % |
| Скам (деньги) | 5 | 5 | 0 | 100 % |
| Транслит / омоглифы | 3 | 3 | 0 | 100 % |
| Сообщения со ссылками | 5 | 5 | 0 | 100 % |
| Голосовые сообщения | 1 | 1 | 0 | 100 % |
| **Итого** | **24** | **23** | **1** | **96,6 %** |

> Единственная ошибка: одно фишинговое сообщение было классифицировано как `SCAM` вместо `PHISHING` (обе категории относятся к угрозам).

### ⏱️ Метрики производительности

- **Время построения промпта** (включая вызовы API): 1–5 с
- **Время ответа LLM**: 1–10 с (зависит от загрузки OpenRouter)
- **Общее время обработки (P95)**: ≤ 15 с

---

##  Ограничения

- **Google Safe Browsing**: 10 000 запросов/день, не обнаруживает zero-day URL
- **VirusTotal**: 500 запросов/день, 4 запроса/минуту; в текущей версии проверка только по хешу (без загрузки файла)
- **OpenRouter**: требует интернет-соединения, данные передаются третьей стороне
- **PaddleOCR**: зависимость от Python и виртуального окружения, высокое потребление памяти
- **Кэширование**: результаты LLM и внешних API не кэшируются между запусками
