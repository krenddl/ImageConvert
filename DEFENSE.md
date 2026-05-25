# ImageForge — подробное описание приложения

Документ для устной защиты. Покрывает: что это, зачем, как устроено,
почему именно так, что происходит при каждом действии пользователя и
при каждом сбое.

---

## 1. Что это и зачем

**ImageForge** — учебный веб-сервис, который принимает изображение от
пользователя и **асинхронно** его обрабатывает: ресайзит до заданного
максимального размера и конвертирует в один из выбранных форматов
(WebP / JPEG / PNG). В UI пользователь сразу получает идентификатор
задачи и видит прогресс **в реальном времени**, без F5 и без ожидания.

### Зачем такая архитектура нужна в реальном мире

Сайты с пользовательским контентом (Instagram, Pinterest, маркетплейсы,
банки с фото документов) сталкиваются с одной и той же проблемой:
люди грузят 5-10 МБ фото с телефона, а сервер должен:

- быстро вернуть ответ клиенту (UX),
- сжать картинку для веба (трафик, скорость загрузки страниц),
- иногда сконвертировать формат (HEIC → JPEG, JPEG → WebP),
- обработать сотни таких запросов параллельно.

Если делать «в лоб» — внутри HTTP-handler'а вызывать обработку, — то:
1. Пользователь ждёт секунды на каждой загрузке.
2. Один API-сервер не справляется с пиками нагрузки.
3. При падении посередине пользователь теряет файл.
4. Невозможно показать прогресс.

ImageForge решает все четыре проблемы через **очередь сообщений** и
**отдельных воркеров**.

---

## 2. Архитектура (упрощённая диаграмма)

```
        Browser (vanilla HTML/CSS/JS)
              │  HTTP upload + WebSocket (SignalR)
              ▼
        ASP.NET Core 8 API ───── publish ────▶ RabbitMQ
              ▲  │                                 │
       SignalR│  │ pub/sub status                  │ consume
        push  │  ▼                                 ▼
              │ Redis  ◀── status + broadcast ── Worker × N
              └────────────────────────────────────┘
                      shared volume "storage"
```

**Компоненты — кто за что отвечает:**

| Компонент | Роль | Почему отдельный процесс |
|---|---|---|
| **API** | Принимает HTTP, валидирует, кладёт в очередь, читает статусы из Redis, отдаёт результаты, держит SignalR-хаб | Лёгкий, быстрый. Одна копия покрывает большую нагрузку |
| **Worker** | Слушает очередь, обрабатывает картинки через ImageSharp, пишет результат и статусы | **Тяжёлый по CPU.** Масштабируется горизонтально — `--scale worker=N` |
| **RabbitMQ** | Очередь сообщений между API и воркерами; гарантирует доставку | Готовый брокер сообщений с at-least-once семантикой |
| **Redis** | Ключ/значение для статусов задач + Pub/Sub для push-уведомлений | In-memory: микросекундные чтения, встроенный TTL |
| **Frontend** | Vanilla HTML+CSS+JS — drag&drop, прогресс-бары, before/after слайдер | Без сборки, без фреймворка — простота, контроль |

Все четыре сервиса крутятся в Docker-контейнерах в одной приватной сети.
Наружу торчат только два порта: `8080` (фронт + API) и `15672`
(RabbitMQ Management UI).

---

## 3. Технологический стек

### Backend
- **.NET 8** (LTS до ноября 2026). Зафиксировано в `global.json`.
- **ASP.NET Core 8** — Web API на минимальных API (Minimal APIs).
- **.NET 8 Worker Service** — `BackgroundService` для фонового процесса.
- **SignalR** (входит в ASP.NET Core) — WebSocket с фолбэками на
  Server-Sent Events / Long Polling.
- **Swashbuckle.AspNetCore** — OpenAPI/Swagger UI на `/swagger`.

### Очередь и хранилище
- **RabbitMQ 3** — брокер сообщений (AMQP 0-9-1).
- **`RabbitMQ.Client` 6.8.1** — официальный клиент для .NET.
- **Redis 7** (в проекте — local Memurai на Windows + Redis в Docker).
- **`StackExchange.Redis` 2.x** — производственный клиент.

### Обработка изображений
- **SixLabors.ImageSharp 3.1.12** — **чистый C#**, без нативных
  зависимостей. Это критично для Docker: образ воркера получается
  маленьким (~80 МБ), без `apt install libgd / libgdiplus / imagemagick`.

