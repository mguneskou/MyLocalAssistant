# Admin UI Web Migration Plan

This plan migrated admin capabilities from WinForms to the existing React + Vite web stack used by the client UI.

## Phase 1 - Foundation (implemented)
- Add a role-protected `/admin` web route for admins.
- Build a web admin shell with tab navigation.
- Port core read-only and operational surfaces:
  - Overview (stats + active model runtime state)
  - Usage (7/30/90-day stats)
  - Models (list, download, activate/deactivate, embedding activate, delete)
  - Server Settings (runtime fields and editable token/retention values)

## Phase 2 - Global Admin Controls (implemented)
- Add global system prompt editor.
- Add cloud key status, update, and key test actions.
- Add explicit UI gating for global-admin-only operations.

## Phase 3 - Agent Management (implemented)
- Port Agents tab features:
  - list and edit enabled/model/rag/prompt/tool bindings/max tool calls
  - scenario notes editing
  - prompt test workbench
- Preserve current authorization behavior (`GlobalAdmin` for updates).

## Phase 4 - Tools Management (implemented)
- Port Tools tab features:
  - tool catalog list/detail
  - enable/disable + config JSON updates
  - tool stats + reset
  - plug-in reload (`/api/admin/tools/reload`)

## Phase 5 - RAG Administration (implemented)
- Port collections/documents/grants workflows:
  - create/update/delete collections
  - upload/delete docs
  - grant management by role/user/department

## Phase 6 - Operations and Decommission (completed)
- Port Users, Departments, and Audit workflows including CSV export.
- Keep Overview/Stats available in web admin for operational visibility.
- Tray host "Open Admin" now opens the web admin route (`/admin`).
- Packaging no longer publishes or distributes the legacy desktop admin executable.
