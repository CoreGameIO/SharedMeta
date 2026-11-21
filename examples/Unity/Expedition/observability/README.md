# Unity Expedition Observability (Prometheus + Grafana)

Локальный стек Prometheus + Grafana в Docker для просмотра метрик `examples/Unity/Expedition/ServerSolution/Expedition.Server` (`/metrics` на порту `5000`).

## Быстрый старт

```powershell
# 1) Запустить Prometheus + Grafana в Docker (с -Open сразу откроет дашборд)
.\examples\Unity\Expedition\observability\up.ps1 -Open

# 2) Запустить сервер Unity Expedition на хосте (:5000)
dotnet run --project examples/Unity/Expedition/ServerSolution/Expedition.Server/Expedition.Server.csproj

# 3) Запустить сцену в Unity (examples/Unity/Expedition/Client) и поиграть
```

Полезные URL:
- **Grafana:** http://localhost:3100/d/sharedmeta-unity-expedition (`admin` / `admin`, анонимный просмотр включён)
- **Prometheus:** http://localhost:9091/targets
- **Сырые метрики сервера:** http://localhost:5000/metrics

## Логи и корреляция по `CallId`

- В **Unity Console** каждый вызов печатается через `IMetaClientCallObserver`:
  `[MetaCall] IExpeditionService.Move 2.3ms success call=<CallId>`
  и логи SharedMeta через `UnityMetaLogger` тоже помечены `[SharedMeta call=<CallId>]`.
- В консоли и `logs/expedition-*.log` сервера (`Serilog` + `{Properties:j}`) тот же `CallId` идёт в scope каждого шага.
- Чтобы включить аналитический лог `SharedMeta.Analytics` (`call_completed`, `session_started`, `session_ended`), задайте перед запуском сервера:
  ```powershell
  $env:SHAREDMETA_ANALYTICS_LOG = "1"
  ```

## Если порты 3000 / 9090 заняты

```powershell
.\examples\Unity\Expedition\observability\up.ps1 -GrafanaPort 3001 -PromPort 9091 -Open
```

## Остановка

```powershell
.\examples\Unity\Expedition\observability\down.ps1          # остановить контейнеры, данные сохранить
.\examples\Unity\Expedition\observability\down.ps1 -Purge   # остановить и удалить volumes
```