### Frontend
- **Vanilla HTML / CSS / JavaScript** — без сборщика, без фреймворка.
- **@microsoft/signalr** — клиентская библиотека SignalR с CDN.
- **Google Fonts**: EB Garamond (заголовки) + Inter (UI) + JetBrains
  Mono (числа и лейблы).
- Дизайн-язык: warm minimal / editorial — тёплый бумажный фон,
  терракотовый акцент, тонкие линии, мягкие тени.

### Инфраструктура
- **Docker + docker-compose** — стек поднимается одной командой.
- **Multi-stage Dockerfile** — separate `build` (SDK) и `final`
  (runtime) стадии, чтобы итоговый образ не содержал компилятора.

---

## 4. Структура проекта

```
ImageForge.sln
├── src/
│   ├── ImageForge.Api/              ASP.NET Core 8 API + SignalR + статика
│   │   ├── Endpoints/
│   │   │   └── ImagesEndpoints.cs   POST/GET /api/images*
│   │   ├── Services/
│   │   │   ├── ImageStorage.cs      where uploads & results live on disk
│   │   │   ├── QueuePublisher.cs    publishes TaskMessage to RabbitMQ
│   │   │   ├── QueueStatsClient.cs  reads RabbitMQ management API
│   │   │   └── TaskStatusBroadcaster.cs  Redis → SignalR bridge
│   │   ├── Hubs/
│   │   │   └── TasksHub.cs          SignalR hub on /hub/tasks
│   │   ├── Dockerfile
│   │   └── Program.cs               DI wiring, middleware pipeline
│   │
│   ├── ImageForge.Worker/           .NET 8 Worker Service
│   │   ├── Services/
│   │   │   ├── WorkerStorage.cs     resolves results folder
│   │   │   └── ImageProcessor.cs    ImageSharp pipeline
│   │   ├── Workers/
│   │   │   └── QueueConsumer.cs     RabbitMQ consumer (BackgroundService)
│   │   ├── Dockerfile
│   │   └── Program.cs
│   │
│   └── ImageForge.Shared/           Contracts shared by Api + Worker
│       ├── Contracts/
│       │   ├── TaskMessage.cs       что летит в RabbitMQ
│       │   ├── TaskStatus.cs        что лежит в Redis
│       │   └── TaskState.cs         "pending" | "processing" | "done" | "failed"
│       ├── Messaging/
│       │   └── RabbitMqOptions.cs   connection settings
│       └── Persistence/
│           ├── RedisOptions.cs      connection + channel + key prefix
│           ├── TaskStatusStore.cs   SET/GET + PUBLISH/SUBSCRIBE
│           └── LifetimeStats.cs     counters in Redis
│
├── frontend/                        index.html + styles.css + app.js
├── storage/                         uploads/, results/ (gitignored, volume в Docker)
├── docker-compose.yml
├── global.json                      .NET 8 SDK pin
├── README.md
└── CLAUDE.md                        исходный бриф проекта
```

**Принципиальное разделение:**

- `ImageForge.Shared` — **«тощий»** проект, на который ссылаются Api и
  Worker. Содержит только то, что должно быть согласовано между двумя
  процессами: формат сообщений в очереди, формат статусов, обёртки
  над Redis. Сам ни на что не ссылается.
- `ImageForge.Api` зависит от `Shared`.
- `ImageForge.Worker` зависит от `Shared`.
- **Api и Worker НЕ зависят друг от друга** — это даёт возможность
  их отдельно деплоить и масштабировать.

---

## 5. Контракты (то, на чём согласованы Api и Worker)

### `TaskMessage` — что Api кладёт в RabbitMQ

```csharp
public sealed record TaskMessage(
    string TaskId,        // GUID без дефисов, общий идентификатор задачи
    string SourcePath,    // абсолютный путь к загруженному файлу
    string TargetFormat,  // "webp" | "jpg" | "jpeg" | "png"
    int?   MaxDimension); // null = не ресайзить
```

Сериализуется `System.Text.Json` в JSON-байты, уезжает в RabbitMQ.

### `TaskStatus` — что лежит в Redis и что отдаёт API

```csharp
public sealed record TaskStatus(
    string  TaskId,
    string  State,        // "pending" | "processing" | "done" | "failed"
    int     Progress,     // 0..100
    string? ResultPath,   // путь к результату; есть только при State=done
    string? Error);       // сообщение об ошибке; есть только при State=failed
```

Хранится в Redis под ключом `imageforge:task:{taskId}` как JSON-строка
с TTL 24 часа. Тот же объект транслируется через SignalR событием
`statusUpdate`.

