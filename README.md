# Midnight Demon

Node demon of [Midnight Panel](https://github.com/Midnight-Panel/panel). It runs on every node, manages the
Docker containers of the servers and provides console, files, backups and stats.

> Status: **Phase 2** – node token auth, system info, logs, Docker connection and boot sync with the panel.

## Installation

Install it with the [Midnight installer](https://github.com/Midnight-Panel/installer) (choose "Demon"). It builds a
single-file binary, sets up the `midnight-demon` systemd service and, with SSL, a Let's Encrypt certificate.
Afterwards paste the configuration from the node's setup tab with `midnight configure demon`.


- .NET SDK 10.0
- Linux with Docker (target platform)

The demon uses the shared DTOs from the panel repository, so both repositories have to be checked out next to each other:

```
midnight-panel/
├── panel/
└── demon/   # this repository
```

## Development

Create a node in the panel (Admin → Nodes) and copy the configuration from its setup tab, for example:

```json
{
  "panelUrl": "http://localhost:5220",
  "tokenId": "dev",
  "token": "dev-token",
  "httpPort": 8080,
  "sftpPort": 2022,
  "dataPath": "/tmp/midnight",
  "dockerSocket": "unix:///var/run/docker.sock"
}
```

```bash
MIDNIGHT_DEMON_CONFIG=./config.json dotnet run --project src/Midnight.Demon
curl http://localhost:8080/api/health
```

Endpoints (all except `/api/health` need `Authorization: Bearer mdn_<tokenId>.<token>`):

- `GET /api/health` – public liveness check
- `GET /api/system/info` – OS, kernel, CPU, memory, disk of the data path, Docker engine
- `GET /api/system/logs?lines=200` – last demon log lines (kept in memory)
- `/api/servers/{uuid}/files/...` – file manager (list, contents, write, create-folder, rename, copy, delete, compress,
  decompress, chmod, pull, upload, download), everything confined to the server volume
- `/api/servers/{uuid}/backups` – create (202), `{backupUuid}/download`, `{backupUuid}/restore` (202), delete;
  archives are stored in `{dataPath}/backups/{uuid}/{backupUuid}.tar.gz`

Optional file manager limits in the configuration (defaults shown):

```json
"files": { "maxUploadBytes": 1073741824, "archiveTimeoutSeconds": 1800, "pullTimeoutSeconds": 3600, "maxConcurrentPulls": 3 }
```

On start the demon calls `GET /api/nodes/remote/boot` on the panel and retries with backoff until it succeeds.

Tests:

```bash
dotnet test Midnight.Demon.slnx
```

## Open items

- [ ] Container management, installation and power actions (phase 3)

## License

MIT
