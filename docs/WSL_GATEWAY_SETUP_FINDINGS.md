# WSL Gateway Setup Findings

Date: 2026-05-22

## Executive summary

The local WSL gateway install/pairing path is now much more reliable in isolated validation: fresh install, reset/recreate, operator pairing, Windows node pairing, post-setup reconnect, and gateway wizard route detection all pass in repeated smoke loops.

The real-profile run now proves the product-driven provider-auth/running path after GitHub Copilot auth: the Windows setup UI shows the GitHub device URL/code, the gateway stores Copilot auth, `missingProvidersInUse` becomes empty, and Copilot-backed agent smoke replies succeed. The remaining gaps are now narrower UX/coverage gaps: the OpenAI browser-login branch did not surface its auth URL, and we still need automated validation that can exercise provider-auth readiness without requiring a human device-code login.

## What now works

The reset loop was made reproducible with:

```powershell
.\scripts\dev-smoke-wsl-setup.ps1 -NoBuild -PostSetupUsableTimeoutSeconds 45
```

The stricter validation loop now proves:

1. A local `OpenClawGateway` WSL distro can be created.
2. The gateway service becomes healthy on `ws://localhost:18789`.
3. The tray operator pairs.
4. The Windows tray node pairs.
5. Post-setup operator reconnect succeeds.
6. Gateway `node.list` sees the Windows node.
7. The V2 setup advances to Gateway Welcome and sends `wizard.start`.
8. Reset/recreate while preserving Windows AppData identity can pass.

Visible real-profile proof now additionally shows:

1. The embedded gateway wizard can drive GitHub Copilot device auth from the product UI.
2. Full reset can clear previous provider/config state.
3. The user can choose **Copilot -> GitHub Copilot** and authorize through `https://github.com/login/device`.
4. The wizard can skip channel setup using the **Finished / Skip for now** option.
5. The outer onboarding flow reaches **All set**.
6. `models status` reports `github-copilot/claude-opus-4.7` with no missing providers.
7. A Copilot-backed local agent smoke returns `OPENCLAW_OK`.

Three additional 2-iteration `ResetRedoPreserveIdentity` runs passed with the stricter wizard-route proof:

| Run | Summary |
| --- | --- |
| 1 | `artifacts\wsl-gateway-validation\stability-3x-ui\run-1b\20260522-130657\summary.json` |
| 2 | `artifacts\wsl-gateway-validation\stability-3x-ui\run-2\20260522-131043\summary.json` |
| 3 | `artifacts\wsl-gateway-validation\stability-3x-ui\run-3\20260522-131422\summary.json` |

## Fixed root causes so far

### 1. Node role upgrade used the wrong approval path

The gateway can report a Windows node role upgrade through `device.pair.requested`, not only `node.pair.requested`.

Evidence:

```text
pairing required: device is asking for a higher role than currently approved
reason=role-upgrade
node.pair.approve failed: unknown requestId
```

Fix:

- `OpenClawGatewayClient.NodePairApproveAsync` and `DevicePairApproveAsync` now wait for gateway acknowledgement instead of returning success when the frame is sent.
- `GatewayConnectionManager` tries `node.pair.approve` first and falls back to `device.pair.approve` when needed.

### 2. Setup raced the app-level node auto-connect

The setup engine owns the node-pairing phase, but the app also auto-connected the local node service after operator connect. That could collide with `PairWindowsTrayNode`.

Fix:

- `App.TryConnectLocalNodeServiceAsync` now honors `_suppressNodeDuringSetup`.

### 3. Operator metadata upgrade invalidated old operator tokens

After Windows node pairing adds the node role to the same device identity, the operator reconnect can trigger:

```text
reason=metadata-upgrade
pairing required: device identity changed and must be re-approved
```

Then the old operator device token can become invalid:

```text
AUTH_DEVICE_TOKEN_MISMATCH
unauthorized: device token mismatch (rotate/reissue device token)
```

Fix:

- Setup `VerifyEndToEnd` refreshes/reissues the operator token.
- Reissue clears only the stale operator token, preserving the keypair and node token.
- Local metadata-upgrade approvals are explicitly approved during setup.

### 4. Reset/recreate preserved stale node tokens

Resetting WSL deletes the gateway's device table, but Windows AppData still had a node device token from the old gateway.

Evidence:

```text
AUTH_DEVICE_TOKEN_MISMATCH
Terminal auth error; stopping reconnect.
```

Fix:

- `ConnectionManagerWindowsNodeConnector` clears only the node token before the setup node-pairing phase.

### 5. Validation was testing old credential assumptions

The validation scripts assumed the old root identity file:

```text
%APPDATA%\OpenClawTray\device-key-ed25519.json
```

Current code uses per-gateway identity:

```text
%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json
```

Fix:

