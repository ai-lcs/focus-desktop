# Installer test isolation

Use a disposable Windows VM with a clean checkpoint for package-level Setup tests. The Dock candidate now has its own AppId, install directory, shortcuts and installed data directory. Still use a VM for the destructive install/uninstall matrix; old host isolation scripts retain legacy identity assumptions and must not be reused. Do not install the candidate on the host, including with `/CURRENTUSER`.

## Account and data identity map

| Stage | Account and integrity | Profile and registry | Shortcut and first-run behavior |
| --- | --- | --- | --- |
| Setup | Default `PrivilegesRequired=admin`; elevated administrator token | Program files and machine uninstall registration under HKLM; Setup itself does not choose a profile to clear | Writes the configured desktop and Start Menu shortcuts; no host test install is allowed |
| First-run app | `[Run]` requests `runasoriginaluser`; Inno documents that this has no effect if Setup was already elevated or launched with “Run as administrator” | App uses the launched process account's `LocalAppData\focus-desktop-dock`; application settings are file-based, with no app registry access in source | `--first-run-if-needed` lets the app check that actual account's profile after launch; it must skip silently when the profile already exists |
| Uninstall helper | Runs with the current uninstaller credentials, which can differ if another admin account was entered | Clear only when process SID, active WTS session SID, exact current-user data path, and directory ACL owner all match | The helper restores desktop state and holds desktop/profile leases through any explicit deletion |
| Clear-data denial | Identity query failure, alternate admin credential, other profile path/owner, reparse point, active profile use, or missing lease | Keep user data; refuse the clear-data uninstall step | Inno does not delete the profile after the helper exits |

The helper owns the removal window: it acquires the desktop and profile leases, validates profile usage, and deletes the data while both leases remain held. Inno does not run a post-uninstall profile deletion after the helper exits. A session identity check cannot authorize an unknown or different administrator account.

1. Start from a fresh VM checkpoint with no Focus Desk HKLM or HKCU uninstall entry. Do not map the host's user profile, LocalAppData, desktop, or Documents into the VM.
2. Transfer only the exact candidate Setup package and its SHA-256 manifest. Keep host shared folders read-only and limited to the package if a VM transfer channel is required.
3. Install with the default administrator mode inside the VM. Use only synthetic VM account data and a disposable study-folder sentinel.
4. Run the first-run, keep-data uninstall, reinstall, and explicit clear-data cases inside the VM. Record the VM account SID, LocalAppData path, Setup hash, and before/after HKLM uninstall state in the VM evidence directory.
5. Test a different administrator account only if a disposable VM account is available. The expected safe result for a SID/session mismatch is preserved user data and a refused clear-data uninstall.
6. Discard or restore the VM checkpoint after the matrix. No host LocalAppData directory or host AppId registration is moved, renamed, or overwritten.

Windows Sandbox is an acceptable single-run alternative only when host drives and profile folders are not shared into the sandbox. Use a checkpointed VM for upgrade/reinstall sequences that need state across multiple runs. If neither environment is available, run only the read-only `verify-uninstall-data-policy.ps1` source check and report package-level install/uninstall acceptance as untested.
