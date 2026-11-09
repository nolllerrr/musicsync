# Развёртывание и откат MusicSync на VPS

Оба варианта требуют непустой `MUSICSYNC_IMAGE_TAG`. Задайте его в файле, переданном через `--env-file`. Для новой сборки используйте версию развёртываемой ревизии из `Directory.Build.props`, например `MUSICSYNC_IMAGE_TAG=0.1.9`. Если под этой версией уже собран образ другой ревизии, добавьте к тегу короткий хеш коммита. Перед обновлением запишите текущий тег и сохраните образ с этим тегом на VPS для отката. Значение, экспортированное в оболочке, имеет приоритет над файлом.

## Постоянный запуск с автоматическим TLS

Профиль `deploy/docker-compose.acme.yml` запускает MusicSync и Caddy в отдельном Compose-проекте `musicsync`. Порт 8443 принимает HTTPS и WSS, а TCP 80 обслуживает проверку HTTP-01. Caddy выпускает и продлевает сертификат для публичного IPv4 или DNS-имени. Состояние сертификатов хранится в Docker-томе `musicsync_caddy_data`. [Сертификаты Let’s Encrypt для IP](https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability) · [Автоматический HTTPS в Caddy](https://caddyserver.com/docs/automatic-https)

Проверьте на VPS доступность портов 80 и 8443 и отсутствие пересечения сети `172.30.53.0/29` с маршрутами хоста и другими Docker-сетями. В `/opt/musicsync/musicsync.env` укажите тег образа в `MUSICSYNC_IMAGE_TAG`, публичный IP или DNS-имя в `MUSICSYNC_HOST` и адрес интерфейса VPS в `MUSICSYNC_BIND_IP`. По умолчанию порты публикуются на `127.0.0.1`. Docker должен запускаться при загрузке системы.

```bash
cd /opt/musicsync/app
docker compose --env-file ../musicsync.env -f deploy/docker-compose.acme.yml config --images
docker compose --env-file ../musicsync.env -f deploy/docker-compose.acme.yml build server
docker compose --env-file ../musicsync.env -f deploy/docker-compose.acme.yml up -d --no-build
docker compose --env-file ../musicsync.env -f deploy/docker-compose.acme.yml logs --tail 50 edge
```

После выпуска сертификата проверьте `https://<адрес>:8443/health` из сети клиентов. В установщике «Вместе» на каждом ПК укажите `wss://<адрес>:8443/ws`. Оба контейнера запускаются с `restart: unless-stopped`. Активные комнаты хранятся в памяти и создаются заново после перезапуска сервера.

Для остановки проекта выполните `docker compose --env-file ../musicsync.env -f deploy/docker-compose.acme.yml down`. Сохраните Docker-тома при обновлении и повторном запуске.

## Запуск с готовым PFX

Профиль `deploy/docker-compose.yml` использует предоставленный PFX и публикует HTTPS/WSS на 8443. Команды выполняются из отдельной копии проекта, например `/opt/musicsync/app`.

## Подготовка доступа

1. Убедитесь, что локальные проверки и испытание ПК + ноутбук завершены.
2. Проверьте порт на сервере командой `ss -ltn 'sport = :8443'`. Свободный локальный порт и доступность через правила провайдера проверяются отдельно.
3. Определите адрес для WSS. Сертификат должен быть доверен Windows-клиентам и содержать в SAN имя DNS либо IP, который будет в URL. Для DNS настройте имя на VPS и используйте сертификат доверенного УЦ. Если доступ планируется по IP, нужен доверенный сертификат с этим IP в SAN. До готовности сертификата и сетевого доступа оставьте публикацию на loopback.
4. Экспортируйте сертификат с закрытым ключом и цепочкой в PFX и поместите вне исходников. Например, для PEM-файлов `openssl pkcs12 -export -out musicsync.pfx -inkey privkey.pem -in cert.pem -certfile chain.pem`. OpenSSL запросит пароль. Ограничьте чтение PFX владельцем и непривилегированным UID процесса контейнера.

## Запуск

```bash
cd /opt/musicsync/app
umask 077
cp -n deploy/.env.example deploy/.env
# Заполните в deploy/.env MUSICSYNC_IMAGE_TAG, MUSICSYNC_PFX_FILE и MUSICSYNC_PFX_PASSWORD.
docker compose --env-file deploy/.env -f deploy/docker-compose.yml config --images
docker compose --env-file deploy/.env -f deploy/docker-compose.yml build server
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --no-build
docker compose --env-file deploy/.env -f deploy/docker-compose.yml ps
docker compose --env-file deploy/.env -f deploy/docker-compose.yml logs --tail 50 server
```

Проверьте HTTPS локально по тому же имени, что в сертификате. Например, для сертификата на `music.example.net`:

```bash
curl --fail --resolve music.example.net:8443:127.0.0.1 https://music.example.net:8443/health
```

После успешной локальной проверки и согласования внешнего доступа задайте `MUSICSYNC_BIND_IP` как конкретный внешний адрес интерфейса VPS. В сети с NAT используйте соответствующий локальный адрес хоста и проверьте маршрутизацию порта. Примените Compose повторно. В панели провайдера проверьте доступ к TCP 8443 и при возможности ограничьте его IP тестовых клиентов. С внешней сети проверьте HTTPS `/health` и подключение MusicSync по `wss://music.example.net:8443/ws`. Проверка сертификата выполняется штатными средствами Windows.

Лимиты контейнера — 0,45 CPU, 192 МиБ RAM и 96 PID, журнал ротируется после 5 МиБ. Сборка образа потребляет ресурсы сверх лимитов runtime-контейнера. Проверьте запас ресурсов VPS командой `docker stats --no-stream`. При обновлении PFX пересоздайте контейнер в согласованное окно. Активные комнаты при перезапуске теряются.

## Откат

Для выключения только MusicSync:

```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down
```

Для возврата предыдущего образа восстановите его `MUSICSYNC_IMAGE_TAG` и параметры сертификата в файле окружения выбранного варианта. Убедитесь, что `config --images` показывает прежний тег, затем запустите соответствующую команду из `/opt/musicsync/app`.

```bash
# Автоматический TLS через Caddy
docker compose --env-file ../musicsync.env -f deploy/docker-compose.acme.yml up -d --no-build --pull never

# Готовый PFX
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --no-build --pull never
```

Повторите проверку `/health`. Верните временные правила 8443 в исходное состояние после выключения сервиса.
