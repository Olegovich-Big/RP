# Лабораторная работа №4 — Publisher–Subscriber

Реализация PA4 на основе PA3. Предыдущие лабораторные `lb1`, `lb2`, `lb3` сохранены.
Добавлены события вычисления метрик и новый компонент EventsLogger в двух экземплярах.

## Выполнение требований

| Требование | Реализация |
| --- | --- |
| RankCalculated после сохранения rank | RankCalculator/Worker.cs + общий EventPublisher |
| SimilarityCalculated в POST после сохранения similarity | Valuator/Pages/Index.cshtml.cs |
| ID текста и значение в событии | JSON: Type, TextId, Value |
| Два логгера получают оба события | Fanout exchange и две отдельные durable-очереди |
| Вывод типа, ID, rank/similarity | EventsLogger/EventSubscriber.cs, строка EVENT с JSON |
| Автоматизация запуска/останова | compose.yaml, scripts/start.ps1, scripts/stop.ps1 |
| C4 со всеми компонентами и браузером | docs/C4.md и docs/c4-containers.drawio |

## Запуск

Нужны Docker Desktop (Linux containers) и Docker Compose v2.
Для локальной сборки и модульных тестов — .NET SDK 8.
Перед запуском остановить PA3 (`..\lb3\scripts\stop.ps1`), поскольку порты совпадают.
Далее команды выполняются из папки `lb4`:

```powershell
.\scripts\start.ps1 -Workers 2
```

Открыть http://localhost:8080/ и отправить любой текст.
Прямые веб-экземпляры доступны на 5001 и 5002.
Панель RabbitMQ: http://localhost:15672/ — учебные учётные данные
`valuator` / `valuator-local`; порт панели опубликован только на localhost.

Два EventsLogger запускаются автоматически, независимо от количества работников.
Проверить сообщения в обеих консолях:

```powershell
docker compose logs -f eventslogger1 eventslogger2
```

Пример формата вывода (демонстрационный ID):

```text
EVENT logger1 {"Type":"SimilarityCalculated","TextId":"00000000-0000-0000-0000-000000000001","Value":0,"EventId":"SimilarityCalculated:00000000-0000-0000-0000-000000000001","Metric":"similarity"}
EVENT logger1 {"Type":"RankCalculated","TextId":"00000000-0000-0000-0000-000000000001","Value":0.25,"EventId":"RankCalculated:00000000-0000-0000-0000-000000000001","Metric":"rank"}
```

В eventslogger2 появляются те же типы событий, ID и значения с префиксом logger2.
Формат сообщения в RabbitMQ содержит три поля: `Type`, `TextId`, `Value`.
При выводе логгер добавляет вычисляемые `EventId` и `Metric`, чтобы явно обозначить,
что означает Value: rank или similarity. EventId стабилен для типа события и текста.

```powershell
# Остановка всех компонентов с сохранением данных
.\scripts\stop.ps1

# Другие режимы, унаследованные от PA2/PA3
.\scripts\start.ps1 -Workers 4 -Method least_conn -ThirdInstance
```

Данные проекта `rp-lb4` изолированы от volumes предыдущих лабораторных.
Имя и группу для About заполнить в `Valuator/appsettings.json`.

## Топология RabbitMQ

- `valuator.processing.rank` — общая очередь заданий конкурирующих RankCalculator.
- `valuator.events` — durable exchange типа **fanout**.
- `valuator.events.logger1` — личная durable-очередь первого EventsLogger.
- `valuator.events.logger2` — личная durable-очередь второго EventsLogger.
- Для некорректных событий у каждой подписки своя очередь с суффиксом `.failed`.

Каждая очередь логгера связана с fanout exchange. Поэтому сообщение копируется
обоим подписчикам, а не распределяется между ними. SubscriberId логгеров различаются:
`logger1` и `logger2`. Не следует масштабировать один сервис логгера с тем же ID:
его экземпляры начнут конкурировать за одну очередь.

Обе подписки объявляются до публикации. Если логгер временно выключен, сообщения
ждут его в очереди. После перезапуска он прочитает накопленные события.

## Сохранение и повторная доставка

Метрика и соответствующее событие outbox записываются одним Redis Lua-скриптом:
событие не создаётся раньше записи результата. Публикация persistent-сообщения
ожидает подтверждение RabbitMQ; только затем outbox-запись удаляется.

Первую попытку публикации SimilarityCalculated делает обработчик POST, а
RankCalculated — рабочий процесс после расчёта. При сбое фоновый отправитель
в том же типе компонента повторяет отправку через две секунды.
Valuator обрабатывает только similarity-outbox, RankCalculator — только rank-outbox.
Повторная доставка задания не пересчитывает сохранённое состояние и не создаёт
новое событие вместо уже подтверждённого.

При гонке отправителей или сбое между confirm и удалением outbox возможна
повторная доставка события. Логгеры могут вывести его повторно с тем же EventId.
Это **at-least-once**, не exactly-once. Подтверждение получения идёт после вывода
в консоль; stdout не является отдельным гарантированно долговечным хранилищем.
Redis и RabbitMQ в учебной конфигурации остаются одиночными сервисами.

## Проверки

```powershell
dotnet build ds-2024.sln -c Release
dotnet test ds-2024.sln -c Release
.\scripts\start.ps1 -Workers 2 -ProcessingDelayMs 250
.\scripts\test.ps1 -TestAsync -TestEvents -TestFailover
```

Тесты стенда проверяют:

- распределение HTTP-запросов и общие ключи форм;
- pending при остановленных работниках и последующее вычисление rank;
- выполнение заданий несколькими RankCalculator;
- правильные rank и similarity для нового и повторного текста;
- получение **обоих типов событий обоими логгерами** с правильным ID и значением;
- остановку logger2, работу logger1 и доставку пропущенных событий logger2 после запуска;
- доступность приложения при остановке одного веб-экземпляра.

Модульные тесты проверяют подсчёт rank, контракт событий, некорректные сообщения
и общие ключи форм. Отдельный Redis-тест включается переменной `VALUATOR_TEST_REDIS`
для тестового Redis; без неё явно пропускается.

Workflow `.github/workflows/lb4.yml` проверяет реальный Docker-стенд в GitHub Actions.
Локально Docker не установлен; статус полного прогона смотреть во вкладке Actions.

Ручной эксперимент с подписчиком:

```powershell
docker compose stop eventslogger2
# Отправить текст через браузер: logger1 получает оба события
docker compose logs --tail 20 eventslogger1
docker compose start eventslogger2
# logger2 получает накопленные события
docker compose logs --tail 20 eventslogger2
```

## C4 для защиты

[Диаграмма контейнеров](docs/C4.md) включает браузер, Nginx, Valuator,
RankCalculator, RabbitMQ, Redis, два EventsLogger и общее хранилище ключей.
[Файл draw.io](docs/c4-containers.drawio) можно открыть в diagrams.net или draw.io Desktop.

## Источники

- [Условие PA4](https://github.com/tanyginvv/DISTRIBUTED-PROGRAMMING/tree/pa4).
- [RabbitMQ Publish/Subscribe (.NET)](https://www.rabbitmq.com/tutorials/tutorial-three-dotnet).
