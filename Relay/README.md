# AnoMech.Relay

A small WebSocket relay for AnoMech's multiplayer mode. It forwards frames between
clients in the same session code (`/session/<code>`) — it doesn't understand AnoMech's
message format at all. Dalamud clients can't reach each other directly (NAT, and
AnoMech firewalls off FFXIV's own server traffic during a scenario), so this process
exists just to get them talking.

**There's no default/public relay.** Every group runs their own — nothing connects
until you type a URL into the Multiplayer window.

## Contents

- [Quick local test](#quick-local-test)
- [Option A — cloud VPS](#option-a--cloud-vps-recommended)
- [Option B — your own PC](#option-b--your-own-pc)
- [Adding TLS (`wss://`)](#adding-tls-wss)
- [Verifying it's reachable](#verifying-its-reachable)
- [What to share with your group](#what-to-share-with-your-group)
- [Security notes](#security-notes)
- [Troubleshooting](#troubleshooting)
- [Configuring the plugin](#configuring-the-plugin)

---

## Quick local test

Same machine or LAN as your test partner? Skip the VPS:

```
cd Relay/AnoMech.Relay
dotnet run -- --port 7890
```

- Host connects to `ws://127.0.0.1:7890`.
- Others on the LAN connect to `ws://<host's-LAN-IP>:7890` (`ipconfig` → IPv4 Address).
- Allow the app through Windows Firewall's private-network prompt if asked.

---

## Option A — cloud VPS (recommended)

Best when your group isn't all on one LAN. Any small Linux box works — a $4–6/mo VPS
is already overkill for a relay this light.

1. **Publish self-contained** (no .NET needed on the VPS):

   ```bash
   cd Relay/AnoMech.Relay
   dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
   ```

2. **Copy it over**:

   ```bash
   scp publish/AnoMech.Relay youruser@your-vps-ip:/home/youruser/anomech-relay
   ```

3. **Run it once to confirm it starts**:

   ```bash
   ssh youruser@your-vps-ip
   chmod +x ~/anomech-relay
   ~/anomech-relay --port 7890
   ```

4. **Open the port**:

   ```bash
   sudo ufw allow 7890/tcp
   ```

   Providers with a network-level firewall (DigitalOcean, AWS, etc.) need the same
   port opened there too — `ufw` alone isn't enough.

5. **Run it as a systemd service** so it survives reboots. Create
   `/etc/systemd/system/anomech-relay.service`:

   ```ini
   [Unit]
   Description=AnoMech multiplayer relay
   After=network.target

   [Service]
   ExecStart=/home/youruser/anomech-relay --port 7890
   Restart=on-failure
   User=youruser

   [Install]
   WantedBy=multi-user.target
   ```

   Then:

   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now anomech-relay
   ```

6. **Point the plugin at it**: your VPS IP or domain (see [TLS](#adding-tls-wss) for
   `wss://`).

---

## Option B — your own PC

Free, but friends outside your LAN need a router port-forward, and your PC has to
stay on for the session.

1. **Publish self-contained**:

   ```
   cd Relay/AnoMech.Relay
   dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
   ```

2. **Allow the port** (PowerShell, as Administrator):

   ```powershell
   New-NetFirewallRule -DisplayName "AnoMech Relay" -Direction Inbound -Protocol TCP -LocalPort 7890 -Action Allow
   ```

3. **Grant the URL reservation**, so `HttpListener` doesn't need an elevated process
   every run (Administrator, once):

   ```powershell
   netsh http add urlacl url=http://+:7890/ user=Everyone
   ```

4. **Port-forward on your router** (external TCP 7890 → your PC's LAN IP, port 7890)
   if anyone joining isn't on your home LAN.

5. **Find your public IP** (search "what is my ip") and share it as the relay
   address. No static IP? A dynamic-DNS service (No-IP, DuckDNS) gives you a stable
   hostname instead.

6. **Run it**: `.\publish\AnoMech.Relay.exe --port 7890`. Closing the console kills
   it — use [NSSM](https://nssm.cc/) or Task Scheduler for something persistent.

---

## Adding TLS (`wss://`)

The relay only speaks plain `ws://`. Fine for testing, but some networks block
unencrypted upgrades — put a reverse proxy in front to terminate TLS.

**[Caddy](https://caddyserver.com/)** is the easiest: automatic Let's Encrypt certs,
almost no config. With a domain pointed at your VPS:

```
relay.yourdomain.com {
    reverse_proxy localhost:7890
}
```

Run `caddy run` (or as a systemd service) and point the plugin at
`wss://relay.yourdomain.com` — no port needed, Caddy handles 443 → 7890.

No domain? nginx + a self-signed cert works, but a domain + Caddy is much less setup
for the same result.

---

## Verifying it's reachable

From another machine (or your phone's data connection):

```powershell
Test-NetConnection -ComputerName your-vps-ip -Port 7890
```

`TcpTestSucceeded : True` means it's open. `False` means the relay isn't running, or
a firewall/port-forward isn't set up — see [Troubleshooting](#troubleshooting).

For a real WebSocket-level check, use
[`websocat`](https://github.com/vi/websocat):

```
websocat ws://your-vps-ip:7890/session/TEST
```

Connects and hangs waiting for input → working.

---

## What to share with your group

- **The relay URL** — same for everyone, doesn't change between sessions.
- **The session code** — assigned by the relay when the host clicks "Host new
  session" (guaranteed not already in use), shown in-window with a Copy button. A
  code with no traffic for 10 seconds is disbanded automatically.

---

## Security notes

- No auth beyond the session code — anyone with the URL and a live code can join
  (capped at 8 peers). Treat it like a party invite link; the host can't kick anyone
  once they've joined.
- The relay doesn't log message contents, only connection open/close and peer counts.
- Running it on a shared machine opens one more port — normal port-hygiene applies.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Disconnected" immediately after Host/Join | Relay isn't running, or the URL/port is wrong. Check the relay's own console/journal output. |
| `Test-NetConnection` fails from outside | VPS firewall/security group isn't open, or the router port-forward doesn't match the PC's current LAN IP (consider a static DHCP lease). |
| Works locally, not for others | Testing with a LAN IP but gave others your public IP without port-forwarding, or vice versa. |
| `HttpListenerException` on startup (Windows) | Missing the `netsh http add urlacl` grant, or another process owns the port — check `netstat -ano \| findstr 7890`. |
| "session full" | 8 peers already connected; host a new session. |
| Connects, nothing happens after Join | Confirm the same relay URL and session code on both ends (case-normalized, but typos happen). |

## Configuring the plugin

Open the Multiplayer window (`/anomech mp`, or the "Multiplayer..." button once a
multiplayer-supported scenario is selected) and type your relay's address into the
**Relay URL** field. Just the address is enough (`relay.example.com`, or
`203.0.113.5:7890` without TLS) — the plugin tries `wss://` first and falls back to
`ws://` only if that relay doesn't support it, telling you which one it used. An
explicit `ws://`/`wss://` also works. Remembered across sessions once set.
