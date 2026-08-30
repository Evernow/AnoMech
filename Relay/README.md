# AnoMech.Relay

Small standalone WebSocket relay for AnoMech's multiplayer mode. It knows nothing
about AnoMech's message format — it just forwards any text frame one connected
client sends to every other client connected under the same session code
(`ws://host:port/session/<code>`). All lobby/gameplay logic lives in the plugin;
this process only exists because Dalamud clients can't reach each other directly
(most players are behind NAT, and AnoMech's `ZoneSession` firewall deliberately
cuts the client off from FFXIV's own server traffic during a scenario, so that
channel can't be reused either).

**There is no default/public relay.** Every group runs their own — the plugin
will not connect anywhere until you type a URL into the Multiplayer window. This
doc covers how to stand one up.

## Contents

- [Quick local test](#quick-local-test)
- [Option A — host on a cloud VPS](#option-a--host-on-a-cloud-vps-recommended)
- [Option B — host from your own PC](#option-b--host-from-your-own-pc)
- [Adding TLS (`wss://`)](#adding-tls-wss)
- [Verifying it's reachable](#verifying-its-reachable)
- [What to share with your group](#what-to-share-with-your-group)
- [Security notes](#security-notes)
- [Troubleshooting](#troubleshooting)

---

## Quick local test

If everyone testing is on the same machine or same LAN (e.g. you and one
friend both physically at your place), you don't need a VPS at all:

```
cd Relay/AnoMech.Relay
dotnet run -- --port 7890
```

- On the host's own PC, the plugin connects to `ws://127.0.0.1:7890`.
- Anyone else on the same LAN connects to `ws://<host's-LAN-IP>:7890` (find the
  LAN IP with `ipconfig` on the host — the `IPv4 Address` under your active
  network adapter, usually `192.168.x.x`).
- No firewall/port-forwarding needed for same-LAN use beyond allowing the app
  through Windows Firewall's *private network* prompt if one pops up.

This is the fastest way to confirm everything works end to end before setting
up anything internet-facing.

---

## Option A — host on a cloud VPS (recommended)

Best if your group isn't all on one LAN. Any small Linux VPS works — the relay
is trivially light (it just forwards text frames); a $4–6/mo box with 512MB–1GB
RAM from any provider (DigitalOcean, Hetzner, Vultr, Linode, AWS Lightsail,
etc.) is overkill already.

1. **Publish a self-contained build on your own dev machine** (no .NET needs
   to be installed on the VPS this way):

   ```bash
   cd Relay/AnoMech.Relay
   dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
   ```

   This produces a single executable at `publish/AnoMech.Relay`.

2. **Copy it to the VPS**:

   ```bash
   scp publish/AnoMech.Relay youruser@your-vps-ip:/home/youruser/anomech-relay
   ```

3. **Make it executable and do a test run**:

   ```bash
   ssh youruser@your-vps-ip
   chmod +x ~/anomech-relay
   ~/anomech-relay --port 7890
   ```

   Leave it running in the foreground for now — you'll turn it into a proper
   service in the next step once you've confirmed it starts without errors
   (`Ctrl+C` to stop it).

4. **Open the port in the VPS firewall.** Most providers default to `ufw` on
   Ubuntu/Debian images:

   ```bash
   sudo ufw allow 7890/tcp
   sudo ufw status
   ```

   If your provider also has a separate network-level firewall/security-group
   UI (DigitalOcean, AWS, etc.), open the same port there too — `ufw` alone
   isn't enough on providers that filter at the network edge.

5. **Run it as a systemd service** so it survives reboots and SSH logouts.
   Create `/etc/systemd/system/anomech-relay.service`:

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
   sudo systemctl status anomech-relay   # confirm it's "active (running)"
   journalctl -u anomech-relay -f        # tail its logs
   ```

6. **Point the plugin at it**: `ws://your-vps-ip:7890` (or your domain name if
   you've pointed one at it — see [TLS](#adding-tls-wss) below for `wss://`).

---

## Option B — host from your own PC

Works if you don't want to pay for a VPS and don't mind your own machine being
the always-on party for the session. Friends outside your LAN need you to
**port-forward** on your router, which is more setup than a VPS but free.

1. **Publish a self-contained Windows build**:

   ```
   cd Relay/AnoMech.Relay
   dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
   ```

2. **Allow the port through Windows Firewall** (run PowerShell as
   Administrator):

   ```powershell
   New-NetFirewallRule -DisplayName "AnoMech Relay" -Direction Inbound -Protocol TCP -LocalPort 7890 -Action Allow
   ```

3. **Grant the URL reservation** so `HttpListener` can bind a non-loopback
   prefix without running the whole process elevated every time (run once, as
   Administrator):

   ```powershell
   netsh http add urlacl url=http://+:7890/ user=Everyone
   ```

   (Skip this if you're fine just always launching the relay from an elevated
   terminal instead.)

4. **Port-forward on your router**, if anyone joining isn't on your home LAN:
   log into your router's admin page (commonly `192.168.0.1` or `192.168.1.1`)
   and forward external TCP port 7890 to your PC's LAN IP, port 7890. The
   exact menu (often "Port Forwarding" or "Virtual Server") varies by router —
   search your router model + "port forwarding" if you're not sure where it
   lives.

5. **Find your public IP** (e.g. search "what is my ip" in a browser) and give
   that out as the relay address: `ws://<your-public-ip>:7890`.

   If your ISP doesn't give you a static public IP (most residential
   connections don't), it can change over time and everyone's saved URL will
   go stale. A free dynamic-DNS service (No-IP, DuckDNS, etc.) gives you a
   stable hostname that follows your IP automatically — worth setting up if
   you'll host recurring sessions.

6. **Run it**: `.\publish\AnoMech.Relay.exe --port 7890`. It has to keep
   running for the duration of your session — closing the console kills it.
   For something more persistent than "leave a window open," wrap it as a
   Windows service with [NSSM](https://nssm.cc/) or run it via Task Scheduler
   with "run whether user is logged in or not."

---

## Adding TLS (`wss://`)

The relay itself only speaks plain `ws://` — no certificate handling built in.
Plain WebSocket is fine for testing, but some networks/corporate proxies block
unencrypted upgrades, and browsers/other tools generally expect `wss://` for
anything crossing the public internet. The standard fix is a reverse proxy
that terminates TLS and forwards to the relay's plain port.

**[Caddy](https://caddyserver.com/)** is the simplest option — it gets you
automatic Let's Encrypt certificates with almost no config. If you have a
domain pointed at your VPS, a `Caddyfile` like this is enough:

```
relay.yourdomain.com {
    reverse_proxy localhost:7890
}
```

Run `caddy run` (or install it as a service — Caddy has first-class systemd
support) and point the plugin at `wss://relay.yourdomain.com` (no port needed —
Caddy handles 443 → 7890 internally).

Without a domain, you can still terminate TLS with a self-signed cert via
nginx, but a domain + Caddy is far less setup for the same result — a domain
from any registrar costs about as much as the VPS itself.

---

## Verifying it's reachable

Before dragging friends into troubleshooting, confirm the relay answers from
outside its own machine.

From another machine (or your phone's data connection, to rule out LAN-only
firewall rules), a quick PowerShell check:

```powershell
Test-NetConnection -ComputerName your-vps-ip -Port 7890
```

`TcpTestSucceeded : True` means the port is open and something's listening.
`False` almost always means either the relay isn't running, the VPS/router
firewall isn't open, or (for Option B) the port forward isn't set up correctly
— see [Troubleshooting](#troubleshooting).

A full WebSocket-level check (confirms the relay's HTTP upgrade handshake
works, not just that the TCP port is open) needs a WebSocket-aware tool, e.g.
[`websocat`](https://github.com/vi/websocat):

```
websocat ws://your-vps-ip:7890/session/TEST
```

If it connects and hangs waiting for input (rather than erroring immediately),
the relay is working.

---

## What to share with your group

Two things, out of band (Discord, whatever) — the plugin has no discovery
service, so both have to be communicated manually:

1. **The relay URL** (`ws://...` or `wss://...`) — same for everyone, doesn't
   change between sessions unless you tear down/move the relay.
2. **The session code** — assigned by the relay (not chosen locally) each time
   the host clicks "Host new session," guaranteeing it's not already in use by
   another active session on that relay; shown in-window with a Copy button
   once the relay responds. A session with no traffic at all for 10 seconds
   (e.g. a host who requested a code but never actually connected/started) is
   disbanded automatically, freeing the code back up.

---

## Security notes

- No authentication beyond the session code itself. Anyone who has both the
  relay URL and a live session's code can join that session (capped at 8
  peers). Treat the code like a party invite link — share it only with people
  you're inviting to that specific run, and note the host can't kick someone
  once they've joined a slot in this version.
- The relay doesn't log or persist message contents, only connection
  open/close and peer counts per session (see its console/journal output).
- Running it on a machine you also use for other things exposes one more open
  port on that machine — normal port-hygiene considerations apply (don't
  reuse a port something else is already listening on, don't leave it
  forwarded on your router longer than you're actually using it if you're
  security-conscious about your home network).

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Plugin shows "Disconnected" immediately after Host/Join | Relay isn't running, or the URL/port is wrong. Check the relay's own console/journal output for a bind error. |
| `Test-NetConnection` fails from outside | VPS firewall (`ufw`) or provider security group isn't open, or (home hosting) the router port-forward doesn't match the PC's *current* LAN IP — DHCP can reassign it after a reboot; consider a static DHCP lease for the hosting PC. |
| Works locally, not for others | You're testing with `127.0.0.1`/LAN IP but gave others your *public* IP without actually port-forwarding, or vice versa. |
| `HttpListenerException` on startup (Windows) | Missing the `netsh http add urlacl` grant, or another process already owns that port — check with `netstat -ano \| findstr 7890`. |
| "session full" | The session already has 8 connected peers (relay-enforced cap); have the host start a new session. |
| Connects, but nothing happens after Join | Confirm you're both pointed at the *same* relay URL and the *same* session code (codes are case-normalized, but a copy/paste typo is the usual culprit). |

## Configuring the plugin

Open the Multiplayer window (`/anomech mp`, or the "Multiplayer..." button
that appears once UMAD P3 Black Hole is selected) and type your relay's URL
into the **Relay URL** field — there's no default, so Host/Join stay disabled
until you enter one. It's remembered across sessions once set, so you only
need to type it again if you switch relays.
