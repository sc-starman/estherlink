Place OpenVPN installer payload files here for Windows local gateway mode.

Required at runtime:
- `OpenVPNInstaller.msi` (preferred)
  or `OpenVPNDriverInstaller.msi`
  or `OpenVPNDriverInstaller.exe`

Notes:
- `openvpn.exe` is resolved from the installed OpenVPN path
  (for example `C:\Program Files\OpenVPN\bin\openvpn.exe`).
- The service will run the MSI/installer silently on first use if OpenVPN is not installed.