- Validation now reads `gateways.json`, uses the active gateway record, and passes the per-gateway identity path to the CLI probe.
- `OpenClaw.Cli` gained `--identity-path` and `--skip-chat` for validation.

### 6. Reset loop preserved stale LocalAppData state

The reset loop originally preserved isolated LocalAppData as well as AppData. That meant iteration 2 could see an old:

```text
setup-state.json = Complete
```

and incorrectly skip real setup after unregistering WSL.

Fix:

- `ResetRedoPreserveIdentity` preserves AppData identity/registry but clears LocalAppData setup state.

### 7. Replacement setup needed confirmation automation

When AppData is preserved, the Welcome page shows **Install new WSL Gateway** and a confirmation dialog.

Fix:

- Validation now clicks **Continue** if the replacement confirmation appears.

### 8. Channel skip used the wrong sentinel

The manual channel picker includes a bottom option labeled **Finished** / **Skip for now** whose value is `__done__`. The quickstart channel picker uses `__skip__`.

Evidence:

```text
Select a channel
Finished — Skip for now
```

Using the generic wizard **Skip** button on that select step originally sent the wrong value for the manual picker and could be interpreted as a plugin id:

```text
Channel setup
__skip__ plugin not available.
```

Fix:

- Wizard skip handling now chooses the sentinel from the current option list.
- It uses `__skip__` when available, otherwise `__done__` when the wizard exposes that as the **Finished / Skip for now** option.

### 9. Final wizard config commit restarts the gateway

After the channel step, the gateway can restart while committing the final wizard config. The setup page briefly shows:

```text
Authenticating...
Connecting to gateway...
```

and then recovers to:

```text
Gateway configuration complete!
Click Next to continue.
```

Fix:

- If a channel-related final step loses the connection during the gateway restart, the wizard completes after rechecking provider auth instead of restarting from step 1.

## Current caveats / follow-ups

### Earlier blocker: WSL gateway stopped after the embedded gateway wizard started

In an earlier real-profile visible setup, the flow reached the embedded gateway wizard:

```text
[V2Bridge] Advancing V2 from LocalSetupProgress -> GatewayWelcome
[GatewayClient] Sending frame: wizard.start
Wizard response payload kind=Object
```

Shortly after that, the gateway emits a shutdown/service-restart event and WSL stops:

```text
[NODE] Received event: shutdown
Server closed connection: 1012 - service restart
gateway connection failed: Unable to connect to the remote server
```

Then:

```powershell
wsl --list --verbose
```

shows:

```text
OpenClawGateway    Stopped
```

and:

```powershell
Invoke-RestMethod http://127.0.0.1:18789/health
```

failed because the gateway was gone.

Follow-up status:

- The product now verifies the Windows-side `wsl.exe ... sleep` keepalive process after spawning it.
- If detached process creation exits immediately, it falls back to `Process.Start`.
- The wizard page also nudges local-gateway keepalive/reconnect while waiting for the gateway.
- Three visible real-profile reset runs reached a live gateway after setup; final config commit can still trigger a short gateway restart, but the UI recovers to the complete step.

### Why WSL stopping matters

The gateway service is inside the `OpenClawGateway` WSL distro. If the WSL VM stops, systemd and the gateway process stop too. The Windows tray can keep retrying WebSocket reconnects, but nothing is listening on port 18789.

That is why the visible setup can appear stuck on:

```text
Connecting to gateway...
```

even though local setup previously reached `Complete`.

## Keepalive findings

### Manual keepalive works

Manually starting a Windows-held WSL sleep process keeps the distro alive:

```powershell
Start-Process wsl.exe -ArgumentList @(
  '-d','OpenClawGateway',
  '-u','openclaw',
  '--','sleep','2147483647'
)
```

With that process alive:

```text
OpenClawGateway    Running
http://127.0.0.1:18789/health -> ok=true
```

### Product keepalive needed verification/fallback

The product creates a marker like:

```text
%LOCALAPPDATA%\OpenClawTray\wsl-keepalive\OpenClawGateway.json
```

but the marker can point at a PID that is no longer alive, or the keepalive can be stopped during replacement/uninstall. The evidence showed the product keepalive was not reliably surviving the replacement/setup handoff.

Fix added:

- After detached `CreateProcess`, verify the `wsl.exe ... sleep` process is still alive before trusting the PID/marker.
- If the detached child exits immediately, fall back to `Process.Start`.
- Add a WSL-side `openclaw-wsl-keepalive.service` as a secondary helper, but keep relying on the Windows-held `wsl.exe ... sleep` process as the primary VM keepalive.

### WSL-side systemd sleep service is not sufficient by itself

Adding an in-distro service such as:

```bash
systemd-run --user --unit=openclaw-wsl-keepalive \
  --description='OpenClaw WSL keepalive' \
  --property=Restart=always \
  --property=RestartSec=30 \
  /usr/bin/sleep 2147483647
```