### Почему именно строковые константы для `State`, а не `enum`

Если `State` — это C# enum, то по умолчанию `System.Text.Json` пишет
его в JSON как число (`0`, `1`, ...). Это плохо: открываешь Redis или
DevTools и видишь магические цифры. Хуже того — добавление нового
значения **в середину enum'а** сдвигает все последующие, и старые
записи начинают читаться как новые состояния.

Строки (`"pending"`, `"processing"`, ...) хранятся в Redis и JSON
человекочитаемо и не зависят от порядка объявления.

---

## 6. Жизненный цикл одной задачи (детально)

Пример: пользователь грузит `photo.jpg` (4 МБ, 4000×3000) с дефолтными
опциями (WebP, 1920px).

### 6.1 На стороне фронта (HTML/JS)

1. Пользователь **дропает файл** на dropzone или выбирает через picker.
2. Файл попадает в массив `staged`. Создаётся `URL.createObjectURL()`
   для preview-картинки, рисуется тумбочка в staging-зоне.
3. Пользователь жмёт **«upload →»**.
4. Создаётся `FormData`, в неё кладутся `file`, `format`, `maxDimension`.
5. `fetch('/api/images', { method: 'POST', body: fd })`.

### 6.2 На стороне API (ASP.NET Core)

Файл: `src/ImageForge.Api/Endpoints/ImagesEndpoints.cs`, метод `UploadAsync`.

1. **Валидация**:
   - `file != null && file.Length > 0`
   - `file.Length <= 20 МБ`
   - `file.ContentType in { image/jpeg, image/jpg, image/png, image/webp }`
   - `format in { webp, jpg, jpeg, png }`
   - `maxDimension >= 0`. Ноль значит «без ресайза», переводим в `null`.

   Если любая проверка падает → `400 Bad Request` с понятным сообщением.

2. **Генерация `taskId`** — `Guid.NewGuid().ToString("N")` — 32 hex-символа
   без дефисов. URL-safe, без коллизий.

3. **Сохранение файла**: `storage/uploads/{taskId}{.ext}` (расширение
   оригинала сохраняется, чтобы Worker знал исходный формат).

4. **Запись начального статуса в Redis**:
   ```
   SET imageforge:task:{taskId} '{"State":"pending","Progress":0,...}' EX 86400
   PUBLISH imageforge:status     '{...тот же JSON...}'
   ```
   Сначала `SET`, потом `PUBLISH` — чтобы любой подписчик, который сразу
   полезет читать ключ, увидел свежее значение.

5. **Публикация в RabbitMQ**:
   ```
   exchange="" routingKey="image-tasks"
   body=JSON.Serialize(TaskMessage)
   DeliveryMode=2 (persistent)
   ```
   Default exchange + routing key = имя очереди = прямая отправка в очередь.

6. **Ответ клиенту**: `200 OK` с `{ "taskId": "..." }`.

   **Это занимает миллисекунды.** Пользователь не ждёт обработки.

### 6.3 На стороне Worker (любая копия, кому RabbitMQ отдаст)

Файл: `src/ImageForge.Worker/Workers/QueueConsumer.cs`, метод `OnMessageAsync`.

1. **Получает сообщение** через `AsyncEventingBasicConsumer`.
2. **Декодирует JSON** в `TaskMessage`.
3. **Меняет статус в Redis на `processing/0`** через
   `TaskStatusStore.SetAndBroadcastAsync` (которая делает SET + PUBLISH).
4. Вызывает `ImageProcessor.ProcessAsync(message, reportProgress, ct)`:
   - `Image.LoadAsync(SourcePath)` — декодирует JPEG в пиксельный буфер.
   - `image.Mutate(x => x.AutoOrient())` — применяет EXIF orientation
     (важно для фото с телефона).
   - `reportProgress(25)` — пушит статус `processing/25` в Redis +
     SignalR.
   - Если нужен ресайз: `image.Mutate(x => x.Resize(...))` с
     `ResizeMode.Max` — вписать в `1920×1920` с сохранением пропорций.
   - `reportProgress(60)`.
   - Подбор энкодера (`WebpEncoder{Quality=80}` / `JpegEncoder` / `PngEncoder`).
   - `image.SaveAsync(resultPath, encoder)` — пишет в
     `storage/results/{taskId}.webp`.
   - `reportProgress(90)`.
5. **Записывает финальный статус**:
   `processing/done/100/resultPath` в Redis + push.
6. **`LifetimeStats.RecordAsync(bytesIn, bytesOut)`** — инкрементирует
   три глобальных счётчика.
