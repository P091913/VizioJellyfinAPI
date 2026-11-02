# TvRemote

A small API that plays things from Jellyfin on the Vizio TV. Send it text like
"play scooby doo"; it wakes the TV if needed, opens Jellyfin, finds the title,
and starts it (resuming where you left off; shows go to the next unwatched episode).

## Run it (Docker)

1. Copy `.env.example` to `.env` and fill it in (see [Settings](#settings)).
   `.env` holds secrets: git ignores it and it is never copied into the image.
2. `docker compose up -d --build`

The API is then on port 5055 of the PC running Docker.

| To | Run |
|---|---|
| Start, or apply a code / `.env` change | `docker compose up -d --build` |
| Stop (stays stopped after a reboot) | `docker compose stop` |
| Start again | `docker compose start` |
| Watch the log | `docker compose logs -f` |
| Remove the container | `docker compose down` |

The TV's Power Mode must be **Quick Start**. The API turns the TV on through the TV's own API,
which only answers in standby when Quick Start is on.

## Use

```powershell
$api = "http://<docker-pc-ip>:5055"
Invoke-RestMethod "$api/tv/command" -Method Post -ContentType "application/json" -Body '{"text":"play scooby doo"}'
Invoke-RestMethod "$api/tv/command" -Method Post -ContentType "application/json" -Body '{"text":"play dune","dryRun":true}'
Invoke-RestMethod "$api/tv/status"
```

| Endpoint | Body | Does |
|---|---|---|
| `POST /tv/command` | `{"text": "...", "dryRun": false}` | Free text (see phrases below) |
| `POST /tv/play` | `{"title": "...", "dryRun": false}` | Play a title directly |
| `POST /tv/launch` | | Wake TV + open Jellyfin |
| `POST /tv/pause` `/tv/resume` `/tv/stop` | | Playback control |
| `POST /tv/off` | | Turn the TV off |
| `GET /tv/status` | | Power, what's showing, now playing |
| `GET /health` | | Liveness |

Every response has `status` and a speakable `message`:
`playing`, `ok`, `would_play`, `ambiguous` (message asks "Which one: A or B?", `data.candidates` lists them),
`not_found`, `not_playing`, `unknown_command`, `error`.

### Phrases understood

- play / watch / put on / start *title* — optional "on the TV", "the movie", "the next episode of"
- add a year to pick a version: "play dune 2021", "play dune from 1984"
- pause · resume / unpause / continue / play / resume playing / keep watching · stop
- turn on the TV / open jellyfin · turn off the TV

### iPhone Shortcut

Dictate Text → Get Contents of URL (`http://<docker-pc-ip>:5055/tv/command`, POST, JSON body: `text` = Dictated Text)
→ Get Dictionary Value `message` → Speak Text.

## Settings

All settings are environment variables, set in `.env`. A double underscore is one level in the
settings: `Tv__Ip` is the setting `Tv:Ip`. The app refuses to start if a required one is missing.

| Variable | Required | Notes |
|---|---|---|
| `Tv__Ip` | yes | The TV's LAN address |
| `Tv__AuthToken` | yes | Token from pairing with the TV's SmartCast API. A TV reset needs a new pairing. |
| `Tv__AppUrl` | yes | The page the TV opens (tvshim), e.g. `http://<docker-pc-ip>:8097/web/index.html` |
| `Jellyfin__ApiKey` | yes | Jellyfin Dashboard → API Keys |
| `Jellyfin__BaseUrl` | no | Default `http://localhost:8096`. In Docker: `http://host.docker.internal:8096` |
| `Api__Key` | no | If set, callers must send header `X-Api-Key` (or `?key=`). `/health` stays open. |
| `Tv__SessionMatch` | no | Default `VIZIO`. How the TV's Jellyfin session is recognized (decoded DeviceId) |
| `Tv__JellyfinDeviceId` | no | Pin an exact DeviceId instead |
| `Tv__BootSettleSeconds` | no | Default 8. Wait after power-on before launching |
| `Tv__PowerOnTimeoutSeconds` | no | Default 45. Give up waking after this |

To run it from Rider instead of Docker, put the same variables in the run configuration's
environment variables.
