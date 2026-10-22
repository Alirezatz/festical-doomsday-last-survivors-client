# Festical Doomsday Last Survivors Client — Unofficial Community Client

A multi-account manager for **Doomsday: Last Survivors** (also known as *Festical Doomsday: Last Survivors*).

One window holds every account you own: connect them all at once, run alliance actions across them, watch a single shared log, and keep each session on its own server route. The game protocol, the raw TCP client, the edge-server routing and the capture decoder are all written from scratch in C# — there is no third-party game library involved.

> **This is an unofficial community client.** It is **not** written, endorsed, published, or supported by IGG (the game's publisher) or by any of its studios. It is an independent third-party tool built by the community. All game names, logos, and assets remain the property of their respective owners. Use it responsibly, and read the [Terms of use](#terms-of-use) before you start.

---

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
  - [1. Install Wireshark (with tshark)](#1-install-wireshark-with-tshark)
  - [2. Get the client](#2-get-the-client)
- [Building an account from a network capture](#building-an-account-from-a-network-capture)
  - [Step 1 — Open Wireshark and pick your interface](#step-1--open-wireshark-and-pick-your-interface)
  - [Step 2 — Start recording](#step-2--start-recording)
  - [Step 3 — Log into the game](#step-3--log-into-the-game)
  - [Step 4 — Stop, save, and hand the file to the client](#step-4--stop-save-and-hand-the-file-to-the-client)
- [Important: use throwaway accounts only](#important-use-throwaway-accounts-only)
- [Important: 30-day validity and single-session logins](#important-30-day-validity-and-single-session-logins)
- [Daily use](#daily-use)
- [Telegram group](#telegram-group)
- [Donate](#donate)
- [Build from source](#build-from-source)
- [How it works](#how-it-works)
- [Account file format](#account-file-format)
- [Security and privacy](#security-and-privacy)
- [Troubleshooting](#troubleshooting)
- [Disclaimer](#disclaimer)
- [Terms of use](#terms-of-use)

---

## Features

- **Multi-account dashboard** — every account in one list, each with its own state, its own connect/disconnect button and its own log lines.
- **Build accounts from a capture** — the client decodes a Wireshark `.pcap`/`.pcapng` file and writes the account JSON for you. No manual editing.
- **One shared backend** — a single service and a single worker thread per account, instead of one process per account. Memory stays flat no matter how many accounts you run.
- **One shared console** — all account output lands in a single log, prefixed with the account name.
- **Alliance actions** — search an alliance by TAG (or by exact `[TAG]Name`) and send join requests to every connected account; send leave requests the same way. Both ask for confirmation first.
- **Alliance Auto Help** — when all accounts are connected, the client watches each account's alliance assistance requests and sends the game's normal help request on its own.
- **Custom server** — point new connections at another host without editing the account files, and go back to the normal route with one click.
- **Edge-server routing** — resolves the fresh GameServer address from the live LoginServer reply and the `vn_server_edge` table, with public DoH/UDP fallback and TCP probing, so a stale IP in an old file never breaks a connection.
- **Two languages** — the entire window in English and Persian, including right-to-left mirroring.
- **Cross-platform** — Windows, Linux and macOS, from the same source.

---

## Requirements

| Requirement | Details |
| --- | --- |
| **Wireshark** (with **tshark**) | **Required.** The client shells out to `tshark` to read your capture files. Wireshark itself is only needed for *recording* the capture. |
| **.NET 10** | Only if you build from source. The published builds are self-contained — the runtime travels inside the executable, so end users install nothing. |
| **Windows, Linux or macOS** | The published builds cover Windows x64, Linux x64, macOS arm64 and macOS x64. |

Wireshark is the **only** third-party program you have to install. There is no other external dependency: DNS resolution, DoH lookups and the TCP client are all built into the executable.

> ### ⚠️ While you are installing Wireshark, make sure to tick **tshark**
>
> The Wireshark installer shows a list of optional components. **You must leave the "TShark" component selected** (it is selected by default) — the client cannot read a capture without `tshark.exe`.
>
> On Windows the component list looks like this:
>
> ```
> Select Components
>  ☑ Npcap                                        ← required for capturing
>  ☑ Npcap Loopback Adapter
>  ☑ TShark                                      ← REQUIRED, keep this ticked
>  ☐ WinPcap                                     ← legacy, do not install
> ```
>
> If you deselect TShark, the client will fail with `tshark not found`. You can also fix it later by re-running the Wireshark installer and choosing *Modify* → *TShark*.

---

## Installation

### 1. Install Wireshark (with tshark)

1. Download Wireshark from <https://www.wireshark.org/download.html>.
2. Run the installer and accept the default install location
   (`C:\Program Files\Wireshark` on Windows).
3. On the **Select Components** page, **keep `TShark` ticked**. Also keep **Npcap** ticked, because Npcap is what lets Wireshark capture your network traffic.
4. Finish the install.

The client looks for `tshark` in this order:

```
C:\Program Files\Wireshark\tshark.exe
C:\Program Files (x86)\Wireshark\tshark.exe
/usr/bin/tshark
/usr/local/bin/tshark
/opt/homebrew/bin/tshark
tshark                        (anything on your PATH)
```

### 2. Get the client

Grab the archive for your platform from the [Releases](https://github.com/Alirezatz/festical-doomsday-last-survivors-client/releases) page, unpack it, and run the executable:

| Platform | File |
| --- | --- |
| Windows x64 | `FesticalDoomsdayClient-windows-x64.zip` → `FesticalDoomsdayClient.exe` |
| Linux x64 | `FesticalDoomsdayClient-linux-x64.tar.gz` → `FesticalDoomsdayClient` |
| macOS arm64 | `FesticalDoomsdayClient-macos-arm64.tar.gz` → `FesticalDoomsdayClient` |
| macOS x64 | `FesticalDoomsdayClient-macos-x64.tar.gz` → `FesticalDoomsdayClient` |

The builds are **self-contained single-file** executables: no .NET runtime, no extra DLLs, nothing to install next to them. The only thing the client reads from disk is the `edge_servers.json` route table, which is embedded in the executable — and a newer copy of that file placed in a `Backend` folder next to the executable will override the embedded one.

---

## Building an account from a network capture

The client never asks you for your password. It reads the game's own login handshake out of a network capture and rebuilds the account file from it. Here is the whole procedure.

### Step 1 — Open Wireshark and pick your interface

1. Start **Wireshark**.
2. In the capture list at the top, choose the interface that carries your game traffic — usually your **Wi-Fi** or **Ethernet** adapter. If the game runs through a VPN or a proxy, pick that adapter instead, because the traffic you need is the one leaving through it.
3. Double-click the interface to start capturing.

> **Tip:** if you are not sure which interface is the right one, hover over the interface names and check the traffic counter. The one with a moving byte counter is the one the game is using. You can also start capturing on all of them and narrow it down later.

### Step 2 — Start recording

Click the **red square** (or press `Ctrl+E`) to start recording. The interface row turns red. Keep it running.

### Step 3 — Log into the game

1. Launch **Doomsday: Last Survivors** and log in with the account you want to use.
2. Wait until the **main map of the game** is fully loaded and visible.
3. Exit the game.

> **Do not stop the capture too early.** The client needs the complete login exchange: the LoginServer request *and* its reply, plus the GameServer login. If the recording ends before the map is on screen, the handshake is incomplete and the extraction fails.

### Step 4 — Stop, save, and hand the file to the client

1. In Wireshark, click the **red square** again (or press `Ctrl+E`) to stop the recording.
2. Use **File → Save As…** and save the capture as a `.pcapng` file (or `.pcap`). Remember where you saved it.
3. In the client, click **Build from capture** (or press `Ctrl+B`).
4. Select the file you just saved.
5. The client decodes it and writes a `.json` file **next to the capture**, with the same base name. It then adds that JSON to the account list automatically.

You can also add a JSON you already have with **Add JSON files** (`Ctrl+O`).

---

## Important: use throwaway accounts only

> ### ⚠️ Read this before you record anything
>
> **Never use your main, real, or valuable account for this.** Use a **freshly created account that exists only for botting and automation**.
>
> Why this matters:
>
> - The account JSON contains **live login credentials** — the exact session token the game issued. Whoever holds that file holds the session.
> - A bot client logs in and out, reconnects and spams alliance requests. The game's own anti-cheat watches exactly that pattern. An automated account can be flagged, **banned, or have its resources stripped**.
> - If the same account is used from several places, the sessions fight each other and the game invalidates the older one.
> - A ban on the throwaway account costs you nothing. A ban on your main account costs you everything.
>
> **Rule of thumb:** if you would be upset to lose the account, do not put it in this client. Create a dedicated account, use it only here, and keep it away from your everyday play.

---

## Important: 30-day validity and single-session logins

Two facts about the game's login system shape how you use this client. Both were confirmed against the game's own server behaviour.

### 1. Every login file is valid for 30 days

> Every account JSON (every login) stays valid for **30 days**, according to the information published by the game and confirmed against the server behaviour.

Keep the JSON file, and keep using it for the whole 30-day window. You do **not** need to re-record it every session.

### 2. A new login on another device kills the previous session

> Every login issues a **user ID**. If you log in again with the same user ID **from a different device**, the **previous session is invalidated**.

That means:

- **Do not log into the same account with the real game on another device** (or on this device) after you have built its JSON. That login kills the session stored in your JSON, and the client stops working for that account.
- **After every login, do not enter that account through the normal game client again.** Stay with the JSON file, and use it for as long as its 30 days have not run out.
- If the game forces you to log in again, or you switch devices, your JSON becomes stale: **record a new capture and build a new JSON**.

**Put together:**

| Situation | What to do |
| --- | --- |
| You just built a JSON | Use **that file** for the next 30 days. Do not log in manually. |
| 29 days have passed | Nothing. Keep using the same file. |
| 30 days are up | Record a new capture and build a new JSON. |
| The account was logged in on another device | The old JSON is dead. Record a new capture. |
| You want to play manually again | Remember: that login invalidates the JSON. You will have to re-record. |

---

## Daily use

1. **Add your accounts.** Either *Build from capture* (new capture) or *Add JSON files* (existing JSON).
2. **Connect.** *Connect All* connects the accounts in the list one after another. *Disconnect All* closes them again, one at a time, waiting a random 1–5 seconds between each so the server does not see a burst of traffic. Each account card also has its own **Connect** / **Disconnect** / **Remove** buttons.
3. **Alliance.** Enter an alliance TAG (for example `_CC`) or an exact `[TAG]Name`, then press **Join** to search the game server, resolve the alliance and send a join request to every connected account — again with a random 1–5 second delay between accounts. **Leave** does the same in reverse. Both actions ask for confirmation. If a TAG matches several alliances, use `[TAG]Name` to pick the exact one.
4. **Alliance Auto Help** runs on its own: once **all** accounts are connected, the client watches each account's assistance requests and sends the game's normal help request when one is due.
5. **Custom server.** To test a different server without editing any file, type a hostname or IP and switch on **Use Custom Server**. New connections use that host and keep the GameServer port from each account's own JSON. Disconnect and reconnect the accounts to switch them. Switch the option off to return to the normal route.
6. **Watch the console.** All account logs are in the single console at the bottom of the window, prefixed with the account name.

**Keyboard shortcuts**

| Key | Action |
| --- | --- |
| `Ctrl+O` | Add account JSON files |
| `Ctrl+B` | Build an account from a capture |
| `Ctrl+L` | Clear the console |

**The account list** is remembered between runs in
`%LOCALAPPDATA%\DlsResourceFarm\accounts.json`
(`~/.local/share/DlsResourceFarm/accounts.json` on Linux, `~/Library/Application Support/DlsResourceFarm/accounts.json` on macOS). The list only holds **paths** to your JSON files — the credentials stay in the JSON files themselves, on your disk.

---

## Build from source

You need the **.NET 10 SDK**.

```bash
git clone https://github.com/Alirezatz/festical-doomsday-last-survivors-client.git
cd festical-doomsday-last-survivors-client
```

**Run it:**

```bash
dotnet run --project ./src/FesticalDoomsday.Client/FesticalDoomsday.Client.csproj
```

**Publish a self-contained single file for one platform:**

```bash
dotnet publish src/FesticalDoomsday.Client/FesticalDoomsday.Client.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  --output artifacts/windows-x64
```

**Publish every platform at once:**

```powershell
./build/publish-all.ps1
```

| Path | Purpose |
| --- | --- |
| `src/FesticalDoomsday.Client/Backend/` | the game protocol, the raw TCP client, the edge-server routing and the capture decoder |
| `src/FesticalDoomsday.Client/Views/` | the Avalonia window and the confirmation dialog |
| `src/FesticalDoomsday.Client/Services/` | the localizer (English/Persian), the account store, the backend bridge |
| `src/FesticalDoomsday.Client/Assets/` | the application icon and the embedded edge-server table |
| `build/publish-all.ps1` | publishes all four platform targets |
| `.github/workflows/release.yml` | builds the tagged releases automatically |

**Stack:** C# · .NET 10 · Avalonia 11.3 (Skia) · `System.Text.Json`.

---

## How it works

| File | Purpose |
| --- | --- |
| `Backend/Protocol.cs` | the wire format (`[len][opcode][protobuf]`) and every message builder and parser |
| `Backend/Opcodes.cs` | the game's own `MsgType` name table (5 588 entries) |
| `Backend/GameClient.cs` | the blocking raw-TCP client |
| `Backend/EdgeRoute.cs` | the edge-server table, DNS/DoH/UDP resolution and TCP probing |
| `Backend/LiveCapture.cs` | the `tshark` helpers and the TCP stream reassembly |
| `Backend/CaptureExtractor.cs` | builds the account JSON out of a capture |
| `Backend/BackendService.cs` | the shared backend that serves every account |

**How a capture becomes an account file**

1. The client runs `tshark -r <capture> -T fields -e tcp.stream -e ip.src -e tcp.srcport -e ip.dst -e tcp.dstport -e tcp.seq_raw -e tcp.payload -E separator=|`.
2. It reassembles every TCP stream from the raw sequence numbers.
3. It walks the payloads looking for the game's own messages: the LoginServer request (`0x279D`), the LoginServer reply, and the GameServer login.
4. From those it recovers the GameServer address, the session token, the account and player IDs, the device ID and the client version — and writes them into a JSON file next to the capture.

**How a connection is made**

1. The client sends a fresh LoginServer request, using the credentials from the account JSON, and gets the kingdom and the private GameServer port back.
2. It looks up the matching edge server in the `vn_server_edge` table, resolves the edge hostname with the current network's DNS, and TCP-probes the result.
3. If the system DNS answer is unreachable, the client falls back to public DoH/UDP resolvers (Cloudflare, Google) and to the known edge addresses before it gives up.
4. If the captured LoginServer itself cannot be reached, the other login routes from the game's own `server_config` are tried. An authentication error from a reachable server is never retried.

---

## Account file format

The client writes this file next to your capture, with the same base name. **It contains live credentials — treat it like a password file.**

```jsonc
{
  "game_server": "34.187.190.254:10294",   // GameServer address from the capture
  "f3": "668e690634183fae43814c0617c7756b", // the live session token
  "account": 1607883238,                    // the account ID
  "player_id": 1607883238,                  // the player (user) ID
  "device_id": "…",                         // randomised per account by the client
  "version": "1.61.5",                      // the client version that logged in
  "ls_reply": { … },                        // the LoginServer reply, when present
  "ls_request": {
    "account": 1607883238,
    "web_session": "…",
    "udid": "…",
    "login_server": "192.243.110.109:11032"
  }
}
```

When you add an account, the client replaces the root `device_id` with a unique random 40-character hexadecimal value. It changes **only** the root `device_id` — `ls_request.udid` and the other login fields are left untouched.

---

## Security and privacy

- **Nothing is uploaded.** The client has no telemetry, no analytics, no account, and no update server. Your account files and your captures never leave your machine.
- **Credentials stay on your disk.** The account list stores only file paths. The JSON files hold the real session material.
- **Tokens are never printed.** The console shows progress and results, never the token values.
- **`.gitignore` blocks credential files.** All `*.json` files are ignored by default, with an exception only for the embedded `edge_servers.json`. Never commit your own account JSON.
- **Use throwaway accounts** and keep the JSON files private.

---

## Troubleshooting

**`tshark not found - install Wireshark`**
Wireshark is installed but `tshark` is not visible. Re-run the Wireshark installer, choose **Modify**, and tick **TShark**. On Linux, install `tshark` through your package manager. You can also add the Wireshark folder to your `PATH`.

**`Capture extraction error` / the client cannot find a login in the capture**
The capture is missing part of the handshake. Re-record it and make sure you: captured the right interface, kept the recording on through the full login, waited for the **main map** to appear, and stopped *after* that.

**The account connects and immediately disconnects**
The session in the JSON has most likely expired, or it was invalidated by a login from another device. Record a new capture and build a new JSON. Check the console for the exact reason.

**The account is banned or the game asks for a verification code**
You are most likely using an account that was logged in elsewhere, or an account the anti-cheat already flagged. Create a fresh throwaway account and never log into the same account from two places.

**A connection times out**
The edge server for your current network may be unreachable. The client already tries public DoH/UDP resolvers and the known edge addresses. Try a different network, or set a custom server host.

---

## Disclaimer

This project is an **independent, unofficial, community-made client**. It is not affiliated with, endorsed by, sponsored by, or connected to IGG or any of its studios or partners. It is not an official product and it is not supported by the game's developers.

This client talks to the game's own servers using the protocol the game already uses on your own device. Using it is at your own risk, and you are responsible for complying with the game's terms of service and with the laws that apply to you.

## Terms of use

- This software is provided **as is**, without warranty of any kind.
- The authors are not liable for any account ban, loss of in-game items, data loss, or any other damage arising from your use of this tool.
- You are responsible for the accounts you use it with. **Use throwaway accounts.**
- The account JSON files contain live credentials. Keep them private, never share them, and never commit them to a repository.
- Do not use this client for anything that violates the game's terms of service, any third party's rights, or any applicable law.
- The game and its assets belong to their respective owners. This project claims no ownership over them.
- Contributions, bug reports and pull requests are welcome.

---


## Telegram group

Questions, setup help, release news and updates:

### 👉 https://t.me/DoomsdayFTGroup

The same link is built into the client — the **Community** section in the footer of the window opens it in your browser.

---

## Donate

This client is free, open source and maintained in the community's free time. If it saves you time, you can support the project:

| Network | Address |
| --- | --- |
| **USDT (TRC20)** | `TYVUbUzjy6wkmqArA9fsFwz4qhRbJNErws` |
| **BTC** | `bc1q4q93d288fsh8ukp5v6ekrcytz3jwlmcvvv3f7n` |
| **Ethereum (ERC20)** | `0x4860Caf80CD15e17624A63eE169332EA7306FEeb` |

---

**Made by the community, for the community.** 💙