7. **`BasicAck(deliveryTag)`** — подтверждает RabbitMQ'у, что
   сообщение обработано → брокер удаляет его из очереди.

Если на любом шаге **исключение**:
- Конкретные типы (`UnknownImageFormatException`,
  `InvalidImageContentException`) переводятся в понятные сообщения.
- Статус ставится `failed` с этим сообщением.
- `BasicNack(requeue: false)` — сообщение **не возвращается** в очередь
  (иначе оно бы зациклилось, повторно валя того же — или другого —
  воркера).

### 6.4 Обратно в браузер через SignalR

Параллельно с workflow выше работает `TaskStatusBroadcaster` — это
`BackgroundService` внутри API. Он:

1. Подписан на Redis-канал `imageforge:status` через
   `IConnectionMultiplexer.GetSubscriber().SubscribeAsync(...)`.
2. На каждое сообщение из канала:
   - Десериализует JSON в `TaskStatus`.
   - Шлёт его в SignalR-группу `task:{taskId}` через
     `IHubContext<TasksHub>.Clients.Group(...).SendAsync("statusUpdate", ...)`.

На фронте:
- Сразу после `POST /api/images` JS вызывает
  `connection.invoke('SubscribeToTask', taskId)`, что добавляет
  WebSocket-соединение этого таба в группу `task:{taskId}`.
- Колбэк `connection.on('statusUpdate', ...)` обновляет прогресс-бар,
  имя стейта и (при `done`) показывает before/after-слайдер с download-кнопкой.

**Полное латентность от записи в Redis до отрисовки в браузере:** ~5-15 мс.

### 6.5 Download

Когда `state === 'done'`, JS подставляет в before/after `<div>`'ы
background-image:
- `before`: `/api/images/{taskId}/source` — стримит оригинал из
  `storage/uploads/`.
- `after`: `/api/images/{taskId}/result` — стримит результат из
  `storage/results/`.

Кнопка `Download` ссылается на тот же `/result` с заголовком
`Content-Disposition: attachment` (через `TypedResults.Stream(..., fileName)`).

---

## 7. Endpoint'ы

| Метод | URL | Назначение |
|---|---|---|
| `POST` | `/api/images` | Загрузка картинки. `multipart/form-data`: `file`, `format`, `maxDimension`. Возвращает `{ taskId }`. |
| `GET`  | `/api/images/{taskId}` | Текущий снимок `TaskStatus` из Redis. `404` если ключа нет (никогда не загружали или истёк TTL). |
| `GET`  | `/api/images/{taskId}/result` | Стримит обработанный файл. `400` если ещё не `done`. |
| `GET`  | `/api/images/{taskId}/source` | Стримит оригинал (для before/after). |
| `GET`  | `/api/stats` | Live-статистика RabbitMQ: `consumers / messagesReady / messagesUnacknowledged`. |
| `GET`  | `/api/lifetime-stats` | Лимфетайм счётчики из Redis: `processed / bytesIn / bytesOut`. |
| `WS`   | `/hub/tasks` | SignalR-хаб. Методы: `SubscribeToTask(taskId)`, `UnsubscribeFromTask(taskId)`. События: `statusUpdate(TaskStatus)`. |
| `GET`  | `/` | Главная страница (index.html). |
| `GET`  | `/swagger` | OpenAPI документация. |

---

## 8. Конфигурация (что и откуда читается)

ASP.NET Core читает конфиг из:
1. `appsettings.json` (общий файл).
2. `appsettings.{Environment}.json` (например, `.Development.json`).
3. Переменные окружения с двойным подчёркиванием как разделителем
   вложенности: `RabbitMq__Host`, `Redis__ConnectionString`.

Каждое следующее значение **перекрывает** предыдущее. В Docker мы
переопределяем хосты через env, ничего не меняя в коде:

```yaml
environment:
  RabbitMq__Host:         rabbitmq          # вместо localhost
  Redis__ConnectionString: redis:6379
  Storage__Root:           /app/storage
  Frontend__Path:          /app/frontend
```

В DI это связывается так (пример для RabbitMQ):

```csharp
builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value);
```

`AddOptions().Bind()` — стандартный паттерн, чтобы класс настроек
автоматически валидировался и можно было получить через `IOptions<T>`.
`AddSingleton` распаковывает `.Value`, чтобы сервисы получали уже
готовый `RabbitMqOptions` без префикса.

---

## 9. Что и где хранится

### В Redis

