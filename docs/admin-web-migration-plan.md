# Admin UI Web Migration Plan

This plan migrates admin capabilities from WinForms to the existing React + Vite web stack used by the client UI.

## Phase 1 - Foundation (implemented)
- Add a role-protected `/admin` web route for admins.
- Build a web admin shell with tab navigation.
- Port core read-only and operational surfaces:
  - Overview (stats + active model runtime state)
  - Models (list, download, activate/deactivate, embedding activate, delete)
  - Server Settings (runtime fields and editable token/retention values)
- Keep WinForms Admin available in parallel.

## Phase 2 - Global Admin Controls (implemented)
- Add global system prompt editor.
- Add cloud key status, update, and key test actions.
- Add explicit UI gating for global-admin-only operations.

## Phase 3 - Agent Management (implemented)
- Port Agents tab features:
  - list and edit enabled/model/rag/prompt/tool bindings/max tool calls
  - scenario notes editing
- Preserve current authorization behavior (`GlobalAdmin` for updates).

## Phase 4 - Tools Management (implemented)
- Port Tools tab features:
  - tool catalog list/detail
  - enable/disable + config JSON updates
  - tool stats + reset
- Note: plugin reload is not included because there is no `/api/admin/tools/reload` endpoint in the current server API.

## Phase 5 - RAG Administration (implemented)
- Port collections/documents/grants workflows:
  - create/update/delete collections
  - upload/delete docs
  - grant management by role/user/department

## Phase 6 - Operations and Decommission (web implementation completed)
- Port Users, Departments, and Audit workflows including CSV export.
- Keep Overview/Stats available in web admin for operational visibility.
- Decommission checklist remains an operational rollout step:
  - run one release in dual-admin mode (WinForms + Web Admin)
  - deprecate WinForms Admin once parity validation is signed off
