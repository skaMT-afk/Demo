# ИП «ошла работа по полной» — TaskFlow

Команда ООО «тче наш». Учебный проект HSE РБПО 2026: назначение задач, текстовые результаты, приёмка и доработка.

**Вход в комплект EK1:** [паспорт](PROJECT.md) → [требования SR](REQUIREMENTS.md) → [DFD](DFD.md) → [угрозы T](THREATS.md) → [решения D](DESIGN.md) → [проверки V](docs/CHECKS.md).

[Вклад](CONTRIBUTIONS.md) · [Использование ИИ](AI_USAGE.md) · [Статус проверок](docs/VERIFICATION.md) · [Сдача](SUBMISSION.md) · [Подготовка к защите](docs/DEFENSE.md).

Для запуска на ноутбуке и показа ручек: **[инструкция Windows и сценарий демонстрации](docs/WINDOWS_DEMO.md)**. После запуска сервера: `python scripts/demo.py`.

## Запуск

Нужны Docker Engine/Docker Desktop с Linux containers и Compose v2, Python 3.10+ для вспомогательных скриптов. На Windows откройте PowerShell/терминал VS Code в папке, где лежит compose.yaml. Если команда python не найдена, используйте py -3. Интернет нужен для первой сборки образов и загрузки NuGet. Локально устанавливать .NET SDK при Docker-запуске не нужно.

```sh
python scripts/init_env.py
docker compose up -d --build
python scripts/smoke.py
```

Скрипт init_env не перезаписывает существующий .env. Дождитесь окончания сборки; smoke ожидает готовность API до 60 попыток. Успешная проверка печатает PASS и завершает работу с кодом 0. Она создаёт тестовые аккаунты и задачи с уникальными именами. Повторные запуски в ту же минуту могут получить 429 из-за лимита auth; дождитесь следующего окна либо перезапустите изолированный API.

Быстрая ручная проверка в Windows:

```powershell
curl.exe http://127.0.0.1:8080/health
```

В Linux/macOS используйте curl. Ожидается HTTP 200 и JSON:

```json
{"status":"ok","database":"reachable"}
```

Сборка, запуск Compose и smoke подтверждены в GitHub Actions; ссылки и область проверки — в [протоколе](docs/VERIFICATION.md). До защиты отдельно проверь запуск на своём ноутбуке.

Остановка с сохранением данных:

```sh
docker compose down
```

Сброс **только учебных данных** (удаляет volume со всеми локальными аккаунтами и задачами):

```sh
docker compose down -v
```

После сброса повторите запуск. Схема создаётся EnsureCreated, автоматического обновления старой схемы нет.

## Сборка вне Docker

Для проверки компиляции при установленном .NET 8 SDK:

```sh
dotnet restore src/TaskFlow.Api/TaskFlow.Api.csproj
dotnet build src/TaskFlow.Api/TaskFlow.Api.csproj -c Release --no-restore
```

Для запуска вне Docker дополнительно нужна собственная PostgreSQL 16 и переменная ConnectionStrings__Default с Host/Database/Username/Password. Основной воспроизводимый путь для сдачи — Compose; порт БД в нём не опубликован намеренно.

## API

Все тела — application/json. После login используйте заголовок Authorization: Bearer TOKEN. Автор определяется сервером, не передаётся клиентом. UUID пользователей берутся из ответов register. Поля JSON — camelCase, состояния — английские коды из паспорта.

| Метод/путь | Тело | Доступ / успешный статус |
| --- | --- | --- |
| GET /health | — | Публичный / 200 |
| POST /api/auth/register | login, password | Публичный / 201 |
| POST /api/auth/login | login, password | Публичный / 200, accessToken/expiresAt |
| GET /api/me | — | Сессия / 200 |
| POST /api/auth/logout | — | Сессия / 204 |
| POST /api/teams | name | Сессия / 201 |
| GET /api/teams/{id} | — | Член команды / 200 |
| POST /api/teams/{id}/members | userId | Создатель команды / 204 |
| POST /api/teams/{id}/tasks | assigneeId, title, description | Член команды / 201 |
| GET /api/tasks | — | Только связанные задачи, первые 100 / 200 |
| GET /api/tasks/{id} | — | Автор/исполнитель и членство / 200 |
| POST /api/tasks/{id}/submit | result, version | Исполнитель, assigned/rework / 200 |
| POST /api/tasks/{id}/accept | version | Автор, review / 200 |
| POST /api/tasks/{id}/return | version | Автор, review / 200 |

Описание может быть пустой строкой, но поле description обязательно. version берите из последнего GET или ответа операции. При 409 перечитайте задачу и повторно оцените изменившийся результат, не подставляйте новую версию автоматически. Пароль: 12–128 символов; login: 3–32 ASCII буквы/цифры/подчёркивание, без учёта регистра.

## Диагностика

- Docker не отвечает: запустите Docker Desktop и включите Linux containers.
- Порт 8080 занят: измените левый порт в compose (например 127.0.0.1:8081:8080) и задайте API_URL=http://127.0.0.1:8081 для smoke.
- Ошибка соединения с БД после замены .env: пароль существующего volume не меняется автоматически. Верните исходный .env либо удалите только ненужный учебный volume с предупреждением выше.
- Сборка/restore не проходит: проверьте доступ к NuGet и реестрам Microsoft/Docker; не считайте это успехом теста.
- Просмотр состояния: docker compose ps. Логи: docker compose logs api. Не публикуйте секреты/.env.

Workflow [.github/workflows/verify.yml](.github/workflows/verify.yml) собирает и запускает тот же Compose, выполняет smoke, удаляет только тестовый volume CI. Наличие workflow не означает, что он уже прошёл.