| Ключ / канал | Тип | Содержимое | Кто пишет / читает | TTL |
|---|---|---|---|---|
| `imageforge:task:{taskId}` | string | JSON `TaskStatus` | Api+Worker пишут, Api читает | 24 ч |
| `imageforge:status` | pub/sub channel | JSON `TaskStatus` | Api+Worker публикуют, Api подписан | — |
| `imageforge:stats:processed_total` | integer | total processed tasks | Worker инкрементит, Api читает | бесконечно |
| `imageforge:stats:bytes_in` | integer | sum of source sizes | Worker инкрементит | бесконечно |
| `imageforge:stats:bytes_out` | integer | sum of result sizes | Worker инкрементит | бесконечно |

### В RabbitMQ

- Очередь `image-tasks` — `durable=true, exclusive=false, autoDelete=false`.
- Сообщения `DeliveryMode=2` (persistent) → записываются на диск.
- Default exchange `""` — сообщение с routingKey="image-tasks" попадает
  напрямую в одноимённую очередь.

### На диске (общий volume `storage`)

```
storage/
├── uploads/
│   └── {taskId}.{ext}    оригинал, как загрузил пользователь
└── results/
    └── {taskId}.{ext}    обработанный, расширение по TargetFormat
```

Volume общий для api-контейнера и всех worker-контейнеров. Api
сохраняет загрузки + отдаёт оба пути через `/source` и `/result`,
Worker читает `uploads/` и пишет `results/`.

---

## 10. Масштабирование — главный пункт защиты

### Команда

```bash
docker compose up --scale worker=3
```

### Что физически происходит

Docker запускает **три копии** одного и того же worker-контейнера:
`claudebars-worker-1`, `-2`, `-3`. У каждой свой IP в Docker-сети,
все три:
- читают одни и те же `appsettings.json` + env,
- подключаются к одному и тому же RabbitMQ по имени `rabbitmq`,
- мапят один и тот же volume `storage:/app/storage`.

С точки зрения OS — три независимых процесса. Они **ничего не знают
друг о друге**.

### Кто дирижирует

**Никто из нас.** Распределение — врождённое свойство RabbitMQ:

1. Каждый Worker делает `BasicConsume` — подписан на `image-tasks`.
2. RabbitMQ хранит список consumers. У нас три.
3. При публикации сообщения брокер решает кому отдать.
4. С `BasicQos(prefetchCount: 1)` — отдаёт **только тому, кто свободен**:
   воркер должен сначала ack-нуть предыдущее сообщение.

### Демонстрация (вживую)

В нашем тесте: **20 загрузок одновременно**, 3 воркера → расклад
**8/7/6 задач**. Никаких race, никаких потерь.

### Почему НЕ масштабируем API

Api — лёгкий, в основном делает ввод/вывод (читать файл, писать в
очередь, отвечать клиенту). Узкое место — Worker (CPU-тяжёлая
обработка картинок). Поэтому скалируем то, что тормозит. Если бы
API не хватало, добавили бы load balancer перед N копиями API + Redis
backplane для SignalR — но это явно out of scope.

---

## 11. Устойчивость (resilience) и failure recovery

### Что происходит при крахе Worker'а во время обработки

Сценарий, проверенный вживую (`docker kill worker-1`):

1. Worker берёт задачу, делает `processing/processing` запись в Redis,
   начинает работать.
2. SIGKILL — процесс умирает. **`BasicAck` не успевает уйти.**
3. RabbitMQ через тайм-аут TCP heartbeat (~30 сек) видит: consumer
   мёртв.
4. **Возвращает unacked-сообщение** обратно в `messagesReady`.
5. Любой живой worker (или этот же, перезапущенный через
   `restart: unless-stopped`) подбирает сообщение.
6. Снова обрабатывает, ack, готово.

**Сообщение не теряется.** Это **at-least-once delivery** — гарантия,
что задача будет выполнена **минимум один раз** (возможно больше — если
worker упал ровно между `SaveAsync` и `BasicAck`, повторная попытка
перезапишет файл).

В нашем случае повторное выполнение **идемпотентно**: тот же `taskId`
→ та же папка `storage/uploads/{taskId}.*` → тот же `storage/results/{taskId}.{format}`.
Никаких побочных эффектов.

### Что происходит при невозможности обработать (битый файл)

- `Image.LoadAsync` бросает `UnknownImageFormatException`.
- В `catch`-блоке вылавливаем по типу, переводим в понятное сообщение
  `"The file is not a recognized image format."`.
- Пишем `failed` статус в Redis с этим Error.
- `BasicNack(requeue: false)` — **не** возвращаем в очередь, иначе
  бесконечный цикл.
