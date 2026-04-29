# MyLocalAssistant — install scripts

## install-service.ps1

Publishes the server self-contained for win-x64 (no .NET runtime needed on the
target box), optionally generates a self-signed TLS cert, writes the listen
URL into `config\server.json`, and registers `MyLocalAssistantServer` as a
Windows service running under `NT AUTHORITY\NetworkService`.

Run from an **elevated** PowerShell at the repo root or inside `scripts\`.

```powershell
# Plain HTTP on :8080
.\install-service.ps1

# HTTPS with auto-generated self-signed cert on :8443
.\install-service.ps1 -EnableHttps

# HTTPS with your own PFX
.\install-service.ps1 -EnableHttps -CertificatePath C:\certs\mla.pfx -CertificatePassword 'hunter2'

# Custom install dir / port
.\install-service.ps1 -InstallPath D:\MLA\Server -Port 9000
```

The script is idempotent — re-run it to upgrade. Existing data, logs, and the
JWT signing key in `config\server.json` are preserved.

### TLS notes

- Self-signed certs are written as `config\tls.pfx` with an empty password
  and access is restricted to the service account via NTFS ACLs.
- Clients will see a trust warning until you distribute the cert to their
  Trusted Root store. For real deployments use a cert from your internal CA
  and pass `-CertificatePath`.
- Changing the listen URL, cert path, or signing key requires restarting the
  service: `Restart-Service MyLocalAssistantServer`.

### LDAP / Active Directory

LDAP is configured by editing `config\server.json` after installation
(no UI for this — it's set-once for the org). Add an `ldap` block:

```json
"ldap": {
  "enabled": true,
  "server": "dc01.corp.local",
  "port": 389,
  "useSsl": false,
  "baseDn": "DC=corp,DC=local",
  "bindDn": "CN=svc-mla,OU=Service Accounts,DC=corp,DC=local",
  "bindPassword": "<service account password>",
  "userFilter": "(&(objectClass=user)(sAMAccountName={0}))",
  "usernameAttribute": "sAMAccountName",
  "displayNameAttribute": "displayName",
  "adminGroup": "MLA-Admins",
  "groupToDepartment": {
    "MLA-Engineering": "R&D",
    "MLA-HR": "HR",
    "MLA-Finance": "Finance"
  }
}
```

Behaviour:
- LDAP-authenticated users are auto-provisioned on first successful login
  (`AuthSource = "ldap"`). Display name, admin flag, and department membership
  are re-synced from AD on every login.
- The local `admin` / `admin` bootstrap account stays usable as a break-glass
  even when LDAP is enabled (it's `AuthSource = "local"`).
- Local accounts are unaffected — LDAP is only consulted for usernames not
  present locally, or for rows already marked `AuthSource = "ldap"`.
- LDAP users cannot change/reset their password via the API (HTTP 403); they
  manage it in AD as usual.

Restart the service after changing this section.

## uninstall-service.ps1

```powershell
# Remove the service, leave files on disk
.\uninstall-service.ps1

# Remove the service AND wipe the install dir (data, models, vectors, logs)
.\uninstall-service.ps1 -PurgeData
```
