# SipBotLib

Headless SIP/PBX library built on [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery). Register, answer, dial, transfer, DTMF, and PCM audio — **no GUI softphone**.

Coding agents should use the **`sipbot`** CLI/daemon (`tools/sipbot`). The JSON Lines contract, env vars, and flows are in **[AGENTS.md](AGENTS.md)**. Play any PCM/μ-law WAV; `sipbot` resamples to the negotiated codec (PCMU 8 kHz default, G.722 16 kHz unless `--pcmu-only`). The `registered` / `status` / `answered` events include an `audio` object so you do not have to scrape the docs.

`tools/LiveCallTest` is a low-level register → auto-answer → echo harness. Prefer `sipbot serve` for automation.

## Quick start

```bash
dotnet build SipBotLib.sln -c Release
```

Linux x64 self-contained binary:

```bash
dotnet publish tools/sipbot/sipbot.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o dist/sipbot-linux-x64
./dist/sipbot-linux-x64/sipbot serve --auto-answer
```

Configure with `SIP_SERVER`, `SIP_USERNAME`, `SIP_PASSWORD` (env wins over `sipsettings.json`). See [AGENTS.md](AGENTS.md).

## Library

`SipClient` (inbound `Accept`/`Answer`, outbound `CallAsync`, `BlindTransferAsync`, DTMF events) plus `BaseAudioEndPoint` for PCM in/out.

```bash
dotnet pack SipBotLib.csproj -c Release
```

Target: **net8.0**. License: MIT.

Related consumer: [SipBotOpen](https://github.com/calebtt/SipBotOpen) (submodule). Public `SipClient` inbound behavior is preserved.