- Пользователь видит `state="failed"` и текст ошибки в карточке.

### Что происходит при крахе API

- Все воркеры продолжают работать, очередь сосёт сообщения.
- Просто никто не принимает новые загрузки и не отдаёт результаты.
- При перезапуске API всё восстанавливается — все статусы лежат в
  Redis, очередь в RabbitMQ.

### Что происходит при крахе RabbitMQ

- Api публикует с `DeliveryMode=2` (persistent), очередь `durable=true`
  → данные на диске → переживут перезагрузку.
- Худшее последствие: те задачи, которые api опубликовал между моментом
  падения и моментом запуска нового сообщения, могут потеряться (если
  brokerнэ не успел fsync на диск). Для production-сценариев есть
  publisher confirms — у нас out of scope.

### Что происходит при крахе Redis

- Api начинает возвращать `404` на GET статусов (потому что Memurai
  пуст после рестарта).
- Worker не сможет писать статусы — но он ловит исключение в
  внутреннем try-catch вокруг `SetAndBroadcastAsync`, чтобы не убить
  основную обработку.
- На production добавили бы Redis persistence (AOF) и/или Sentinel.

---

## 12. Frontend архитектура

### Без фреймворка

Зачем без фреймворка: учебный проект, дизайн editorial — простота
важнее повторного использования. Один HTML + один CSS + один JS файл,
все три отдаются через `app.UseStaticFiles` на том же origin, что и API
— нет CORS, нет build-step.

### SignalR JS-клиент

Подключается из CDN:
```html
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js"></script>
```

Логика подписки в `app.js`:
```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hub/tasks')
  .withAutomaticReconnect()
  .build();

conn.on('statusUpdate', (status) => {
  // обновить карточку в DOM
});

await conn.start();
await conn.invoke('SubscribeToTask', taskId);
```

`withAutomaticReconnect()` — встроенная exponential backoff
переподключение при потере связи.

### Полл-фолбэки

Помимо push'а SignalR, фронт ходит за «спокойными» данными:
- `/api/stats` — раз в 2 сек (workers connected).
- `/api/lifetime-stats` — раз в 5 сек (общие счётчики).

GET `/api/images/{id}` тоже остался — он используется как **начальный
снимок**, потому что `SubscribeToTask` подписывает только на **будущие**
обновления.

### Темная тема

CSS-переменные `:root` определяют светлую палитру. Селектор
`[data-theme="dark"]` перебивает их тёмными вариантами. JS добавляет/
удаляет атрибут на `<html>` и сохраняет выбор в `localStorage`. Маленький
inline-скрипт в `<head>` применяет тему **до отрисовки**, чтобы не было
вспышки белого.

### Before/after слайдер

Два `<div>`'а с `background-image: url(.../source)` и `.../result`,
второй с `clip-path: inset(0 0 0 50%)`. JS на mousedown/mousemove
пересчитывает процент позиции и обновляет clip-path + position
вертикальной ручки.

---

## 13. Развёртывание через Docker

### `docker-compose.yml`

Четыре сервиса:

```yaml
services:
  rabbitmq:    image: rabbitmq:3-management   # с web-UI
  redis:       image: redis:7-alpine
  api:         build: src/ImageForge.Api/Dockerfile
  worker:      build: src/ImageForge.Worker/Dockerfile
volumes:
  storage:                                    # named volume для files
```

### Dockerfile (multi-stage)

```dockerfile
# build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ImageForge.Shared/ImageForge.Shared.csproj   src/ImageForge.Shared/
COPY src/ImageForge.Api/ImageForge.Api.csproj         src/ImageForge.Api/
RUN dotnet restore src/ImageForge.Api/ImageForge.Api.csproj
COPY src/      ./src/
COPY frontend/ ./frontend/
RUN dotnet publish src/ImageForge.Api/ImageForge.Api.csproj -c Release -o /app/publish

# runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=build /src/frontend ./frontend
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ImageForge.Api.dll"]
```

Зачем разделение:
- `sdk` слой содержит компилятор и tooling (~700 МБ).
- `aspnet` runtime — только то, что нужно для запуска (~210 МБ).
- В final-стадии копируется только `publish/` — никаких исходников,
  `.git`, кэшей.

Worker использует `dotnet/runtime:8.0` (без ASP.NET) — экономит ещё ~80 МБ.

### Healthchecks и зависимости

```yaml
depends_on:
  rabbitmq:
    condition: service_healthy
```

