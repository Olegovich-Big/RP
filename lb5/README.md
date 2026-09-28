# Лабораторная работа №5 — уведомления браузеру через SignalR

PA5 реализована на основе PA4. Предыдущие лабораторные не изменены.

## Что сделано

1. RankCalculator перед новым вычислением ждёт случайное целое число секунд
   от 3 до 15 включительно: `Random.Shared.Next(3, 16)` и отменяемый `Task.Delay`.
   Уже обработанное повторно доставленное задание использует сохранённый результат.
2. Summary использует официальную JavaScript-библиотеку SignalR и **WebSocket**.
   Готовый результат появляется без перезагрузки страницы и без HTTP-опроса.
3. В Valuator встроен SignalR Hub `/hubs/evaluation` и мост RabbitMQ → SignalR.
   Nginx проксирует WebSocket Upgrade. Redis backplane доставляет сообщения
   браузерам независимо от веб-экземпляра, к которому они подключились.
4. Сохранены два EventsLogger и оба события PA4. Добавлена C4-диаграмма с браузером.

## Запуск

Нужны Docker Desktop с Linux containers и Docker Compose v2.
Остановить предыдущий стенд: `..\lb4\scripts\stop.ps1`, так как порты совпадают.
Из папки `lb5`:

```powershell
.\scripts\start.ps1 -Workers 2
```

Открыть http://localhost:8080/, отправить текст и оставить Summary открытой.
Сначала видна надпись «Оценка содержания не завершена», после вычисления она
заменяется результатом. Задержка вычисления — 3–15 секунд; время ожидания в очереди
и передачи данных может увеличить общее время.

- Веб-экземпляры: http://localhost:5001/ и http://localhost:5002/.
- RabbitMQ: http://localhost:15672/, учебные логин/пароль `valuator` / `valuator-local`.
- Логи: `docker compose logs -f rankcalculator eventslogger1 eventslogger2`.
- Остановка с сохранением данных: `.\scripts\stop.ps1`.
- Имя и группу для About заполнить в `Valuator/appsettings.json`.

Методы балансировки и дополнительный веб-экземпляр сохранены:

```powershell
.\scripts\start.ps1 -Workers 4 -Method least_conn -ThirdInstance
```

В PA5 убран параметр ProcessingDelayMs: случайная задержка обязательна.
Данные находятся в отдельных volumes проекта `rp-lb5`.

## Как проходят уведомления

```text
RankCalculator → Redis: сохранить rank и событие outbox
              → RabbitMQ: RankCalculated
                   ├→ EventsLogger #1
                   ├→ EventsLogger #2
                   └→ очередь browser → BrowserEventBridge в Valuator
                                         → SignalR + Redis backplane
                                         → Nginx WebSocket → браузер
```

Мосты в веб-экземплярах читают общую durable-очередь `valuator.events.browser`.
Один мост получает событие и отправляет актуальное состояние в SignalR-группу
`evaluation:{id}`. Redis backplane доставляет сообщение участникам этой группы
на всех веб-экземплярах. Браузеры других текстов не получают это уведомление.

Клиент принудительно использует WebSockets с `skipNegotiation: true`, сервер также
разрешает только этот транспорт. Поэтому для этой конфигурации не требуется
привязка HTTP-запросов клиента к одному серверу при балансировке. При недоступных
WebSockets страница покажет потерю соединения и будет повторять подключение;
переход на long polling намеренно не включён.

При Subscribe сервер сначала добавляет соединение в группу, затем читает Redis
и возвращает снимок текущего состояния. Это закрывает гонку, когда событие уже
произошло до открытия страницы или пришло во время подписки. Запоздавший снимок
без rank не скрывает уже показанный результат.

После обрыва соединения SignalR переподключается; клиент повторяет Subscribe и
получает снимок. После исчерпания встроенных попыток повторяется новое подключение.
Таймеры JavaScript используются только для восстановления связи, не для опроса
результата. При закрытии страницы соединение закрывается.

Уведомления SignalR не являются долговечным хранилищем: состояние находится в Redis.
Доставка событий RabbitMQ остаётся at-least-once; обновление страницы идемпотентно.
У учебного приложения нет аккаунтов: ID текста, как и в PA4, даёт доступ к результату;
SignalR-группа служит маршрутизацией, а не заменой проверки прав пользователя.

## Проверки

Локальные тесты и сборка (.NET SDK 8, Node.js 20+):

```powershell
dotnet build ds-2024.sln -c Release
dotnet test ds-2024.sln -c Release
cd browser-tests
npm ci
npm run test:unit
cd ..
```

Проверка работающего стенда:

```powershell
.\scripts\start.ps1 -Workers 2
cd browser-tests
npm ci
npx playwright install chromium
npm test
cd ..
.\scripts\test.ps1 -TestAsync -TestEvents -TestFailover
```

Chromium-тесты проверяют настоящее WebSocket-соединение через Nginx, обновление
одного результата одновременно на 8080, 5001 и 5002 без навигации, открытие уже
готового результата и восстановление результата, вычисленного во время отключения
браузера от сети. На время подготовки pending-состояния тесты останавливают
работников и обязательно запускают их обратно.

JavaScript unit-тест проверяет гонку снимка и события, а также игнорирование чужого
ID. Сохранены проверки PA4: расчёт, два логгера, восстановление подписчика и отказ
веб-экземпляра. Отдельный Redis-тест пропускается без `VALUATOR_TEST_REDIS`.

Workflow `.github/workflows/lb5.yml` выполняет эти проверки на реальном Docker-стенде
и в Chromium. Локально Docker отсутствует; результат полного прогона — в Actions.

## Основные файлы

- `RankCalculator/Worker.cs` — случайная задержка и расчёт.
- `Valuator/Hubs/EvaluationHub.cs` — группы подписчиков и актуальный снимок.
- `Valuator/Services/BrowserEventBridge.cs` — мост RabbitMQ → SignalR.
- `Valuator/Pages/Summary.cshtml`, `Valuator/wwwroot/js/summary.js` — динамический UI.
- `nginx/conf/nginx.conf` — WebSocket Upgrade и таймауты прокси.
- `browser-tests` — автоматические проверки в настоящем браузере.
- [C4-диаграмма](docs/C4.md), [редактируемый draw.io](docs/c4-containers.drawio).

Клиент SignalR 8.0.29 поставляется локально в `wwwroot/lib/signalr`, поэтому браузеру
не нужен CDN. Версия и целостность npm-пакета зафиксированы в package-lock.json.
Для обновления локальной копии: `npm run vendor` из browser-tests.
MIT-лицензия взята из официального dotnet/aspnetcore и приложена рядом с библиотекой.

## Источники

- [Задание PA5](https://github.com/tanyginvv/DISTRIBUTED-PROGRAMMING/tree/pa5).
- [SignalR: Redis backplane](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane).
- [SignalR: масштабирование и WebSockets](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale).
- [SignalR: JavaScript-клиент](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client).
