# Onboarding Wizard

The onboarding wizard is now the V2 setup flow for installing a new app-owned local WSL gateway on Windows.

## Overview

On first launch, the wizard appears only when there is no usable saved gateway connection. Users with any existing local or external gateway manage connections from the tray app's Connections tab and can start setup intentionally with **Install new WSL Gateway**.

The V2 setup flow walks users through:

1. **Welcome** — Greeting and introduction
2. **Local setup progress** — App-owned `OpenClawGateway` WSL installation
3. **Gateway setup** — Gateway-driven provider/model configuration hosted by `GatewayWizardPage`
4. **Permissions** — Windows system permission review
5. **All set** — Feature summary and completion

The setup flow no longer configures remote/manual gateways. The Welcome page's **Advanced setup** link closes setup and opens the tray app's Connections tab.

## Screen Details

### Welcome
Displays the OpenClaw lobster icon, app title, and a brief description. If an app-owned local WSL gateway already exists, the primary CTA reads **Install new WSL Gateway** and confirmation warns that the current OpenClaw WSL gateway and distro will be deleted. If only an external gateway exists, the CTA remains **Set up locally** and confirmation explains that the external connection remains available in Connections.

### Local setup progress
Installs and connects a new app-owned `OpenClawGateway` WSL instance. When replacing an app-owned local gateway, the removal step is shown as part of progress and can be retried on failure.

### Wizard
Renders server-defined setup steps via RPC (`wizard.start` / `wizard.next`). The gateway controls the flow — steps can be:
- **Note** — informational messages
- **Confirm** — yes/no decisions
- **Text** — free-form input (with PasswordBox for sensitive fields like API keys)
- **Select** — radio button choices (e.g., AI provider selection)
- **Progress** — loading indicator for background operations

If the gateway doesn't support the wizard protocol or is unreachable, this screen shows an "offline" message and can be skipped.

### Permissions
Checks 5 Windows permissions using native APIs and registry:
- Notifications (Toast capability)
- Camera (Windows.Devices.Enumeration)
- Microphone (Windows.Devices.Enumeration)
- Screen Capture (Graphics.Capture)
- Location (optional, registry-based)

Each permission shows its current status (Enabled/Disabled/Allowed/Denied) with an "Open Settings" button linking to the relevant `ms-settings:` URI.

### All set
Displays a completion summary, a Launch at startup toggle, and a Finish button that saves settings and closes setup.

## Security

The onboarding wizard follows these security practices:

- **Input validation**: Setup codes limited to 2KB, decoded JSON validated, gateway URLs checked via `GatewayUrlHelper`
- **URI scheme whitelists**: Only `ms-settings:` for permissions and `http/https` for browser-launch links
- **Token protection**: Query params stripped from all log output
- **Gateway-owned pairing**: Device approval uses the gateway CLI/API path so scope checks, token issuance, audit, and broadcasts stay centralized
- **Error sanitization**: Exception details logged but not shown to users

## Credential Storage

Gateway credentials are registry-backed. Setup codes and QR payloads create or update a `GatewayRecord`; bootstrap credentials live in `GatewayRecord.BootstrapToken`, long-lived manual tokens live in `GatewayRecord.SharedGatewayToken`, and post-pairing device tokens are saved in the per-gateway identity directory. `SettingsManager` may read legacy `Token` / `BootstrapToken` JSON fields for migration, but it does not write them back.

## Localization

All user-visible strings use `LocalizationHelper.GetString()` with the `Onboarding_*` key namespace. Supported languages are discovered from the `Strings/<locale>/Resources.resw` directories; the current locales are English, French, Dutch, Chinese Simplified, and Chinese Traditional.

Translations are AI-generated following the repo convention. Technical terms (Gateway, Token, Node Mode) are kept in English across all locales.

## Developer Guide

See [DEVELOPMENT.md](../DEVELOPMENT.md#developing--testing-the-onboarding-wizard) for build instructions, environment variables, and testing workflow.

### Test Isolation

`SettingsManager` loads `%APPDATA%\OpenClawTray\settings.json` by default. Onboarding tests must not use `new SettingsManager()` without an isolated settings directory, because local user settings such as `EnableNodeMode=true` change setup behavior.

Use a temp settings directory for tests that construct `SettingsManager`, or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.

### WSL Gateway First-Run/Reset Loop

Use the WSL validation scripts when changing local setup, pairing, gateway registry, node mode, or onboarding behavior. They drive the real tray UI path and keep test identity isolated from the user's real `%APPDATA%`.

```powershell
.\scripts\dev-smoke-wsl-setup.ps1
```

The default smoke loop runs `ResetRedoPreserveIdentity` for two iterations: it unregisters the app-owned `OpenClawGateway` distro between runs while preserving the isolated tray identity. This proves both first-run setup and the "delete WSL and do it again" reset path.

For focused runs:

```powershell
.\scripts\validate-wsl-gateway.ps1 -Scenario FreshMachine -ConfirmDestructiveClean
.\scripts\validate-wsl-gateway.ps1 -Scenario ResetRedoPreserveIdentity -ConfirmDestructiveClean -Iterations 2
```

Validation reports separate verdicts for the setup engine reaching `Complete` and the tray being usable afterward with stored device credentials. It also proves the gateway setup UI path by waiting for the Gateway Welcome/wizard route and `wizard.start` after local setup. A run fails if setup completes but the post-setup tray connection hits `PAIRING_REQUIRED`.

### Key Files

| Path | Purpose |
|------|---------|
| `Onboarding/OnboardingWindow.cs` | Host window for the V2 setup shell |
| `src/OpenClawTray.OnboardingV2/OnboardingV2App.cs` | V2 Functional UI root component and page navigation |
| `src/OpenClawTray.OnboardingV2/OnboardingV2State.cs` | V2 shared setup state |
| `Onboarding/GatewayWizard/GatewayWizardState.cs` | Host-owned state for the embedded gateway wizard |
| `Onboarding/GatewayWizard/GatewayWizardPage.cs` | Embedded provider/model setup page inside V2 |
| `Services/LocalGatewaySetup/SetupCodeDecoder.cs` | Base64url setup code parsing used from Connections |
| `Onboarding/Services/InputValidator.cs` | Security input validation |
| `Onboarding/Services/WizardStepParser.cs` | Wizard JSON step parsing |
| `Onboarding/Services/LocalGatewayApprover.cs` | Local gateway URL classification |
| `Onboarding/Services/PermissionChecker.cs` | Windows permission checks |
| `Services/Connection/GatewayRegistry.cs` | Persistent gateway records and migration target |
| `Services/Connection/GatewayConnectionManager.cs` | Operator/node connection lifecycle used by onboarding |
| `Services/SetupExistingGatewayClassifier.cs` | Existing gateway classification for V2 Welcome and startup gating |