`service_healthy` означает: контейнер api/worker запустится **только
после** того, как health check rabbitmq пройдёт. У нас:

```yaml
healthcheck:
  test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
  interval: 10s
  timeout: 5s
  retries: 12
  start_period: 30s
```

Это убирает гонку при первом подъёме.

### `restart: unless-stopped`

И у api, и у worker'а — если процесс упал (например, RabbitMQ ещё не
полностью открыл AMQP-порт когда worker стартанул), Docker
автоматически перезапустит. Это нужная страховка для распределённых
систем.

---

## 14. Решения, осознанно принятые

### Pure C# обработка изображений
**ImageSharp** вместо `System.Drawing` или `Magick.NET`. Никаких
нативных зависимостей → маленький Docker-образ, одинаково работает
на Windows/Linux/macOS.

### Один контракт на два мира
`TaskMessage`, `TaskStatus`, `RabbitMqOptions`, `RedisOptions` — всё в
`Shared`. Api и Worker ссылаются на одни и те же типы → невозможна
несинхронность.

### Strings вместо enum'ов в State
Чтобы Redis и JSON оставались человекочитаемыми и устойчивыми к
перестановкам значений.

### Redis Pub/Sub как мост Worker → SignalR
- Не HTTP — нет лишнего endpoint'а и auth между сервисами.
- Не отдельная RabbitMQ очередь — pub/sub для broadcast'а как раз и
  придуман, у нас Redis уже есть.

### `BasicQos(prefetchCount: 1)` для fair dispatch
Без него RabbitMQ просто round-robin'ил бы — быстрые воркеры
простаивали бы, медленные тонули в очереди.

### `requeue: false` для ошибок
Чтобы poison message не зациклил всех воркеров.

### Status seed ДО publish
В UploadAsync сначала `statusStore.SetAndBroadcastAsync(pending)`,
потом `publisher.Publish(...)`. Иначе быстрый worker мог бы поставить
`processing` ДО того, как Api запишет `pending` → мерцание.

### Frontend на том же origin
Один процесс отдаёт и API, и SignalR-хаб, и статику. Нет CORS.

### EXIF AutoOrient в Worker
Чтобы фото с телефона всегда сохранялись в правильной ориентации
независимо от того, как смотрящий обращается с EXIF.

---

## 15. Что осознанно не реализовано (out of scope)

| Что | Почему |
|---|---|
| Аутентификация, аккаунты | Не часть учебной цели — асинхронной обработки. |
| Реляционная БД | Статусы эфемерные, не нужны связи и сложные запросы. Redis покрывает всё. |
| Облачное хранилище (S3) | Локальный диск через volume достаточен; миграция тривиальна. |
| Распределённая трассировка (OpenTelemetry) | Сложность, не учебная. |
| HTTPS | Предполагается reverse-proxy впереди (nginx, traefik). |
| SignalR Redis backplane | Нужен при `--scale api=N`; у нас одна Api. |
| Dead letter queue | Сейчас просто `nack(requeue:false)` — достаточно для учебной. |
| Publisher confirms | Гарантия записи на диск брокером — production-фича. |
| Idempotency на повторные доставки | Worker эффективно идемпотентен — тот же `taskId` пишет в тот же файл. |

Всё это упомянуто в `CLAUDE.md` (секция 9). На защите указать как
**сознательно** не сделано — это плюс, а не минус.

---

## 16. Типичные вопросы на защите

### Q: Почему API и Worker — это разные процессы?
**A:** Для независимого масштабирования. Api быстрая, ей хватает одной
копии. Worker — CPU-тяжёлый, его клонируем по числу нагрузки. Если бы
они были в одном процессе, пришлось бы клонировать всё, добавлять
load-balancer перед API, синхронизировать SignalR-соединения и т.п.

### Q: Что произойдёт, если Worker упадёт во время обработки?
**A:** RabbitMQ через TCP-таймаут увидит, что consumer мёртв, и вернёт
unacked-сообщение в очередь. Другой живой Worker (или этот, после
перезапуска через `restart: unless-stopped`) его подберёт и обработает
повторно. Это at-least-once гарантия. Повторная обработка безвредна,
потому что taskId один и тот же → файл перезаписывается тем же.

### Q: Почему статус в Redis, а не в БД?
**A:** Три причины: (1) данные эфемерные — нужны на сутки, не
навсегда; (2) запросы только по ключу — БД с её SQL-обвязкой
избыточна; (3) Redis работает в RAM → чтения за микросекунды, что
важно для частых polling-фолбэков. Плюс встроенный TTL — не нужно
писать cron на очистку.

