# Лабораторная работа №7 — Безопасность распределённой системы

Реализация [PA7](https://github.com/tanyginvv/DISTRIBUTED-PROGRAMMING/tree/pa7)
на основе PA4 (один из разрешённых условием вариантов). Работы lb1–lb6 сохранены.
Выполнены оба задания: парольная защита Redis/RabbitMQ и учётные записи пользователей.

## Возможности

- Регистрация с уникальным логином. Логины регистронезависимы: Alice и alice — один логин.
- Логин: 3–32 латинские буквы, цифры или `_`. Пароль: 8–128 символов, с подтверждением при регистрации.
- Пароли пользователей хранятся в Redis как солёные PBKDF2-хеши ASP.NET Core PasswordHasher.
- Вход по логину и паролю, выход через POST. Восстановление пароля и альтернативный вход отсутствуют.
- Отправка текста доступна только после входа. ID автора берётся из Claims подписанной cookie.
- Автор записывается атомарно вместе с текстом, similarity и outbox задания/события.
- Summary доступна только автору, в том числе до завершения rank. Чужой и отсутствующий ID возвращают 404.
- Анонимный запрос перенаправляется на вход. Переданный клиентом AuthorId не меняет автора.
- Cookie работает на всех веб-экземплярах: общий key ring и идентификатор приложения Valuator.PA7.
- Формы защищены antiforgery-токенами. Cookie имеет HttpOnly и SameSite=Lax, время действия — 2 часа.
- Summary выдаётся с Cache-Control: no-store. ReturnUrl разрешает только локальные переходы.

## Запуск

Нужны Docker с Linux-контейнерами, Docker Compose v2 и PowerShell.
Для локальной сборки нужен .NET SDK 8. Остановите стенд другой лабораторной,
если он занимает порты 8080, 5001, 5002, 6379 или 15672. Из папки `lb7`:

```powershell
./scripts/start.ps1 -Workers 2
```

При первом запуске создаётся `.env` с двумя случайными паролями промежуточного ПО.
Файл исключён из Git и Docker build context; пароли не выводятся в консоль.
При следующих запусках файл сохраняется. Не удаляйте его при сохранённых volumes:
RabbitMQ создаёт начального пользователя только при первом старте пустой БД.

Откройте http://localhost:8080/, перейдите в «Регистрация», создайте аккаунт,
войдите и отправьте текст. Скопируйте адрес Summary и попробуйте открыть его
из другого браузерного профиля под другим пользователем — доступ будет закрыт.
Незавершённая оценка ожидается с обновлением страницы, как в PA4.

```powershell
docker compose logs -f app1 app2 rankcalculator
docker compose logs -f eventslogger1 eventslogger2
./scripts/stop.ps1
```

Остановка сохраняет данные Redis, RabbitMQ и ключи cookie. Проект Compose называется
`rp-lb7`, его volumes независимы от предыдущих лабораторных. Имя/группа на About
настраиваются в `Valuator/appsettings.json`.

## Пароли промежуточного ПО

| Параметр в .env | Получатель | Параметр компонента |
| --- | --- | --- |
| REDIS_PASSWORD | Redis, Valuator, RankCalculator | Redis__Password |
| RABBITMQ_USER | RabbitMQ и все три типа компонентов | RabbitMQ__User |
| RABBITMQ_PASSWORD | RabbitMQ и все три типа компонентов | RabbitMQ__Password |

Redis работает с `requirepass`; healthcheck также аутентифицируется.
RabbitMQ создаёт отдельного пользователя из конфигурации.
В C# нет паролей по умолчанию: отсутствующий параметр вызывает ошибку настройки.
EventsLogger не обращается к Redis и получает только настройки RabbitMQ.

Панель RabbitMQ: http://localhost:15672/. Учётные данные находятся в локальном
`.env`, это **не** логин и пароль пользователя веб-приложения.
Redis доступен на localhost:6379, веб-экземпляры — на localhost:5001/5002.
Все опубликованные порты ограничены localhost.

Если процессы запускаются вне Docker, задайте Redis__Password,
RabbitMQ__User и RabbitMQ__Password в их окружении и укажите адреса сервисов.
Пароли передаются через ConfigurationOptions.Password и ConnectionFactory.Password,
а не конкатенируются с исходным кодом подключения.

## Хранение и авторизация

Redis содержит `USER-{НОРМАЛИЗОВАННЫЙ_ЛОГИН}` с ID, логином и PasswordHash.
Регистрация использует SET NX, поэтому два веб-экземпляра не создадут два аккаунта
с одним логином при одновременных запросах.

Для каждого текста сохраняется `AUTHOR-{ID}`. Значение берётся только из
ClaimTypes.NameIdentifier. При запросе Summary ID текущего пользователя сравнивается
с сохранённым автором до отображения метрик. Автор не передаётся в очередь для
принятия решения о доступе. Работники сохраняют оценку без изменения автора.

Сохраняется архитектура PA4: очередь заданий, несколько работников, два независимых
подписчика событий, outbox и подтверждение публикаций. Сервисные учётные данные
не дают вход в веб-приложение и не выдаются браузеру.

## Проверки

```powershell
dotnet test ds-2024.sln -c Release
./scripts/start.ps1 -Workers 2 -ProcessingDelayMs 250
python scripts/test-middleware.py
./scripts/test.ps1 -TestAsync -TestEvents -TestFailover
```

Тесты с Redis следует запускать до работников, чтобы они не забирали тестовые задания:

```powershell
./scripts/init-env.ps1
docker compose up -d --wait redis
./scripts/test-unit.ps1 -WithRedis
```

Без настройки тестового Redis два интеграционных .NET-теста явно пропускаются.
Браузерный сценарий (Node.js и Chromium):

```powershell
cd browser-tests
npm ci
npx playwright install chromium
npm test
```

Проверяются регистрация, занятый логин в другом регистре, неверный пароль,
чужая/анонимная Summary, подмена автора и cookie, CSRF, выход, повторный вход,
cookie между репликами, запрет внешнего ReturnUrl и запрет кеширования результата.
Middleware-тест проверяет отказ Redis/RabbitMQ без пароля и с неверным паролем,
а также успешный вход с настройками стенда. Полный прогон выполняется в GitHub Actions:
`.github/workflows/lb7.yml`.

## Границы учебного стенда

Это локальная HTTP-конфигурация; пароли промежуточного ПО обеспечивают аутентификацию,
но не шифрование транспорта. Для внешнего размещения нужны HTTPS, TLS для Redis/AMQP,
Secure-cookie и защищённое хранение секретов/key ring. Cookie удаляется при выходе;
централизованный отзыв ранее скопированных cookie и блокировка перебора не реализованы.

## Архитектура и источники

- [C4](docs/C4.md), [редактируемая диаграмма](docs/c4-containers.drawio).
- [Cookie authentication, Microsoft](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie).
- [Redis security](https://redis.io/docs/latest/operate/oss_and_stack/management/security/).
- [RabbitMQ passwords](https://www.rabbitmq.com/docs/passwords).
