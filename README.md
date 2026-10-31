# Midnight Daemon

Node daemon of [Midnight Panel](https://github.com/Midnight-Panel/panel). It runs on every node, manages the
Docker containers of the servers and provides console, files, backups and stats.

> Status: **Phase 2** – node token auth, system info, logs, Docker connection and boot sync with the panel.

## Installation

Install it with the [Midnight installer](https://github.com/Midnight-Panel/installer) (choose "Daemon"). It builds a
single-file binary, sets up the `midnight-daemon` systemd service and, with SSL, a Let's Encrypt certificate.
Afterwards paste the configuration from the node's setup tab with `midnight configure daemon`.


- .NET SDK 10.0
- Linux with Docker (target platform)

The daemon uses the shared DTOs from the panel repository, so both repositories have to be checked out next to each other:

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
MIDNIGHT_DAEMON_CONFIG=./config.json dotnet run --project src/Midnight.Daemon
curl http://localhost:8080/api/health
```

Endpoints (all except `/api/health` need `Authorization: Bearer mdn_<tokenId>.<token>`):

- `GET /api/health` – public liveness check
- `GET /api/system/info` – OS, kernel, CPU, memory, disk of the data path, Docker engine
- `GET /api/system/logs?lines=200` – last daemon log lines (kept in memory)

On start the daemon calls `GET /api/nodes/remote/boot` on the panel and retries with backoff until it succeeds.

Tests:

```bash
dotnet test Midnight.Daemon.slnx
```

## Open items

- [ ] Container management, installation and power actions (phase 3)

## License

MIT
