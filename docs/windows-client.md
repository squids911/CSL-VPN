# Windows client notes

The client is a management-plane agent plus an experimental data plane
(behind `Tunnel:Enabled`, default off until validated on hardware): the
service enrolls the device, keeps a session with the control plane, and —
when enabled — brings up the SoftEther L2 tunnel with the assigned address.

## Components

- `CSL.Vpn.WindowsService` — the agent (Windows service, runs as SYSTEM).
- `CSL.Vpn.WindowsTray` — tray app showing agent status from the state file.

## Agent lifecycle

1. On start the agent looks for a one-time enrollment code in
   `C:\ProgramData\CSL VPN\enrollment-code.txt` (plain text, code only).
2. With a code present it calls `POST /enroll` on the control plane
   (`https://v.csl.by` by default; see `appsettings.json`), sending its
   persistent device GUID and the machine name.
3. The issued credential and device GUID are stored in
   `C:\ProgramData\CSL VPN\identity.json` (ACL: SYSTEM + Administrators only,
   best effort). The code file is deleted after a successful enrollment.
4. The agent heartbeats every 60 seconds (`HeartbeatIntervalSeconds`),
   refreshing its single active session. A rejected credential (401) is
   forgotten; the agent then waits for a new enrollment code.
5. Status is mirrored to `C:\ProgramData\CSL VPN\state.json`, which the tray
   app displays (including a `tunnel` field once the data plane is enabled).

## Data plane (experimental)

With `Tunnel:Enabled = true` in `appsettings.json`, after each successful
heartbeat the service ensures the SoftEther tunnel is up:

1. Starts the SoftEther VPN Client service (`SEVPNCLIENT`) — the bundled
   client is expected under `Tunnel:SoftEtherDir`
   (default `C:\Program Files\CSL VPN\softether`).
2. Creates virtual NIC `CSLVPN` and connection account `CSLVPN`
   (`v.csl.by:5555`, hub `CSL`) if missing.
3. Sets the account password to the device credential (`/TYPE:radius`) — the
   hub authenticates it via the control-plane RADIUS facade. Username is the
   device GUID.
4. Connects, waits for the link, then sets the control-plane-assigned address
   statically (`netsh`, gateway 10.255.0.1, mask 255.255.252.0). The hub has
   no DHCP; the control plane owns the pool.
5. Re-applies only when the device/credential/address changes or the link
   drops. On credential rejection (401) and on shutdown the tunnel is torn
   down.

Credential rotation (re-enrollment) is picked up automatically: the next
tick detects the changed credential and re-provisions the account.

## Operator workflow

```powershell
# On the operator machine (or the server), create a one-time code:
curl -X POST "https://v.csl.by/admin/enrollment-codes" -H "X-Admin-Key: <ADMIN_API_KEY>"

# On the Windows machine (as Administrator):
New-Item -ItemType Directory -Force "C:\ProgramData\CSL VPN"
Set-Content "C:\ProgramData\CSL VPN\enrollment-code.txt" "<code>"
# install and start the agent, e.g.:
sc.exe create "CSL VPN Agent" binPath= "C:\csl-vpn\CSL.Vpn.WindowsService.exe" start= auto
sc.exe start "CSL VPN Agent"
```

The tray app needs no configuration; start it after the agent. A packaged,
signed installer is a future workstream item.

## Reinstall behaviour

Keep `device-id.txt` (and ideally `identity.json`) across a reinstall and the
device keeps its VPN address; enrollment with the preserved GUID re-issues
the credential and invalidates the old one.
