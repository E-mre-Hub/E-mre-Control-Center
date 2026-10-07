# E-mre Control Center – Privacy Policy

**Version:** 1.0 · **Effective date:** October 7, 2026 · [Türkçe sürüm](../tr/gizlilik-politikasi.md)

This policy explains what information **E-mre Control Center** (the "App"), a maintenance and update utility for Windows, processes,
where it is stored and when the App connects to third parties. In short: **the App requires no account, does not collect information
that identifies you, uses no telemetry, analytics or advertising, and uses no cookies.** Data produced by the App stays on your computer.

## 1. Controller and contact

The App is published by an independent developer under the name "E-mre Control Center". For questions or requests about this policy,
please use the **Issues** section of the project page: <https://github.com/E-mre-Hub/E-mre-Control-Center/issues>
(please do not post personal information in a public issue).

## 2. Data stored on your computer

The App stores the following data **only on your computer** and does not send it to the developer or anywhere else:

| Data | Location | Contents |
|---|---|---|
| Settings and history | `%LOCALAPPDATA%\E-mre Control Center\state.json` | Preferences (notifications, tray, log setting, etc.), a summary of check / update operations, speed test history (up to 50 results; **IP addresses are not stored**), the date you accepted Ookla's terms |
| Session logs | `%LOCALAPPDATA%\E-mre Control Center\Logs\` | Details of operations: commands, result codes, package names, file paths (paths may contain your Windows user name) |
| Setup log | `%TEMP%\E-mre Control Center Kurulum.log` | Installation / uninstallation steps |
| Temporary downloads | App-specific folders under `C:\ProgramData\…` or `%TEMP%\…` | App update and NVIDIA driver installers; deleted after installation |

- Logs are deleted only at your request (General Settings → Log Files; optionally, logs older than 30 days at startup).
- **System Report** and **Support Package** files are created only when you ask, at a location you choose. In these files the computer
  name, user name, user profile path, your device's IP addresses and MAC addresses are **masked**; the Wi-Fi network name is never written.
  You decide whom to share them with.
- When uninstalling, you can delete this data with the "also delete app data" option.

## 3. Services the App connects to

To perform its functions the App connects to the services below. With every connection your IP address is, by the nature of the
internet, visible to the server you connect to. These services are governed by their own privacy policies.

| Service | When | Information sent |
|---|---|---|
| **GitHub** (api.github.com, github.com) | Update check at startup and every 5 minutes while the App is running; downloading the installer when you click "Update" | A user agent containing the App version (`E-mre-Control-Center/x.y.z`); no account or personal information |
| **Microsoft connectivity test** (msftconnecttest.com, msftncsi.com) | To verify the internet connection | A standard request; no content is sent |
| **Microsoft services** (Windows Update, winget, Microsoft Store, Microsoft Defender, Microsoft Edge Update) | When you start a check / update | Performed by Windows' own components and subject to Microsoft's privacy statement. For Defender, Microsoft's public version page is read |
| **App publishers** | When an app is updated via winget | The installer is downloaded by winget from the publisher's server (e.g. discord.com) |
| **NVIDIA** (nvidia.com, gfwsl.geforce.com, download.nvidia.com) | When the NVIDIA card is checked / the driver is updated | Graphics card model, Windows version and language (to find the right driver) |
| **Cloudflare** (speed.cloudflare.com, 1.1.1.1) | When you run a speed test or network diagnostics | Test traffic. Cloudflare returns your public IP address and ISP so they can be shown on screen; the IP address is not stored |
| **DNS diagnostics** | When you start the DNS test | Queries to your configured DNS servers for well-known domains (microsoft.com, cloudflare.com, google.com, wikipedia.org) |
| **Speedtest by Ookla** (optional) | Only if you choose the Ookla provider and accept Ookla's terms | The test is run with Ookla's official tool; results are sent to Ookla and governed by Ookla's [Privacy Policy](https://www.speedtest.net/about/privacy) and [Terms of Use](https://www.speedtest.net/about/terms). You can withdraw your acceptance in the App |
| **Google Forms** (optional) | Only if you choose to send feedback while uninstalling | The uninstall reasons you select, the message you type, the App version and the Windows version. **Your name, e-mail address, computer name or user name are not sent.** Please do not type personal information in the message |

## 4. Cookies and tracking technologies

The App uses **no cookies, advertising identifiers, fingerprinting, telemetry or analytics tools** and collects no usage statistics.
The GitHub pages from which the App is downloaded are subject to GitHub's own privacy and cookie policies.

## 5. Purpose and legal basis

- Data stored on your computer is used solely for the App to work (showing history, remembering settings, troubleshooting).
- Optional feedback is used only to improve the App and is based on your explicit choice (consent).
- The developer keeps no database that identifies you through the App and does not sell, rent or share data.

## 6. Retention

- Local data stays on your computer until you delete it or uninstall the App together with its data.
- Feedback responses are kept in the developer's Google Sheets account for as long as needed to improve the App. Responses are not linked
  to your identity, so individual responses cannot be attributed to you.

## 7. Your rights (GDPR and KVKK)

Under the EU General Data Protection Regulation (GDPR), where applicable, and Turkish Personal Data Protection Law No. 6698 (KVKK), you
have the right to access, rectify and erase your data, to object to processing and to lodge a complaint. Since most App data is only on
your computer, you can view and delete it yourself. For other requests use the contact channel in section 1. You may also lodge a
complaint with your local data protection authority.

## 8. Children's privacy

The App is a system maintenance tool and is not directed to children; no data is knowingly collected from children.

## 9. Security

The App verifies installers before running them (app updates: size + SHA-256 + product name + version; NVIDIA driver: NVIDIA digital
signature) and keeps files that will run with administrator rights in folders that only administrators can access. No method can
guarantee 100% security.

## 10. Changes

This policy may be updated. The current version is always available in the `legal/` folder of the project repository and linked from
the App (General Settings → Yasal ve Gizlilik / Legal and Privacy). Significant changes are noted in the release notes.
