# SipBotLib for coding agents

SipBotLib is a **headless** SIP/PBX library (SIPSorcery). It is **not** a GUI softphone. Use `sipbot` to register, answer, dial, transfer, play/record WAV, and collect DTMF from a Linux agent box.

Preferred interface: **`sipbot serve`** (JSON Lines on stdout, human logs on stderr, JSON commands on stdin). `tools/LiveCallTest` remains a low-level echo harness.

## Build and run

```bash
dotnet build SipBotLib.sln -c Release
dotnet publish tools/sipbot/sipbot.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o dist/sipbot-linux-x64
```

The published binary is `dist/sipbot-linux-x64/sipbot`.

```bash
export SIP_SERVER=pbx.example.com
export SIP_PORT=5060
export SIP_USERNAME=101
export SIP_PASSWORD='...'
export SIP_FROMNAME=101
# optional:
# export SIP_SETTINGS_PATH=/path/to/sipsettings.json
# export SIP_LOCAL_PORT=0          # 0 = ephemeral UDP bind

./dist/sipbot-linux-x64/sipbot serve --auto-answer
```

From source (framework-dependent):

```bash
dotnet run --project tools/sipbot -- serve --auto-answer
```

## Config precedence

**Environment variables win over file values** when both are present.

1. File path: `--settings`, else `SIP_SETTINGS_PATH`, else `sipsettings.json` in cwd / app directory if it exists.
2. Overlay: `SIP_SERVER`, `SIP_PORT`, `SIP_USERNAME`, `SIP_PASSWORD`, `SIP_FROMNAME`.
3. `SIP_LOCAL_PORT` is the **local UDP bind** (not the PBX port). Default `0` (ephemeral). Do not bind `5060` when co-located with Asterisk.

Env-only is enough if `SIP_SERVER` and `SIP_USERNAME` are set. Never commit real passwords. Example file: `Config/examplesipsettings.json`.

```json
{
  "SipSettings": {
    "configs": [
      {
        "server": "pbx.example.com",
        "port": 5060,
        "username": "101",
        "password": "CHANGE_ME",
        "fromname": "SipBot"
      }
    ]
  }
}
```

## Protocol (`sipbot serve`)

- **Stdout:** JSON Lines events only (machine-readable).
- **Stderr:** Serilog logs.
- **Stdin:** one JSON command object per line.
- Shutdown: SIGINT / SIGTERM / Ctrl+C / `{"cmd":"quit"}`. No `Console.ReadKey`.

### Events

```json
{"event":"registered","user":"101","server":"pbx.example.com","audio":{"codecs":["PCMU","G722"],"playHint":"any PCM/μ-law WAV; source sample rate does not matter (resampled to the negotiated codec: PCMU 8 kHz default, G.722 16 kHz unless --pcmu-only)","pcmuOnly":false}}
{"event":"invite","from":"…","callId":"…"}
{"event":"answered","audio":{"codec":"G722","clockRateHz":16000,"rtpClockRateHz":8000}}
{"event":"dtmf","digit":"5"}
{"event":"ended","durationSec":42}
{"event":"error","message":"…"}
{"event":"status","registered":true,"callActive":false,"audio":{"codecs":["PCMU","G722"],"playHint":"…","pcmuOnly":false}}
{"event":"dtmf_result","digits":"1234","timedOut":false}
{"event":"recorded","file":"out.wav","seconds":30}
```

**Audio:** `{"cmd":"play","file":"prompt.wav"}` accepts any readable PCM or μ-law WAV. Source sample rate does not matter. Outbound is resampled to the **negotiated** codec after SDP (`answered.audio`): PCMU 8 kHz by default, or G.722 16 kHz if the far end agrees (`--pcmu-only` disables G.722). Do not invent a play sample-rate flag. On a bad file, `error.message` says it is not a readable WAV and that rate is resampled.

### Commands

```json
{"cmd":"answer","play":"welcome.wav"}
{"cmd":"hangup"}
{"cmd":"transfer","target":"102"}
{"cmd":"play","file":"prompt.wav"}
{"cmd":"record","file":"out.wav","seconds":30}
{"cmd":"wait_dtmf","timeoutSec":30,"maxDigits":4}
{"cmd":"dial","uri":"sip:102@pbx.example.com","play":"hello.wav"}
{"cmd":"status"}
{"cmd":"quit"}
```

Transfer / dial targets: bare extension (`102`), `user@host`, full `sip:` URI, `tel:` URI, or a PSTN number (`+1…`, punctuation OK). Bare extensions become `sip:{ext}@{SIP_SERVER}`. Phone numbers are reduced to digits (leading `+` stripped) so typical PBX outbound routes match. Dial failure JSONL is `dial failed: <uri> (<SIP status>)` when the far end sent one (e.g. `603 Decline`).

Flags: `--auto-answer`, `--play FILE` (with auto-answer), `--settings PATH`, `--config N`, `--pcmu-only`, `--no-keepalive`. `sipbot serve --help` states the play/resample rule.

## Minimal inbound flow

1. Start `sipbot serve` (optionally `--auto-answer`).
2. Wait for `{"event":"registered",...}`.
3. Inbound: `{"event":"invite",...}` then `{"cmd":"answer"}` (or auto-answer).
4. Expect `{"event":"answered"}`.
5. `{"cmd":"hangup"}` → `{"event":"ended","durationSec":N}`.

Outbound: after registered, `{"cmd":"dial","uri":"102"}` uses `SipClient.CallAsync`.

## Pack the library

```bash
dotnet pack SipBotLib.csproj -c Release -o dist
```

Does not publish to nuget.org from this repo by default.

## Known limits

- Outbound RTP is **PCMU (8 kHz)** unless G.722 is negotiated (`answered.audio.clockRateHz` is 16000 then). `--pcmu-only` disables G.722. Play WAV is always resampled; do not match the file rate yourself.
- No acoustic echo cancellation (AEC).
- NAudio is referenced for **managed** WAV/PCM/μ-law (WDL resampler, `WaveFileReader`). MediaFoundation is not used; Linux agents do not need Windows codecs.
- Single call at a time per `sipbot` process.
- Registration must stay up: use `serve`, not a one-shot process that exits after dial.
- Some PBXs deliver the first inbound INVITE only after OPTIONS qualify; a `registered` event does not always mean the AOR is already reachable.
- PSTN/cellular DTMF is often in-band; `wait_dtmf` needs RFC 4733 telephone-event.

## Manual test plan (VitalPBX / local Asterisk)

Use two extensions (e.g. 101 = sipbot, 102 = a phone or a second client).

1. **Register:** start `sipbot serve` with env or settings; confirm `registered` on stdout and the PBX shows the peer reachable (OPTIONS qualify).
2. **Inbound answer:** call 101 from 102; `invite` then `answer` (or `--auto-answer`); far end hears keep-alive/silence or a WAV from `play`.
3. **DTMF:** from the phone, press digits; expect `dtmf` events. `wait_dtmf` should emit `dtmf_result`.
4. **Transfer:** while in call, `{"cmd":"transfer","target":"103"}` (or another reachable ext).
5. **Outbound dial:** `{"cmd":"dial","uri":"102"}`; 102 rings and answers; expect `answered` then `hangup`.
6. **Shutdown:** Ctrl+C / SIGTERM; process exits without hanging; no `ReadKey`.

Do not commit `sipsettings.json` with real credentials (`**/sipsettings.json` is gitignored).
