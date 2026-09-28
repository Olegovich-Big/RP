# Задание 1

## Запуск готового решения

Требуются .NET SDK 8 и Redis. При установленном Docker Compose:

```powershell
docker compose up -d
dotnet run --project Valuator --urls http://localhost:5080
```

Открыть http://localhost:5080. Если Redis запущен отдельно, задать адрес в
`ConnectionStrings:Redis` в `Valuator/appsettings.json` или через переменную
окружения `ConnectionStrings__Redis`.

Перед сдачей заполнить `Author:Name` и `Author:Group` в
`Valuator/appsettings.json` — эти значения выводятся на странице About.

### Проверка

```powershell
dotnet test ds-2024.sln --configuration Release
$env:VALUATOR_TEST_REDIS = 'localhost:6379,abortConnect=false'
dotnet test ds-2024.sln --configuration Release
```

Без переменной `VALUATOR_TEST_REDIS` проверка реального Redis пропускается.
С ней дополнительно проверяются сохранение текста и оценок, неизвестный ID,
точность сравнения и восемь конкурентных отправок одного текста.

Ручной пример: отправить `Hello, мир!` — rank равен 3/11, similarity равен 0;
отправить тот же текст ещё раз — similarity равен 1. Обновление страницы
результата не пересчитывает similarity.

### Реализация

- `TextEvaluator` считает долю символов вне диапазонов A–Z, a–z, А–Я, а–я и Ё/ё.
  Пробелы, цифры, пунктуация и буквы других алфавитов неалфавитные.
  Символы Unicode считаются по кодовым точкам: эмодзи считается одним символом.
- Пустой ввод отклоняется, строка из пробелов допустима и получает rank = 1.
- `EvaluationStore` сохраняет `TEXT-{id}`, `RANK-{id}`, `SIMILARITY-{id}`.
  Redis Set `VALUATOR-TEXTS` содержит ранее обработанные тексты. Lua-скрипт
  атомарно проверяет дубликат и сохраняет результат, без сканирования базы.
  Регистр, пробелы и переводы строк при сравнении сохраняются.
- Redis-подключение переиспользуется через DI; операции чтения и записи асинхронные.
- Некорректный ID возвращает HTTP 400, отсутствующий результат — 404,
  недоступный Redis — 503. Docker Compose сохраняет данные в volume с AOF.

Для пошаговой отладки открыть `ds-2024.sln`, выбрать Valuator стартовым проектом,
поставить точки останова в `OnPostAsync`, `CalculateRank` и `OnGetAsync`,
запустить отладку и отправить форму.

## Контекст

**Valuator** - приложение помощник редактора.

Пользователь с помощью формы на главной странице отправляет текст на обработку, после чего перенаправляется на страницу *summary*, где видит результат обработки.

Приложение предоставляет следующие функции:

1. оценивает содержание;
2. проверяет похожесть на другие тексты.

Результат оценки содержания - число *rank* в диапазоне [0..1], равное доле неалфавитных символов в тексте.
Алфавитными считаются символы строчных и прописных букв латинского и русского алфавитов.

Проверка на похожесть делается на основе поиска дубликата текста среди ранее обработанных.
Если найден дубликат, то *similarity* = 1, иначе 0.

## Задание

Первое задание является ознакомительным. Прежде всего нужно ознакомиться с используемыми технологиями и инструментами:

* научиться создавать и запускать Web-приложение на фреймворке Asp.Net Core Pages,
* подключать к проекту Nuget библиотеки,
* запускать пошаговую отладку приложения в среде разработки.

В предоставленном шаблоне приложения необходимо:
1. Указать ваше имя и группу на странице About
2. Дописать недостающий код  *(отмечен комментарием `// TODO: (pa1)`)*
3. В качестве хранилища использовать key-value хранилище Redis.

# Материалы

## Инструменты разработки и программные компоненты

1. [ASP.NET Core 8.0](https://learn.microsoft.com/en-us/aspnet/core/getting-started/?view=aspnetcore-8.0)
2. NoSQL база данных [Redis](https://redis.io/) (официальный docker-образ: [redis](https://hub.docker.com/_/redis))
3. Рекомендуемые IDE: [VS Code](https://code.visualstudio.com/), Rider, Visual Studio

## Статьи

_Это лишь рекомендации, подходящие материалы следует искать самостоятельно_

### Статьи по C#

- [Learn C# in Y minutes](https://learnxinyminutes.com/csharp/)
- Шпаргалки: 1) [Шпаргалка по C#](https://high.tealeaf.su/about-csharp.html); 2) [C# cheatsheet](https://reference-xi.vercel.app/cs.html); 3) [C# Cheatsheet (github.com)](https://github.com/jwill9999/C-Sharp-Cheatsheet)
- [Roadmap for JavaScript and TypeScript developers learning C#](https://learn.microsoft.com/en-us/dotnet/csharp/tour-of-csharp/tips-for-javascript-developers)
- [Roadmap for Java developers learning C#](https://learn.microsoft.com/en-us/dotnet/csharp/tour-of-csharp/tips-for-java-developers)
- [Common C# code conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)

### Статьи по Redis

- [Redis - Docker](https://www.w3schools.io/nosql/redis-docker-setup/)
- [Run Redis with Docker Compose](https://kb.objectrocket.com/redis/run-redis-with-docker-compose-1055)
- [NRedisStack guide (C#/.NET)](https://redis.ranebull.me/docs/latest/develop/clients/dotnet/)
- [C#/.NET guide](https://master--redis-doc.netlify.app/docs/connect/clients/dotnet/)
- [Redis as Primary Database in .NET 8 Web API](https://www.csharp.com/article/redis-as-primary-database-in-net-8-web-ap/)
- [Using StackExchangeRedis to integrate Redis with a C# .NET app](https://duongnt.com/stackexchangeredis/)
