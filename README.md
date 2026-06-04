# PlayCards — Durak Online

[Онлайн-игра в **Дурака** на **Blazor Server + C# + SignalR**.](https://playcards-durak.onrender.com/)

## Что реализовано

- Комнаты с коротким кодом.
- Создание комнаты и вход по коду.
- 2–6 игроков.
- Старт игры только создателем комнаты.
- Колода 36 карт.
- Козырь.
- Реальное время через SignalR.
- Атака.
- Защита.
- Взять карты.
- Пас / бито.
- Таблица результатов: победы, поражения, количество карт.
- Dockerfile.
- Render blueprint config.
- GitHub Actions build workflow.

## Локальный запуск

```bash
dotnet restore
dotnet run
```

Открой:

```text
http://localhost:5000
```

или URL, который покажет `dotnet run`.

## Docker

```bash
docker build -t playcards .
docker run --rm -p 8080:8080 playcards
```

Открой:

```text
http://localhost:8080
```

## Deploy на Render Free

В репозитории уже есть `render.yaml` и `Dockerfile`.

1. Зайди в Render.
2. New → Blueprint.
3. Подключи этот GitHub repo.
4. Render найдёт `render.yaml`.
5. Deploy.

Сервис будет использовать Docker и порт `8080`.

## Важные ограничения MVP

Это первая рабочая версия, без базы данных. Комнаты и результаты хранятся в памяти процесса. После перезапуска сервера состояние сбрасывается.

Что стоит добавить дальше:

- PostgreSQL/SQLite для постоянных аккаунтов и рейтинга.
- Reconnect по player token, а не только по текущему SignalR connection.
- Настройки комнаты: переводной/подкидной, лимит игроков, приватность.
- Наблюдатели.
- Таймер хода.
- Античит-валидация на уровне отдельного domain engine + unit tests.
- Match history.
- Auth через GitHub/Google.

## Архитектура

```text
Program.cs
 ├─ Blazor Server
 ├─ SignalR /gamehub
 └─ GameRoomService singleton

Models/
 ├─ Card.cs
 └─ GameModels.cs

Services/
 └─ GameRoomService.cs

Hubs/
 └─ GameHub.cs

Pages/
 ├─ Index.razor
 ├─ _Host.cshtml
 └─ Error.cshtml

Shared/
 ├─ MainLayout.razor
 └─ CardView.razor
```

## Security note

Состояние руки отправляется персонально каждому SignalR connection. Остальные игроки получают только публичную информацию: имя, количество карт, статус атаки/защиты/паса и счёт.