### Q: Почему очередь, а не прямой вызов?
**A:** Очередь делает 4 вещи сразу: (1) асинхронность — пользователь
не ждёт обработки; (2) буферизация пиков нагрузки; (3) гарантия
доставки при сбоях воркера; (4) точка масштабирования — добавление
нового worker не требует никаких изменений в API.

### Q: Почему RabbitMQ, а не Kafka / SQS / NATS?
**A:** RabbitMQ — стандартный AMQP-брокер с богатой моделью exchanges,
exactly-once-ack механизмом и Management UI. Kafka заточен под
event-streaming (миллионы сообщений в секунду, реплеи) — overkill для
нашей задачи. SQS привязан к AWS. NATS быстрый, но не имеет
встроенного persist на диск без JetStream. Для академического проекта
RabbitMQ — sweet spot.

### Q: Зачем `BasicQos(prefetchCount: 1)`?
**A:** Без него RabbitMQ при подключении сразу размажет много
сообщений по всем consumers (round-robin), независимо от их реальной
скорости. С `prefetchCount=1` — отдаёт только тому, кто сейчас
свободен. Получается естественная балансировка по реальной CPU-скорости.

### Q: Почему SignalR, а не WebSocket напрямую?
**A:** SignalR — слой абстракции с автоматическим фолбэком (WS → SSE →
Long Polling), автоматическим reconnect и группами для targeted-broadcast.
Если самим писать поверх raw WebSocket, придётся реализовать всё это
руками. Плюс готовая JS-библиотека `@microsoft/signalr`.

### Q: Почему ImageSharp, а не System.Drawing?
**A:** `System.Drawing.Common` обёртка над GDI+ — работает только на
Windows нативно, а на Linux требует libgdiplus, на macOS вообще не
поддерживается с .NET 6. ImageSharp — чистый C#, одинаково работает
везде. В Docker это критично — образ воркера не требует apt-install
никаких системных пакетов.

### Q: Что в Worker'е изменилось по сравнению с EXIF на телефоне?
**A:** `image.Mutate(x => x.AutoOrient())` — применяет EXIF
orientation **в пиксельные данные**, после чего сохраняется уже
повёрнутая картинка. Сам EXIF-тег обычно теряется при конвертации в
WebP/PNG, поэтому если бы не делали AutoOrient, портретные фото
сохранялись бы лежащими на боку.

### Q: Что показывает `consumers: 3, messagesReady: 0`?
**A:** К нашему RabbitMQ подключены 3 worker-консьюмера (3 копии
worker-контейнера). В очереди прямо сейчас 0 сообщений ждут обработки.
Это значит — все 3 воркера простаивают, готовые подцепить любую новую
задачу.

---

## 17. Команды для демо

```bash
# Полная стек
docker compose up --build

# С 3 воркерами для демонстрации масштабирования
docker compose up -d --scale worker=3

# Логи конкретного воркера
docker logs claudebars-worker-1 --tail 20

# Содержимое Redis вживую
docker compose exec redis redis-cli KEYS 'imageforge:task:*'
docker compose exec redis redis-cli GET 'imageforge:task:<id>'

# Очередь RabbitMQ
docker compose exec rabbitmq rabbitmqctl list_queues name messages_ready consumers

# Полная очистка
docker compose down -v
```

Web-интерфейсы:
- **http://localhost:8080** — фронт
- **http://localhost:8080/swagger** — OpenAPI
- **http://localhost:15672** — RabbitMQ Management UI (guest/guest)

---

## 18. История милестонов (что когда было сделано)

```
M1   Solution + 3 проекта (Api / Worker / Shared)
M2   POST /api/images, GET /api/images/{id} (заглушка)
M3   Api ↔ RabbitMQ ↔ Worker
M4   Реальная обработка через ImageSharp
M5   Статусы в Redis (pending → processing → done/failed)
M6   Параметры от клиента + промежуточный прогресс
M7   Push в браузер через SignalR (Redis Pub/Sub bridge)
M8   Vanilla-фронт: drag&drop, карточки, прогресс, before/after
M9   Docker + docker-compose
M10  Горизонтальное масштабирование + live worker fleet UI
M11  Polish: валидация, Swagger, friendly errors, README
+    Clear-completed кнопка, EXIF AutoOrient, staging area,
     lifetime stats, dark mode
```

Видно на `git log --oneline` — один коммит на каждую логическую
единицу работы, последовательное наращивание функциональности с
сохранением работоспособности после каждого шага.