works while WSL is already running, but it does not necessarily prevent the WSL VM itself from being torn down by Windows. The Windows-side `wsl.exe ... sleep` process is the more important keepalive on this machine.

### CRLF bug found in the in-distro keepalive script

The first C# implementation passed a raw multiline string to `bash -lc`. On Windows this preserved CRLF line endings, and Bash saw:

```text
bash: line 1: set: -\r: invalid option
```

Fix:

- Build the shell script with explicit LF (`\n`) joining instead of a raw multiline literal.

## Provider-auth finding

Even when local setup and gateway UI handoff succeed, the machine may still not be "running" from a user perspective because model auth is missing.

Evidence from the real gateway:

```text
openclaw models auth list
Profiles: (none)
```

```text
openclaw models status --json
defaultModel: openai/gpt-5.5
missingProvidersInUse: ["openai"]
```

Visible Chat then reports:

```text
No API key found for provider "openai"
```

Conclusion:

- "Setup complete" must not mean "usable chat" unless provider auth is configured or the UI clearly blocks and routes the user to auth setup.

Follow-up real-profile proof:

- The product UI now drives GitHub Copilot auth directly. It renders:

  ```text
  Authorize GitHub Copilot
  URL: https://github.com/login/device
  Code: <device code>
  ```

- After browser authorization, the gateway reports:

  ```text
  defaultModel: github-copilot/claude-opus-4.7
  missingProvidersInUse: []
  providersWithOAuth: github-copilot (1)
  ```

- A Copilot-backed local agent smoke succeeds:

  ```text
  OPENCLAW_OK
  provider: github-copilot
  model: claude-opus-4.7
  ```

## Current hypothesis

The local WSL gateway setup had been failing the "fresh to running" bar because of two issues:

1. **WSL keepalive/restart durability**
   - Manual Windows-side keepalive works.
   - Product keepalive needed immediate-exit detection and fallback.

2. **Provider auth completion**
   - The gateway wizard can route through provider auth, but users must complete provider selection/auth.
   - Chat is only usable after a configured model provider is available.

## Recommended next fixes

### A. Make keepalive self-healing and verified

After every local gateway lifecycle event, verify:

```powershell
wsl --list --verbose
```

shows:

```text
OpenClawGateway    Running
```

and verify an actual live Windows process exists:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" |
  Where-Object { $_.CommandLine -match 'OpenClawGateway|2147483647|sleep' }
```

If not, re-arm keepalive and update the marker.

Important lifecycle points:

- after setup starts gateway,
- after replacement/uninstall completes,
- after setup reaches Complete,
- before advancing to GatewayWelcome,
- after gateway restart/shutdown events,
- on tray startup when active gateway is local.

### B. Avoid stale keepalive markers

The marker should not be trusted unless the PID is live and command line still matches:

```text
wsl.exe -d OpenClawGateway -u openclaw -- sleep 2147483647
```

If marker validation fails, delete marker and spawn a new keepalive.

### C. Add provider-auth gate to onboarding

Before allowing onboarding to finish into Chat:

1. call `models.authStatus` or equivalent status RPC,
2. check `missingProvidersInUse`,
3. if missing providers exist, render an auth-required step instead of finishing.

Short-term text can be explicit:

```text
Model provider authentication is required before OpenClaw can chat.

Missing provider(s): openai

From WSL, run:
openclaw models auth add

Then return here and retry.
```

Longer term, the Windows wizard should drive the provider auth flow directly.

Current status:

- A provider-auth gate was added so onboarding checks provider readiness before silently completing into broken Chat.
- GitHub Copilot auth was proven through the Windows product UI across three visible full-reset runs.
- The OpenAI browser-login branch still needs a UX fix: it reached the "Paste authorization code or redirect URL" step without surfacing the actual auth URL.

### D. Extend validation to prove "fresh to running"

Current validation proves backend setup and the gateway wizard route. It should also prove one of:

- provider auth is configured, or
- the UI is visibly blocked on "provider auth required" and does not proceed to Chat.

The test should fail if Chat opens and immediately reports:

```text
No API key found for provider "openai"
```

## Shareable conclusion

We fixed the pairing and reset/recreate reliability issues and proved the product-driven GitHub Copilot path end to end. The remaining work is no longer basic gateway pairing. It is polishing and automating the transition from "local gateway setup complete" to "fully running user experience":

- WSL keepalive was hardened, but should be covered by a non-destructive validation mode for already-authenticated gateways.
- Provider auth is now gated, and Copilot auth/agent smoke was proven through the product UI.
- The OpenAI auth path needs to show/launch the auth URL instead of asking for a redirect/code without context.

The next PR should either include or explicitly track:

1. durable local WSL keepalive across setup/restart/provider wizard,
2. provider-auth gate before onboarding completion,
3. validation that proves fresh install -> gateway wizard -> provider auth required or authenticated -> running,
4. OpenAI browser-login URL surfacing.
