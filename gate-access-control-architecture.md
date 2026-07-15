# Gate Access Control Integration — Architecture

**BearMGMT ↔ OpenTech Alliance IoE (Insomniaccia) — Physical Gate Access for Self-Storage**

| Field | Value |
|---|---|
| Document type | Architecture & Integration Design |
| Status | Draft for review |
| Date | 2026-07-15 |
| Audience | Backend team, reviewers, future module owners (Lease/Payment/Notifications) |
| Sources | POC-validated vendor integration guide v2 (docx), `C:\repo\AccessControlPoc` (outbound API spike), `C:\repo\IoEWebhook` (inbound SNS webhook spike), BearMGMT codebase conventions (`.claude/rules/*`), business requirements TM-009, MO-003/MO-005, TR-005, PM-007, TP-004/TP-013 |
| Vendor APIs | Access Control Service v2.0 · Token Auth Service v1.0 · Access Event API (separate host) · IOE Webhooks (AWS SNS) |

---

## Table of Contents

1. [Executive Summary & Scope](#1-executive-summary--scope)
2. [Business Requirements Traceability](#2-business-requirements-traceability)
3. [Domain Glossary & Canonical Model](#3-domain-glossary--canonical-model)
4. [Vendor Integration Profile: OpenTech IoE](#4-vendor-integration-profile-opentech-ioe)
5. [Multi-Tenancy & Account Mapping Strategy](#5-multi-tenancy--account-mapping-strategy)
6. [Provider Abstraction (Anti-Corruption Layer)](#6-provider-abstraction-anti-corruption-layer)
7. [Database Design](#7-database-design)
8. [Gate Code Lifecycle & Security](#8-gate-code-lifecycle--security)
9. [Consistency Model — What "Atomic" Really Means](#9-consistency-model--what-atomic-really-means)
10. [Flow: Tenant Move-In (TM-009)](#10-flow-tenant-move-in-tm-009)
11. [Flows: Move-Out & Unit Transfer (MO-003/MO-005, TR-005)](#11-flows-move-out--unit-transfer-mo-003mo-005-tr-005)
12. [Delinquency Escalation Contract (PM-007)](#12-delinquency-escalation-contract-pm-007)
13. [Scheduled Execution & Worker Architecture](#13-scheduled-execution--worker-architecture)
14. [Event Ingestion: Webhooks + Polling Backfill](#14-event-ingestion-webhooks--polling-backfill)
15. [Application Layer Design (Vertical Slices)](#15-application-layer-design-vertical-slices)
16. [Tenant Portal Display & the 2×2 Restriction Matrix (TP-004/TP-013)](#16-tenant-portal-display--the-22-restriction-matrix-tp-004tp-013)
17. [Authorization & Permissions](#17-authorization--permissions)
18. [Observability, Audit & Alerting](#18-observability-audit--alerting)
19. [Failure Modes & Runbook](#19-failure-modes--runbook)
20. [Testing Strategy](#20-testing-strategy)
21. [Phased Delivery Plan & Module Dependencies](#21-phased-delivery-plan--module-dependencies)
22. [Open Questions, Risks & Decision Log](#22-open-questions-risks--decision-log)
- [Appendix A — Sample Payloads (Sanitized)](#appendix-a--sample-payloads-sanitized)
- [Appendix B — Configuration Reference](#appendix-b--configuration-reference)

---

## 1. Executive Summary & Scope

BearMGMT must control **physical gate access** at self-storage properties: issue a unique gate code to each tenant at move-in, restrict it on delinquency, revoke it at move-out or transfer, and record every gate interaction (granted/denied) for staff visibility and audit. The gate hardware (keypads, controllers) is managed by a third-party vendor platform — **OpenTech Alliance IoE (Insomniaccia)** for the MVP — which Bear integrates with over three REST services plus an AWS SNS webhook stream.

### Key architectural decisions (summary)

| # | Decision | Rationale (detail in section) |
|---|---|---|
| D1 | **Provider-agnostic port** `IGateAccessProvider` in Application; `OpenTech` adapter in Infrastructure | Future switch to PTI/Janus/other must not touch Domain or Application (§6) |
| D2 | **Credential scope = organization** (N provider configs per org allowed); per-org OpenTech account **recommended**, single shared account **supported** — final choice is a client decision | Isolation, blast radius, commercial reality; schema supports both (§5, §22) |
| D3 | **DB intent first, vendor call second** — never the reverse | A vendor-side change with no Bear record is an unauditable security hole; a Bear-side pending row is visible and retryable (§9) |
| D4 | **`gate_operations` table = merged outbox + scheduler**, executed by a Worker dispatcher (`FOR UPDATE SKIP LOCKED`) | Wolverine has no durable message persistence today; scheduled revocations (transfers) must survive restarts and stay cancellable/re-datable (§13) |
| D5 | Gate codes stored **AES-256-GCM encrypted (KMS envelope)** + deterministic **HMAC for uniqueness index**; never hashed-only, never logged | Vendor never echoes the code back → Bear DB is the only copy → must be recoverable to display to the tenant (§8) |
| D6 | **Move-out "atomicity" = one local DB transaction + guaranteed-eventual external revocation** with a fail-open SLA and staff alerting | An HTTP call cannot join a Postgres transaction; the honest contract is defined explicitly (§9, §11) |
| D7 | **Webhook events are observational only** — no business state transition depends on webhook delivery; nightly polling backfill closes gaps | SNS retry policy is finite; control flow rides on commands/operations instead (§14) |
| D8 | Vendor unit status (`Vacant/Rented/Delinquent`) is a **derived projection of Bear state — never synced vendor→Bear** | Two-way sync guarantees drift/oscillation; Bear `UnitStatus` is master (§3) |

### Scope & phases

| Phase | Contents | Blocked on |
|---|---|---|
| **0 — POC (done)** | Outbound API spike (`AccessControlPoc`), inbound SNS webhook spike (`IoEWebhook`), QA validation of all vendor workflows | — |
| **1 — MVP core (buildable now)** | Provider config + secrets, facility/unit mapping & onboarding, gate code generation/encryption, provision/suspend/reinstate/revoke commands (staff-triggered via admin endpoints), `gate_operations` dispatcher in Worker, webhook ingestion + polling backfill, gate events list, audit/observability, permissions | Nothing — Property & Unit modules exist |
| **2 — Lease integration & tenant experience** | Move-in finalization trigger, `lease_id` binding, move-out bundle, transfers with scheduled revocation, tenant portal code display (2×2 matrix), confirmation email + bounce alert | Lease/Contract module, Notifications module |
| **3 — Delinquency & hardening** | PM-007 escalation engine (step rules → gate suspend/reinstate/overlock task), SNS→SQS pipeline, event table partitioning, additional providers | Payment module |

In Phase 1 the same commands that Phase 2 automates are exposed to **staff** through the Admin Portal, so the integration is fully exercisable before the Lease module exists.

---

## 2. Business Requirements Traceability

| Req | Requirement summary | Design elements | Sections | Phase |
|---|---|---|---|---|
| **TM-009** | Auto-generate unique gate code per tenant, tied to active lease, at move-in finalization; show on-screen + email with move-in confirmation; auto-deactivate at lease end/move-out; failure message if generation fails; staff alert on email bounce | `ProvisionGateAccessCommand`, CSPRNG code generation + facility-scoped uniqueness, sync-attempt/async-guarantee pattern, `pending_activation` UX state, `GateAccessProvisioned` notification event, bounce-alert contract for Notifications module | §8, §9, §10 | 1 (staff-triggered) → 2 (lease-triggered) |
| **MO-003 / MO-005** | Gate revocation bundled with autopay disable, unit status change, inspection work order — atomic as a single transaction | Single EF Core transaction for all local effects + `gate_operations` outbox row; unit-level `vacate` as primary provider call; fail-open SLA + dead-letter alert | §9, §11, §13, §19 | 1 (revoke command) → 2 (bundle) |
| **TR-005** | Transfer: new gate code for new unit immediately; old unit's revocation scheduled at old unit's move-out date | Immediate provision for new unit + `gate_operations` row with `status='scheduled'`, `scheduled_for` in **property timezone**, re-datable/cancellable | §11, §13 | 2 |
| **PM-007** | Delinquency escalation profiles: per-overdue-day step rules → notification / document / gate lock / gate unlock / overlock | `SuspendGateAccessCommand`/`ReinstateGateAccessCommand` (exist in Phase 1), `TenantDelinquencyStageChanged` event contract, escalation profile/step tables, overlock modeled as **work-order output, not a gate API call** | §12 | 1 (commands) → 3 (engine) |
| **TP-004 / TP-013** | Gate code shown in tenant's Unit Detail; struck-through + lock icon + tooltip when restricted; **code withheld server-side when restricted**; gate restriction and portal restriction are independent states (2×2 matrix) | `GetTenantGateCodeQuery` omits code from DTO in the handler when `is_gate_restricted` or status ≠ active; independent `is_gate_restricted` flag on credential; matrix behavior enumerated | §16 | 2 |
| *(implicit)* | Multi-org mapping: Bear Organization→Property→Unit vs OpenTech Account→Facility→Unit; who maps, how; single vs per-org vendor account | `gate_provider_configs` / `gate_facility_mappings` / `gate_unit_mappings`; Org Admin-driven onboarding flow; trade-off analysis + client decision | §5, §7, §22 | 1 |
| *(implicit)* | Future provider switch (OpenTech → other) must be possible | `IGateAccessProvider` port + capabilities, canonical models, `provider_type` discriminator, migration playbook | §6 | 1 (design) |

---

## 3. Domain Glossary & Canonical Model

> **Terminology hazard:** in BearMGMT, "tenant" usually means the *SaaS tenant* (an Organization, per `TenantContext`). In this document, **tenant = the end renter** (a person renting a storage unit), matching the business requirements. The SaaS tenant is always called **Organization**.

### Bear ↔ OpenTech term mapping

| Bear concept | Bear entity/table | OpenTech concept | Vendor key | Notes |
|---|---|---|---|---|
| Organization | `organizations` | **Account** | account credentials (API key/secret, client id/secret) | The commercial relationship boundary. One org ↔ one (or more) vendor accounts via `gate_provider_configs` (§5) |
| Property | `properties` (`Property`, has `Timezone`) | **Facility** | `facilityId` (int), `propertyNumber` (string) | 1:1 via `gate_facility_mappings`. `propertyNumber` ↔ `Property.Code` is the human-matching hint during onboarding |
| Unit | `units` (`Unit.UnitNumber`) | **Unit** | `unitId` (int), `unitNumber` (string, unique per facility) | 1:1 via `gate_unit_mappings`. Unit must exist vendor-side **before** a visitor can reference it |
| Tenant (renter) on a unit / future Lease | `users` (role `tenant`) today; `leases` in Phase 2 | **Visitor** (`isTenant: true`) | `visitorId` (int) — vendor-assigned, **no external key supported** | Captured immediately from the create response and stored on `gate_access_credentials` — it is the *only* correlation key |
| Gate code | `gate_access_credentials.access_code_*` | `accessCode` (digits 1–10, write-only) | — | Never echoed back by the vendor → Bear DB is the only recoverable copy (§8) |
| Gate interaction | `gate_events` | **Gateway Event** (`FacilityEventModel` / SNS `GatewayEvent`) | `eventId` | Append-only, deduped on `(provider_config_id, external_event_id)` |
| Unit occupancy status | `Unit.Status` (Draft/Vacant/Occupied/Reserved/Maintenance) | `unit.status` (Vacant/Rented/Delinquent) | — | **One-way projection Bear → vendor.** Never sync vendor status back into Bear (D8) |

### Anti-corruption layer

Vendor DTOs (`VisitorResponseModel`, `FacilityEventModel`, SNS envelopes, …) **never cross the Infrastructure boundary**. The Application layer speaks only Bear-canonical records (`GateFacility`, `GateUnitRef`, `GateVisitorRef`, `GateEvent`, `GateProviderCapabilities` — §6). All translation, including raw event-type-enum → canonical event-type mapping, happens inside the OpenTech adapter. This is what makes D2 (multiple accounts) and the future-provider requirement cheap instead of a rewrite.

---

## 4. Vendor Integration Profile: OpenTech IoE

Everything below is **validated against the QA environment** via the POC Postman collection and `AccessControlPoc`; ⚠ marks caveats discovered during validation.

### 4.1 Service hosts (three, not one)

| Service | Purpose | Base URL (QA) | Required headers |
|---|---|---|---|
| Auth | Token issuance (Token Auth Service v1.0) | `https://auth.insomniaccia-qa.com` | `Content-Type: application/x-www-form-urlencoded` |
| Access Control | Facilities, units, visitors, devices (v2.0) | `https://accesscontrol.insomniaccia-qa.com` | `Authorization: Bearer …`, `api-version: 2.0`, `X-Correlation-ID` |
| Access Event | Event log retrieval (EventProxy, same v2.0 spec, **separate host**) | `https://accessevent.insomniaccia-qa.com` | `Authorization: Bearer …`, `api-version: 2.0`, `X-Correlation-ID` |

⚠ The Access Event API is a **separate host** — an earlier guide draft assumed it was consolidated; the QA POC disproved that. Store all three base URLs per provider config.

⚠ The three action-group "runner" endpoints (`POST …/actiongroups/{id}/open|hold|close` — remote gate open) default to `api-version: 3.0`, not 2.0. Out of MVP scope, but always set the header explicitly.

### 4.2 Authentication — OAuth2 **password grant** (confirmed, not client_credentials)

`POST /auth/token` (form-encoded). The vendor-issued **API key/secret pair rides in the `username`/`password` fields**; `client_id`/`client_secret` are the separate application credentials:

```
grant_type=password
client_id={AC_CLIENT_ID}
client_secret={AC_CLIENT_SECRET}
username={AC_API_KEY}        ← vendor API key, not a user name
password={AC_API_SECRET}     ← vendor API secret, not a user password
```

Response: `{ token_type, access_token, expires_in (3600s), refresh_token, id_token }`.

> **Never commit these values.** All four are stored in AWS Secrets Manager and referenced by `gate_provider_configs.secret_ref` (§5, §7). The QA credentials currently sitting in the POC's `appsettings.json` must be **rotated before production** and must never be migrated into Bear source. Some logging middleware does not mask `username`/`password` form fields by default — the token request body must be excluded from request logging.

**Token caching strategy** (replicates Bear's proven `Auth0ManagementApiClient` pattern — singleton, `ConcurrentDictionary` cache, per-entry `SemaphoreSlim` double-checked locking):

- Cache key: **`providerConfigId`** (one vendor account = one token), not scope.
- Expiry: `expires_in − 60s` safety buffer.
- Renewal prefers the **`refresh_token` grant** (`grant_type=refresh_token`) so the shared API secret travels less; on refresh failure, fall back to a fresh password grant (the POC's `AccessControlTokenClient` already implements this fallback).
- On `401` from any API call: refresh once, retry the original request once, then surface `ExternalServiceException`.
- On repeated 401 after refresh: evict the Secrets Manager cache entry too (handles credential rotation, §6.2).

```mermaid
sequenceDiagram
    autonumber
    participant AC as OpenTech Adapter
    participant TP as GateTokenProvider (singleton cache)
    participant AUTH as OpenTech Auth API
    participant API as OpenTech Access Control API

    AC->>TP: GetTokenAsync(providerConfigId)
    alt Cached token valid (now < expiry - 60s)
        TP-->>AC: cached access_token
    else Expired or missing
        TP->>TP: acquire per-config semaphore, double-check
        alt Have refresh_token
            TP->>AUTH: POST /auth/token (grant_type=refresh_token)
            AUTH-->>TP: new access_token + refresh_token
        else No refresh_token or refresh failed
            TP->>AUTH: POST /auth/token (grant_type=password, key/secret)
            AUTH-->>TP: access_token + refresh_token
        end
        TP-->>AC: access_token (cached, expiry-60s)
    end
    AC->>API: request with Bearer + api-version: 2.0 + X-Correlation-ID
    alt 401 Unauthorized
        AC->>TP: ForceRefreshAsync(providerConfigId)
        TP->>AUTH: POST /auth/token
        AUTH-->>TP: new token
        AC->>API: retry original request once
    end
    API-->>AC: response
```

### 4.3 Vendor object model & lifecycle endpoints (v2.0, QA-confirmed)

| Bear trigger | Vendor call | Behavior / notes |
|---|---|---|
| Property linked | `GET /facilities`, `GET /facilities/detail?propertyNumber=` | Discovery for the mapping UI; response is a plain array of `{id, name, propertyNumber}` |
| Unit onboarded | `POST /facilities/{facilityId}/units` `{unitNumber}` | Returns `unit.id` — store it. `unitNumber` must be unique per facility. ⚠ The POC collection accidentally sent this to the *event* host; always target the access-control host |
| Move-in | `POST /facilities/{facilityId}/visitors` `{firstName, lastName, isTenant: true, unitId, accessCode, …}` | Returns `visitor.id` — **capture and store immediately; there is no lookup by external key**. Unit must already exist. `accessCode`: digits only, 1–10 chars, never echoed on reads |
| Delinquent | `POST /facilities/{facilityId}/visitors/{visitorId}/disable` (empty body) | `isEnabled=false`, `isLockedOut=true`; unit status unchanged. Bulk twin: `POST …/units/{unitId}/disable` (all occupants + unit → Delinquent) |
| Reinstate | `POST /facilities/{facilityId}/visitors/{visitorId}/enable` (empty body) | ⚠ The POC sent a leftover JSON body here — the spec defines **no body**; it was silently ignored. Omit it |
| Move-out | `POST /facilities/{facilityId}/units/{unitId}/vacate` (empty body) — **primary, spec-documented** | Deactivates *all* unit visitors and flips unit → Vacant |
| Move-out (single visitor) | `POST /facilities/{facilityId}/visitors/{visitorId}/remove` (empty body) | ⚠ Worked on an `isTenant: true` visitor in QA, but the spec says tenant deactivation requires unit vacate. Treat as capability-gated fallback; confirm with vendor (§22 V-4) |
| Event backfill | `GET {eventUrl}/events/facilities/{facilityId}?minDate&maxDate&limit&offset` | Paged; `204 No Content` = empty page, not an error; dedup key = event `id` |
| Event types | `GET {eventUrl}/events/types` | Cache at startup; includes category names |

⚠ **`timeGroupId`/`accessProfileId` = 0 sentinel:** the confirmed POC request sent literal `0` for both; per schema, `null`/omission is what triggers the documented defaults ("24-hour" time group, "All Access" profile). An explicit `0` targets whatever record has id 0. Bear will **omit both fields unless configured** in `gate_provider_configs.settings`, and this is escalated as vendor question V-7 (§22).

`suppressCommands` (batch device pushes) is supported but the vendor flags it for deprecation — Bear defaults it to `false` (real-time pushes) everywhere.

### 4.4 Error semantics & retry taxonomy

| Vendor response | Meaning | Bear behavior |
|---|---|---|
| `200` | Success | Process |
| `204` | Empty event page | Empty page, not an error |
| `400` | Invalid data | **No retry.** Log body, mark operation `failed` for human review |
| `401` | Token expired | Refresh once → retry once → else `ExternalServiceException` |
| `403` | Credential/permission problem | No retry; mark config `sync_error`; staff alert (likely rotation/provisioning issue) |
| `404` | Bad facility/unit/visitor id | No retry; flag mapping drift; reconciliation candidate |
| `409` | Already in requested state | **Treat as idempotent success** |
| `5xx` / timeout | Vendor-side failure | Exponential backoff 2s/4s/8s (max 3 in-process attempts via the standard resilience handler), then hand back to the `gate_operations` retry schedule (§13) |

All outbound calls carry `X-Correlation-ID` (propagated by Bear's existing `CorrelationIdDelegatingHandler`), so a Bear request can be traced in vendor logs.

---

## 5. Multi-Tenancy & Account Mapping Strategy

This section answers the four questions raised during design: *how do Bear's Organization→Property→Unit map to OpenTech's Account→Facility→Unit, do we need one OpenTech account or many, who performs the mapping and how, and how do we keep the door open for other providers?*

### 5.1 The mapping model

```mermaid
flowchart LR
    subgraph Bear["BearMGMT (multi-tenant, shared DB)"]
        ORG[Organization]
        P1[Property A]
        P2[Property B]
        U1[Unit 101]
        U2[Unit 102]
        ORG --> P1
        ORG --> P2
        P1 --> U1
        P1 --> U2
    end
    subgraph Mapping["Gate mapping tables (Bear DB)"]
        GPC[gate_provider_configs<br/>org-scoped, provider_type,<br/>secret_ref → AWS Secrets Manager]
        GFM[gate_facility_mappings<br/>property_id UNIQUE]
        GUM[gate_unit_mappings<br/>unit_id UNIQUE]
    end
    subgraph OT["OpenTech IoE"]
        ACC[Account<br/>API credentials]
        F1[Facility 8594<br/>propertyNumber 123]
        VU1[Unit 38616]
        ACC --> F1
        F1 --> VU1
    end
    ORG -. "1..N configs" .-> GPC
    GPC -.-> ACC
    P1 -. "1:1" .-> GFM
    GFM -.-> F1
    U1 -. "1:1" .-> GUM
    GUM -.-> VU1
```

- **Organization ↔ Account** — via `gate_provider_configs`. An organization may hold **multiple** configs (e.g., it acquired facilities that came with their own OpenTech account). This resolves the "one account or many?" question *structurally* instead of by policy: configs are just rows.
- **Property ↔ Facility** — strictly **1:1** (`UNIQUE (property_id)`). A property physically has one gate system; supporting two providers on one gate is not a real scenario and would double every write path. Each facility mapping points at the provider config that owns it.
- **Unit ↔ vendor Unit** — strictly 1:1 (`UNIQUE (unit_id)`), scoped under its facility mapping. Natural matching key during import: `unitNumber` ↔ `Unit.UnitNumber` (vendor-unique per facility).
- **Tenant/Lease ↔ Visitor** — per active occupancy, on `gate_access_credentials` (§7.4). The vendor-assigned `visitorId` is captured at create time; there is no way to look a visitor up later by a Bear key.

### 5.2 One OpenTech account or one per organization?

**Recommendation: one OpenTech account per organization** (credentials per org). **The schema supports both models**, and because this is ultimately a commercial question (who contracts with OpenTech — each storage operator, or Bear as a reseller?), the final choice is recorded as **client decision C-1 (§22)**.

| Dimension | Per-org accounts (recommended) | Single shared Bear account |
|---|---|---|
| Commercial reality | Each organization contracts with OpenTech; the gate hardware is theirs | Makes Bear a de-facto reseller — a contractual/liability question, not an engineering one |
| Security blast radius | A compromised secret exposes **one org's** gates | One secret exposes **every customer's** gates — hard to accept |
| Tenant isolation | Vendor-side isolation matches Bear-side isolation | Isolation exists only in Bear's mapping tables; `propertyNumber` collisions across orgs become possible |
| Rate limits / throttling | Isolated per account | Shared; one org's bulk sync can starve another (noisy neighbor) |
| Webhook/event routing | Subscription per account; `providerConfigId` in the webhook URL makes attribution trivial | All events in one stream; must route by facility — workable but weaker |
| Offboarding an org | Delete config + secret; clean | Untangle facilities inside a shared account |
| Ops overhead | N secrets, N onboarding flows, N token caches | Simplest ops — the only real advantage |

If the client chooses the single-account model, it is simply *one* `gate_provider_configs` row owned by the platform organization with facility mappings across orgs pointing at it — no schema change. Token caching, webhook routing, and the provider factory are keyed by `providerConfigId` either way.

### 5.3 Who does what (roles & permissions)

| Actor | Responsibility | Permission (§17) |
|---|---|---|
| **Org Admin / Org Owner** | Creates the provider config: submits vendor credentials once through a **write-only form** (backend stores them in AWS Secrets Manager; only `secret_ref` persists in the DB; values are never readable back through any API). Runs facility discovery, links properties, triggers unit sync | `gates.configuration.manage` |
| **Super Admin (Bear staff)** | Cross-org visibility and troubleshooting (tenant-filter bypass); assists onboarding; not the routine owner — the org owns its vendor relationship | `gates.configuration.manage` + tenant bypass |
| **Property staff (manager/staff)** | Day-to-day operations: view credentials, provision/suspend/reinstate/revoke, regenerate codes, view events, retry dead-lettered operations | `gates.access.view` / `gates.access.manage`, `gates.events.view` |
| **Tenant (renter)** | Sees **their own** gate code in the Client Portal, subject to the withholding rules (§16) | Client portal policy + ownership check |

### 5.4 Onboarding flow (per property)

```mermaid
sequenceDiagram
    autonumber
    actor Admin as Org Admin
    participant API as Bear API
    participant H as Handlers (Wolverine)
    participant SM as AWS Secrets Manager
    participant DB as Bear DB
    participant OT as OpenTech Access Control API

    Admin->>API: 1. Create provider config (credentials, write-only)
    API->>H: ConfigureGateProviderCommand
    H->>SM: Store credentials as new secret
    SM-->>H: secret ARN
    H->>DB: INSERT gate_provider_configs (secret_ref only)
    H->>OT: VerifyCredentialsAsync (GET /facilities)
    OT-->>H: 200 + facility list
    H-->>Admin: Config active + discovered facilities

    Admin->>API: 2. Link property ↔ facility
    API->>H: LinkGateFacilityCommand(propertyId, externalFacilityId)
    Note over H: Auto-suggest match by propertyNumber ↔ Property.Code,<br/>human confirms — never auto-link silently
    H->>DB: INSERT gate_facility_mappings (status=linked)

    Admin->>API: 3. Sync units
    API->>H: SyncGateUnitsCommand(propertyId)
    H->>OT: GET /facilities/{fid}/units
    OT-->>H: existing vendor units
    H->>DB: match by unitNumber → INSERT gate_unit_mappings (synced)
    H->>OT: POST /facilities/{fid}/units (for Bear units missing vendor-side)
    OT-->>H: created unit ids
    H->>DB: INSERT mappings, report unmatched both directions

    Admin->>API: 4. Backfill events + subscribe webhooks
    H->>OT: GET {eventUrl}/events/facilities/{fid} (paged, initial load)
    H->>DB: INSERT gate_events ON CONFLICT DO NOTHING
    Note over Admin,OT: SNS webhook subscription established per account<br/>(provisioning process = vendor question V-1, §22)
```

Facility linking is **suggest-then-confirm**: the backend proposes matches (`propertyNumber` ↔ `Property.Code`, name similarity) but a human always confirms — a wrong link would point gate mutations at the wrong physical property (see Security Notes: validate ids before mutating calls).

### 5.5 Switching providers later (OpenTech → PTI/other)

Because of the port/adapter split (§6) and canonical models, a provider switch per property is an operational playbook, not a rewrite:

1. Implement the new adapter (`provider_type = 'pti'` etc.) against `IGateAccessProvider` + a webhook processor if the vendor pushes events.
2. Create the new provider config (new credentials/secret) for the org.
3. Per property: create the new facility mapping in `disabled` status alongside the old, sync units to the new provider.
4. **Re-provision credentials**: for every active `gate_access_credentials` row, decrypt the stored code and create the visitor on the new provider (codes are recoverable precisely because of D5 — a hashed-code design would force issuing every tenant a new code at cutover).
5. Cut over: flip the facility mapping (old → `disabled`, new → `linked`) inside one transaction; enqueue revocations against the old provider.
6. Keep old `gate_events` (they carry `provider_config_id`); decommission the old config once its operations drain.

The main constraint: **one active provider per property at a time** (the `UNIQUE (property_id)` on active mappings). Steps 3–5 therefore stage the new mapping as `disabled` until cutover.

---

## 6. Provider Abstraction (Anti-Corruption Layer)

### 6.1 Component view

```mermaid
flowchart TB
    subgraph Api["BearMGMT.Api"]
        EP[Gate endpoints<br/>/api/v1/admin/gates/*<br/>/api/v1/client/gates/*]
        WH[Webhook endpoint<br/>/api/v1/webhooks/gate/opentech/:providerConfigId<br/>AllowAnonymous + verification]
    end
    subgraph App["BearMGMT.Application"]
        CMD[Commands & Queries<br/>vertical slices]
        PORT[IGateAccessProvider port<br/>IGateProviderFactory<br/>IGateWebhookProcessor<br/>IGateCodeCipher]
        REPO[IGate*Repository interfaces]
    end
    subgraph Infra["BearMGMT.Infrastructure"]
        OTA[OpenTechGateAccessProvider<br/>adapter]
        TOK[GateTokenProvider<br/>per-config token cache]
        SNSP[OpenTechSnsWebhookProcessor<br/>SNS verify + normalize]
        SEC[SecretsManager resolver<br/>+ IMemoryCache]
        KMS[KMS envelope cipher]
        EFR[EF repositories]
    end
    subgraph Worker["BearMGMT.Worker"]
        DISP[GateOperationDispatcher<br/>30s poll, SKIP LOCKED]
        BKF[GateEventBackfillJob<br/>nightly polling]
        SWEEP[GateEventProcessingSweep]
    end
    subgraph AWS["AWS"]
        SM[(Secrets Manager)]
        KMSK[(KMS CMK)]
        DB[(Aurora PostgreSQL)]
    end
    subgraph Vendor["OpenTech IoE"]
        AUTH[Auth API]
        ACAPI[Access Control API]
        EVAPI[Access Event API]
        SNS[AWS SNS<br/>IOE webhooks]
    end

    EP --> CMD
    WH --> CMD
    CMD --> PORT
    CMD --> REPO
    PORT -. implemented by .-> OTA
    PORT -. implemented by .-> SNSP
    REPO -. implemented by .-> EFR
    OTA --> TOK
    TOK --> AUTH
    OTA --> ACAPI
    OTA --> EVAPI
    SEC --> SM
    KMS --> KMSK
    OTA --> SEC
    EFR --> DB
    DISP --> PORT
    BKF --> PORT
    SNS --> WH
    DISP --> DB
    BKF --> DB
    SWEEP --> DB
```

Clean Architecture holds: Api and Worker depend on Application; Infrastructure implements Application interfaces; Domain stays dependency-free. No endpoint or handler ever references a vendor SDK/DTO (mandatory adapter rule).

### 6.2 The port: `IGateAccessProvider`

Defined in `BearMGMT.Application/Gates/`, canonical records only:

```csharp
public interface IGateAccessProvider
{
    GateProviderCapabilities Capabilities { get; }

    Task<IReadOnlyList<GateFacility>> ListFacilitiesAsync(GateProviderContext ctx, CancellationToken ct);
    Task<GateFacility?> GetFacilityByPropertyNumberAsync(GateProviderContext ctx, string propertyNumber, CancellationToken ct);
    Task<IReadOnlyList<GateUnit>> ListUnitsAsync(GateProviderContext ctx, string externalFacilityId, CancellationToken ct);
    Task<GateUnitRef> EnsureUnitAsync(GateProviderContext ctx, string externalFacilityId, GateUnitSpec spec, CancellationToken ct);

    Task<GateVisitorRef> CreateVisitorAsync(GateProviderContext ctx, CreateGateVisitorRequest request, CancellationToken ct);
    Task<IReadOnlyList<GateVisitor>> ListUnitVisitorsAsync(GateProviderContext ctx, string externalFacilityId, string externalUnitId, CancellationToken ct); // reconcile-before-retry (§9.3)
    Task UpdateVisitorAccessCodeAsync(GateProviderContext ctx, GateVisitorRef visitor, string newAccessCode, CancellationToken ct); // capability-gated (V-2)
    Task SuspendVisitorAsync(GateProviderContext ctx, GateVisitorRef visitor, CancellationToken ct);   // OpenTech: /disable
    Task ReinstateVisitorAsync(GateProviderContext ctx, GateVisitorRef visitor, CancellationToken ct); // OpenTech: /enable
    Task RevokeAsync(GateProviderContext ctx, GateRevocationRequest request, CancellationToken ct);    // Mode: UnitVacate | VisitorRemove

    Task<GateEventPage> GetEventsAsync(GateProviderContext ctx, string externalFacilityId, DateTimeOffset since, DateTimeOffset until, int offset, int limit, CancellationToken ct);
    Task VerifyCredentialsAsync(GateProviderContext ctx, CancellationToken ct); // config-time smoke test
}

public sealed record GateProviderCapabilities(
    bool SupportsUnitVacate,          // OpenTech: true
    bool SupportsVisitorRemove,       // OpenTech: true (tenant-remove caveat V-4)
    bool SupportsInPlaceCodeUpdate,   // OpenTech: pending vendor confirmation (V-2)
    bool SupportsWebhooks,            // OpenTech: true (SNS)
    int MinCodeLength, int MaxCodeLength); // OpenTech: 1..10 (Bear enforces ≥ 4)

public sealed record GateProviderContext(Guid OrganizationId, Guid ProviderConfigId, GateProviderSettings Settings);
```

Capability flags let handlers branch without knowing the vendor: e.g., `RevokeAsync` prefers `UnitVacate` when supported (the spec-documented move-out path) and code regeneration falls back to remove+recreate when `SupportsInPlaceCodeUpdate` is false (which changes `external_visitor_id` — the handler must persist the new ref).

### 6.3 The factory: per-config resolution

```csharp
public interface IGateProviderFactory
{
    Task<(IGateAccessProvider Provider, GateProviderContext Context)> CreateAsync(Guid providerConfigId, CancellationToken ct);
}
```

Resolution chain:

1. Load the `gate_provider_configs` row (tenant-filtered).
2. `provider_type` discriminator → adapter via **keyed DI** (`[FromKeyedServices("open_tech")]` registration).
3. Resolve credentials from AWS Secrets Manager by `secret_ref`; cache in `IMemoryCache` (~5 min TTL). On a 401-after-refresh from the vendor, **evict** the entry — this is how credential rotation propagates without restarts.
4. Build `GateProviderContext` with merged settings (`gate_provider_configs.settings` jsonb over defaults).

Token caching lives in a singleton `GateTokenProvider` keyed by `providerConfigId` (§4.2). The adapter itself is stateless; three named `HttpClient`s (`gate-auth`, `gate-control`, `gate-event`) are registered with `CorrelationIdDelegatingHandler` and inherit the global `AddStandardResilienceHandler()` — no hand-rolled retry loops.

Errors: every vendor failure surfaces as `ExternalServiceException` (→ HTTP 502 via `GlobalExceptionMiddleware`), with response bodies sanitized/truncated before logging (same discipline as `Auth0ErrorBodySanitizer`).

### 6.4 Webhook normalization

Inbound event handling is also vendor-specific (SNS envelopes, signature verification, `SubscriptionConfirmation` handshake), so it sits behind its own port:

```csharp
public interface IGateWebhookProcessor
{
    string ProviderType { get; }  // routes /webhooks/gate/{providerType}/…
    Task<GateWebhookResult> ProcessAsync(Guid providerConfigId, string rawBody, IHeaderDictionary headers, CancellationToken ct);
}
```

`OpenTechSnsWebhookProcessor` (Infrastructure) performs the full SNS pipeline proven in the `IoEWebhook` POC (§14) and emits canonical `GateEvent` rows. The Application layer never sees an SNS envelope.

---

## 7. Database Design

All tables follow Bear conventions: `uuid` PKs defaulting to `gen_random_uuid()`, snake_case names (⚠ remember the EF quirk: `UseSnakeCaseNamingConvention()` does **not** insert an underscore before digits), `AuditableEntity` columns (`created_at`, `created_by`, `updated_at`, `updated_by`, `deleted_at`) with the `auth_bump_updated_at` trigger, `IHasTenant` global query filters on every table (all carry `organization_id`), enums stored as snake_case strings with CHECK constraints derived from the C# enum, and seed/permission data in separate migrations. `gate_events` is append-only like `audit_logs` (block trigger, no `updated_at`).

### 7.1 Entity-relationship overview

```mermaid
erDiagram
    organizations ||--o{ gate_provider_configs : "1..N configs"
    gate_provider_configs ||--o{ gate_facility_mappings : owns
    properties ||--o| gate_facility_mappings : "1:1"
    gate_facility_mappings ||--o{ gate_unit_mappings : contains
    units ||--o| gate_unit_mappings : "1:1"
    gate_facility_mappings ||--o{ gate_access_credentials : scopes
    units ||--o{ gate_access_credentials : "per occupancy"
    users ||--o{ gate_access_credentials : "tenant_user_id"
    gate_encryption_keys ||--o{ gate_access_credentials : "dek_id"
    gate_provider_configs ||--o{ gate_events : receives
    gate_access_credentials ||--o{ gate_events : "resolved to"
    gate_access_credentials ||--o{ gate_operations : "acts on"
    gate_unit_mappings ||--o{ gate_operations : "acts on"

    gate_provider_configs {
        uuid id PK
        uuid organization_id FK
        text provider_type "CHECK open_tech"
        text display_name
        text environment "sandbox|production"
        text secret_ref "Secrets Manager ARN only"
        text webhook_token
        jsonb settings
        boolean is_enabled
    }
    gate_facility_mappings {
        uuid id PK
        uuid organization_id FK
        uuid provider_config_id FK
        uuid property_id FK "UNIQUE"
        text external_facility_id
        text external_property_number
        text status "linked|sync_error|disabled"
        timestamptz last_synced_at
        timestamptz last_event_sync_at
        text last_error
    }
    gate_unit_mappings {
        uuid id PK
        uuid organization_id FK
        uuid facility_mapping_id FK
        uuid unit_id FK "UNIQUE"
        text external_unit_id
        text external_unit_number
        text sync_status "pending|synced|error"
        text last_error
        timestamptz last_synced_at
    }
    gate_access_credentials {
        uuid id PK
        uuid organization_id FK
        uuid facility_mapping_id FK
        uuid unit_id FK
        uuid tenant_user_id FK
        uuid lease_id "nullable, reserved for Lease module"
        text external_visitor_id "vendor-assigned"
        bytea access_code_ciphertext "AES-256-GCM"
        bytea access_code_nonce
        uuid dek_id FK
        bytea access_code_hmac "uniqueness index"
        text status "state machine"
        boolean is_gate_restricted
        timestamptz activated_at
        timestamptz revoked_at
        text revoke_reason
        timestamptz last_access_at
    }
    gate_encryption_keys {
        uuid id PK
        uuid organization_id FK
        bytea wrapped_dek
        text kms_key_arn
        timestamptz retired_at
    }
    gate_events {
        uuid id PK
        uuid organization_id FK
        uuid provider_config_id FK
        uuid facility_mapping_id "nullable"
        uuid credential_id "nullable"
        text external_event_id "dedup key"
        int event_type_raw
        text event_type "canonical enum"
        timestamptz occurred_at
        text external_unit_id
        text external_visitor_id
        text device_name
        text code_entered_masked
        jsonb raw_payload
        text source "webhook|poll"
        text processing_status "pending|processed|skipped|error"
        timestamptz received_at
        timestamptz processed_at
    }
    gate_operations {
        uuid id PK
        uuid organization_id FK
        text operation_type
        uuid credential_id "nullable"
        uuid unit_mapping_id "nullable"
        uuid facility_mapping_id "nullable"
        jsonb payload "canonical request snapshot"
        text trigger_source
        text status "state machine"
        timestamptz scheduled_for
        text idempotency_key "UNIQUE"
        int attempt_count
        timestamptz next_attempt_at
        text last_error
        text correlation_id
        timestamptz completed_at
    }
```

### 7.2 `gate_provider_configs` — vendor account credentials scope

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` PK | `gen_random_uuid()` |
| `organization_id` | `uuid` FK → organizations | `IHasTenant`; global query filter |
| `provider_type` | `text` | `CHECK (provider_type IN ('open_tech'))` — derived from the `GateProviderType` enum; extend for future providers |
| `display_name` | `text` | `UNIQUE (organization_id, display_name) WHERE deleted_at IS NULL` |
| `environment` | `text` | `CHECK (environment IN ('sandbox','production'))` |
| `secret_ref` | `text` | **AWS Secrets Manager secret name/ARN only. Credentials are never stored in this database.** Secret JSON: `{ authBaseUrl, controlBaseUrl, eventBaseUrl, clientId, clientSecret, apiKey, apiSecret }` |
| `webhook_token` | `text` | Random 256-bit token embedded in the webhook URL — defense-in-depth alongside SNS signature verification (§14) |
| `allowed_topic_arns` | `text[]` | SNS topic allow-list (proven pattern from the `IoEWebhook` POC) |
| `settings` | `jsonb` | Provider knobs: `codeLength` (default 6), `defaultTimeGroupId`/`defaultAccessProfileId` (default: omit — §4.3), `revocationMode` override, blocked code patterns |
| `is_enabled` | `boolean` | Disable = stop dispatching operations + reject webhooks for this config |
| audit columns | | `AuditableEntity` + soft delete |

**Why organization-level (not property-level):** credentials belong to the commercial relationship (org ↔ vendor account), not to a building. Property-level would multiply secrets and token caches without isolating anything the facility mapping doesn't already isolate. Allowing **N configs per org** covers the acquisition case (one org, two vendor accounts) and the platform-owned single-account case (C-1) with zero schema change.

### 7.3 Mapping tables

**`gate_facility_mappings`**

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | | standard |
| `provider_config_id` | `uuid` FK | the account this facility lives under |
| `property_id` | `uuid` FK → properties | **`UNIQUE WHERE deleted_at IS NULL`** — one gate system per property (D-note §5.1) |
| `external_facility_id` | `text` | vendor `facilityId` (stored as text — provider-agnostic; OpenTech's is numeric) |
| `external_property_number` | `text` | vendor `propertyNumber`; onboarding match hint |
| `status` | `text` | `CHECK (status IN ('linked','sync_error','disabled'))` |
| `last_synced_at`, `last_event_sync_at`, `last_error` | | `last_event_sync_at` = polling backfill cursor (§14.4) |

Constraints/indexes: `UNIQUE (provider_config_id, external_facility_id)`; index `(organization_id, status)`.

**`gate_unit_mappings`**

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | | standard |
| `facility_mapping_id` | `uuid` FK | |
| `unit_id` | `uuid` FK → units | **`UNIQUE WHERE deleted_at IS NULL`** |
| `external_unit_id` | `text` | vendor `unitId` — required by visitor create |
| `external_unit_number` | `text` | vendor `unitNumber`; import matching key |
| `sync_status` | `text` | `CHECK (sync_status IN ('pending','synced','error'))` |
| `last_error`, `last_synced_at` | | |

Constraints/indexes: `UNIQUE (facility_mapping_id, external_unit_id)`; index `(facility_mapping_id, sync_status)` for reconciliation sweeps.

### 7.4 `gate_access_credentials` — the core aggregate

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | | standard |
| `facility_mapping_id` | `uuid` FK | |
| `unit_id` | `uuid` FK → units | the unit this code opens |
| `tenant_user_id` | `uuid` FK → users | the renter (Auth user with role `tenant` today) |
| `lease_id` | `uuid` **nullable, no FK yet** | **Reserved column** — deliberate schema debt. Binds to the Lease module in Phase 2 (FK added then); far cheaper than re-keying this table's semantics later |
| `external_visitor_id` | `text` nullable | Vendor-assigned; captured from the create response. `CHECK (status <> 'active' OR external_visitor_id IS NOT NULL)` |
| `access_code_ciphertext` | `bytea` | AES-256-GCM ciphertext (§8) |
| `access_code_nonce` | `bytea` | 96-bit GCM nonce, unique per encryption |
| `dek_id` | `uuid` FK → gate_encryption_keys | enables key rotation |
| `access_code_hmac` | `bytea` | HMAC-SHA256 of the code under a **separate** key — exists solely because ciphertext is non-deterministic and cannot be unique-indexed |
| `status` | `text` | `CHECK (status IN ('pending_activation','active','suspended','revoke_scheduled','revoke_pending','revoked','failed'))` |
| `is_gate_restricted` | `boolean` | The gate-side flag of the 2×2 matrix (§16). Portal restriction lives with the portal/user state — deliberately **not** on this table |
| `activated_at`, `revoked_at`, `revoke_reason` | | |
| `last_access_at` | `timestamptz` | maintained from the event stream — ops/UX signal ("last gate entry") |
| audit columns | | standard; `token`-class fields excluded from audit snapshots via `[AuditIgnore]`-equivalent (§8.5) |

**The uniqueness constraint that enforces TM-009's "unique gate code":**

```sql
CREATE UNIQUE INDEX ux_gate_credentials_code_per_facility
    ON gate_access_credentials (facility_mapping_id, access_code_hmac)
    WHERE status IN ('pending_activation','active','suspended','revoke_scheduled','revoke_pending')
      AND deleted_at IS NULL;
```

A revoked credential releases its code for reuse (partial index), while two *live* credentials can never share a code at one facility — a keypad identifies the person by code alone, so this is a hard functional requirement enforced by the database, not by best-effort application checks.

Other indexes: `(tenant_user_id)`, `(unit_id) WHERE status IN (…live…)`, `(facility_mapping_id, status)`.

**Credential state machine:**

```mermaid
stateDiagram-v2
    [*] --> pending_activation : ProvisionGateAccess (code generated,\nintent committed locally)
    pending_activation --> active : vendor create OK\n(external_visitor_id captured)
    pending_activation --> failed : retries exhausted (dead-letter)
    failed --> pending_activation : staff retry
    active --> suspended : SuspendGateAccess\n(delinquency / manual)
    suspended --> active : ReinstateGateAccess
    active --> revoke_scheduled : ScheduleGateRevocation\n(transfer, future-dated)
    revoke_scheduled --> active : schedule cancelled
    revoke_scheduled --> revoke_pending : scheduled_for reached
    active --> revoke_pending : RevokeGateAccess (move-out)
    suspended --> revoke_pending : RevokeGateAccess
    revoke_pending --> revoked : vendor revocation confirmed
    revoke_pending --> revoke_pending : retry with backoff\n(fail-open SLA alert §11.3)
    revoked --> [*]
```

### 7.5 `gate_encryption_keys` — KMS envelope

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | | one **active** DEK per org (partial unique on `organization_id WHERE retired_at IS NULL`) |
| `wrapped_dek` | `bytea` | data key wrapped by the KMS CMK (`GenerateDataKey`) |
| `kms_key_arn` | `text` | which CMK wrapped it |
| `created_at`, `retired_at` | | rotation: new rows use the new DEK; old rows decrypt with their own `dek_id`; lazy re-encrypt on read is optional |

### 7.6 `gate_events` — append-only ingestion

Append-only exactly like `audit_logs`: no `updated_at`/`deleted_at` for the payload columns, plus a block trigger raising on UPDATE of immutable columns / any DELETE (only `processing_status`/`processed_at`/`credential_id`/`facility_mapping_id` are updatable — enforced by a column-restricted trigger).

Key design points:

- **Dedup:** `UNIQUE (provider_config_id, external_event_id)`; all ingestion (webhook *and* polling) is `INSERT … ON CONFLICT DO NOTHING` — the same event arriving from both paths lands once.
- `event_type` canonical values: `access_granted`, `access_denied_invalid_code`, `access_denied_invalid_area`, `access_denied_delinquent`, `access_denied_invalid_time`, `access_denied_loitering`, `door_alarm`, `unit_door_opened`, `unit_door_closed`, `device_offline`, `device_online`, `facility_opened`, `facility_closed`, `unknown` — mapped in the adapter from vendor enums (18, 15, 16, 17, 19, 24, 10, 33, 34, 11, 12, 8, 9, else). `unknown` preserves forward compatibility; `raw_payload` (jsonb) always keeps the full vendor event.
- `code_entered_masked`: vendor events may carry `codeEntered`; Bear stores it **masked** (`****34`) — never the full entered code (§8.5).
- Indexes: **BRIN on `occurred_at`** (large, naturally time-ordered table); btree `(facility_mapping_id, occurred_at DESC)` for the events screen; partial `(processing_status) WHERE processing_status = 'pending'` for the sweep.
- **Growth plan:** monthly range partitioning by `occurred_at` is a stated Phase 3 step, not MVP — but no query may assume a single physical table (always filter by facility + time window; list endpoints paginate via `PagedQuery`).

### 7.7 `gate_operations` — merged outbox + scheduled-action store

One table serves both roles deliberately: a scheduled revocation is just an operation whose `scheduled_for` is in the future and which remains **cancellable and re-datable** until picked up. Splitting outbox and scheduler would duplicate the entire execution machinery (dispatcher, retry, dead-letter, dashboard) for no benefit.

| Column | Type | Notes |
|---|---|---|
| `id`, `organization_id` | | standard |
| `operation_type` | `text` | `CHECK IN ('create_visitor','update_visitor_code','suspend_visitor','reinstate_visitor','remove_visitor','vacate_unit','create_unit','sync_units','backfill_events')` |
| `credential_id` / `unit_mapping_id` / `facility_mapping_id` | `uuid` nullable FKs | whichever applies |
| `payload` | `jsonb` | canonical request snapshot (e.g., the `CreateGateVisitorRequest` minus the plaintext code — the executor decrypts from the credential row at send time; codes never sit in `payload`) |
| `trigger_source` | `text` | `CHECK IN ('move_in','move_out','transfer_scheduled','delinquency','manual','reconciliation','onboarding')` |
| `status` | `text` | `CHECK IN ('scheduled','pending','in_progress','succeeded','failed','dead','cancelled')` |
| `scheduled_for` | `timestamptz` | `now()` for immediate ops; future for transfer revocations — computed in the **property's timezone** (§11.2) |
| `idempotency_key` | `text` `UNIQUE` | e.g. `revoke:{credentialId}:{moveOutDate}` — a re-submitted command lands on the existing row instead of duplicating |
| `attempt_count` | `int` | |
| `next_attempt_at` | `timestamptz` | retry schedule (§13.2) |
| `last_error` | `text` | sanitized |
| `correlation_id` | `text` | end-to-end trace from originating request to vendor call |
| `completed_at` | `timestamptz` | |

Indexes (dispatcher hot paths): partial `(next_attempt_at) WHERE status IN ('pending','failed')`; partial `(scheduled_for) WHERE status = 'scheduled'`; `(credential_id, status)`; `(organization_id, status)` for the ops dashboard.

**Operation state machine:**

```mermaid
stateDiagram-v2
    [*] --> scheduled : future-dated (transfer)
    [*] --> pending : immediate
    scheduled --> pending : scheduled_for reached
    scheduled --> cancelled : transfer cancelled / re-dated
    pending --> in_progress : dispatcher claims (SKIP LOCKED)
    in_progress --> succeeded : vendor call OK (or 409 = already done)
    in_progress --> failed : transient failure\n(next_attempt_at set, backoff)
    failed --> in_progress : dispatcher retry
    failed --> dead : max attempts / non-retryable 4xx
    dead --> pending : staff manual retry
    succeeded --> [*]
    cancelled --> [*]
```

### 7.8 Future sketch (Phase 3): delinquency escalation tables

Documented now so the Payment module builds against a known shape; **not** created in Phase 1:

- `gate_escalation_profiles` — `id`, `organization_id`, `property_id` (nullable = org default), `name`, `is_enabled`.
- `gate_escalation_steps` — `id`, `profile_id`, `overdue_days_threshold` (unique per profile), `action` `CHECK IN ('notification','document_generation','gate_suspend','gate_reinstate','overlock_task')`, `action_settings` jsonb, `is_lien_trigger` (last step). Sorted by day offset; unique per step; last step = lien trigger, per PM-007.

### 7.9 Migration checklist for this module

Standard Bear rules apply: `dotnet ef migrations add` with Designer files; `auth_bump_updated_at` trigger on every new table except `gate_events`; append-only block trigger on `gate_events`; `Down()` drops triggers → children → parents; CHECK constraints derived from C# enums (never raw literals); permission seeds (`gates.*`) in a **separate** migration; verify snake_case names against the digit quirk (`…_v2`-style names are safe; no digit-adjacent columns exist in this schema).

---

## 8. Gate Code Lifecycle & Security

### 8.1 Why the code must be encrypted — and cannot be hashed

The vendor **never returns the access code on any read** (`accessCode` is write-only; reads return `code: null` for tenants). Bear's database is therefore the *only* recoverable copy, and TP-004 requires displaying it to the tenant. A hash is one-way → hashing is functionally impossible here. The code is a live physical-security credential, so plaintext storage is equally unacceptable. Encryption-at-rest with controlled reveal is the only design that satisfies both constraints.

### 8.2 Encryption: application-level AES-256-GCM with KMS envelope — not pgcrypto

| Option | Verdict | Why |
|---|---|---|
| `pgcrypto` (`pgp_sym_encrypt`) | **Rejected** | The key travels inside SQL statement text — it leaks into Postgres logs, `pg_stat_statements`, and RDS Performance Insights; rotation and enveloping are clumsy |
| App-level AES-256-GCM + KMS envelope | **Chosen** | Key never touches the DB tier; standard AWS rotation story; per-org key isolation |
| Hash only | Impossible | Must display to tenant (§8.1) |

Mechanics:

1. One **KMS CMK** for the gate module (per environment).
2. Per organization, a **DEK** is created via `GenerateDataKey`; the *wrapped* DEK is stored in `gate_encryption_keys`; the plaintext DEK is cached in process memory only.
3. Each code is encrypted AES-256-GCM with a fresh 96-bit nonce; `(ciphertext, nonce, dek_id)` land on the credential row.
4. Rotation: mint a new DEK (old row gets `retired_at`); new credentials use it; old rows still decrypt via their own `dek_id`; lazy re-encrypt on read is optional.
5. A **separate HMAC-SHA256 key** (Secrets Manager, never the encryption key) produces `access_code_hmac` for the uniqueness index and for equality lookups (e.g., "which credential does entered code X belong to") without decryption.

The Application layer sees only an `IGateCodeCipher` port (`Encrypt`, `Decrypt`, `ComputeLookupHmac`); KMS specifics stay in Infrastructure.

### 8.3 Generation algorithm

- **Source:** `RandomNumberGenerator` (CSPRNG) — never `Random`.
- **Length:** 6 digits default; configurable 4–10 per provider config (vendor accepts 1–10; Bear never allows < 4). Stored as a digit *string* — leading zeros are legal and significant.
- **Rejection filters:** all-same digits (`444444`), full ascending/descending runs (`123456`, `987654`), code == unit number, org-configurable blocklist.
- **Uniqueness scope: the facility** (§7.4's partial unique index). Generate → HMAC → attempt insert; on unique violation, regenerate. Cap at **10 attempts**, then fail loudly (TM-009's failure-message path). Capacity math: at 6 digits (10⁶ space) with ~1,000 live codes per facility, per-attempt collision probability ≈ 0.1% — ten consecutive collisions is ~10⁻³⁰; if it ever happens, something is systematically wrong and failing loudly is correct.

### 8.4 Regeneration & reveal

- **Regenerate** (staff- or tenant-initiated): generate a new code → update ciphertext/HMAC in the local transaction → enqueue `update_visitor_code`. If the provider lacks `SupportsInPlaceCodeUpdate` (vendor question V-2), the executor falls back to **remove + recreate visitor**, which yields a *new* `external_visitor_id` that must be persisted. The old code stops working at the vendor as soon as the operation succeeds.
- **Reveal** (showing the plaintext to staff or tenant) is itself an **audited action**: `gate_code.revealed` with actor, credential id, and portal scope — never the value.

### 8.5 Non-negotiable handling rules

| Rule | Enforcement |
|---|---|
| Codes never appear in logs | No log statement may interpolate the code; structured-log review + code review gate; token request bodies excluded from HTTP logging (§4.2) |
| Codes never appear in audit `before/after` JSON | Credential audit snapshots exclude `access_code_*` fields (same `[AuditIgnore]` discipline as invitation `token_hash`) |
| Codes never sit in `gate_operations.payload` | Executor decrypts from the credential row at send time (§7.7) |
| `codeEntered` from vendor events stored masked | `****34` in `gate_events.code_entered_masked`; full value only inside `raw_payload` jsonb, which is access-restricted like other tenant PII |
| Only two display surfaces | Admin credential detail (staff, `gates.access.view`) and tenant Unit Detail (§16); both server-side gated |
| No codes in URLs | Codes travel only in request/response bodies over TLS |

---

## 9. Consistency Model — What "Atomic" Really Means

### 9.1 The honest contract

MO-003 says move-out actions "execute atomically as a single transaction." An HTTP call to OpenTech **cannot join a Postgres transaction** — as literally stated, that requirement is unbuildable. The contract this architecture implements, and which the client must sign off on (§22 C-4):

> **All local effects commit atomically** (one EF Core transaction: unit status, autopay flag, work order, credential state, outbox row). **The external gate mutation is guaranteed-eventual**: retried with backoff, visible in a pending state, dead-lettered with a staff alert if it cannot complete within the SLA.

### 9.2 The universal ordering rule: DB intent first, vendor call second

Never the reverse. If Bear called the vendor first and crashed before committing, a live gate code would exist with **no Bear record** — an unauditable security hole with no retry path and nothing to reconcile against. With intent-first, the worst case is a `pending` row that retries or dead-letters *visibly*. This asymmetry decides the ordering for every operation in this module.

Corollary: the vendor call happens **after** `SaveChangesAsync` (same pattern as Bear's domain-event dispatch rule), either inline (move-in's fast path) or from the Worker dispatcher.

### 9.3 Idempotency

| Layer | Mechanism |
|---|---|
| Command → operation | `gate_operations.idempotency_key` (UNIQUE) — re-submitting a command lands on the existing row |
| Vendor `409 Conflict` | Treated as success (already in requested state) |
| suspend/reinstate/remove/vacate | Naturally idempotent vendor operations — safe to retry blindly |
| **create_visitor — the hard case** | The vendor accepts **no client-supplied key**, so a retry after an ambiguous timeout could double-create. **Reconcile-before-retry is mandatory**: before any `create_visitor` retry, the executor calls `ListUnitVisitorsAsync` for the target unit and matches on name + enabled state; if a matching visitor exists, it **adopts** that `visitor.id` and marks the operation succeeded instead of re-creating |
| Event ingestion | `ON CONFLICT (provider_config_id, external_event_id) DO NOTHING` (§7.6) |

### 9.4 Why an outbox, not a saga

Gate revocation and suspension are **one-way, idempotent** operations — there is nothing to compensate. A saga with compensating transactions would be over-engineering; a pending-state outbox with retry is strictly simpler and sufficient. The only "compensation-like" behavior in the module is failed *provisioning* (move-in), where the credential goes to `failed` and staff either retry or release the code.

---

## 10. Flow: Tenant Move-In (TM-009)

**Pattern: synchronous attempt, asynchronous guarantee.** The tenant at the counter should see their gate code immediately (happy path: one vendor round-trip, sub-second), but the business flow must not fail if the vendor is slow or down.

1. **Local transaction (Wolverine EF Core transaction):** validate unit mapping exists (else fail with a clear onboarding message) → generate code (§8.3) → insert `gate_access_credentials` (`pending_activation`) → insert `gate_operations` (`create_visitor`, `pending`) → audit record. Commit.
2. **Inline execution (post-commit, ~5s budget):** the same request executes the operation: `CreateVisitorAsync` → capture `external_visitor_id` (mandatory — only correlation key) → credential → `active`, operation → `succeeded`.
3. **Success response:** gate code shown on-screen; `GateAccessProvisioned` notification event raised → Notifications module (Phase 2) emails the move-in confirmation (contracts, unit details, move-in date, next billing date, gate code — but see C-2 §22 on code-in-email risk).
4. **Failure/timeout:** response returns the explicit UX state required by TM-009 — *"Gate code generated; gate activation is in progress"* — and the Worker dispatcher takes over retries. Terminal failure → credential `failed`, staff alert (§19).
5. **Email bounce:** the confirmation email is the tenant's only off-screen channel to the code, so a bounce triggers a **staff alert** — this is a contract on the Notifications module (Phase 2): it must consume delivery-status callbacks (e.g., SES → SNS bounce topic) and raise `GateCodeEmailBounced` → staff task (§21.2).

```mermaid
sequenceDiagram
    autonumber
    actor Staff as Staff (Admin Portal)
    participant API as Bear API
    participant H as ProvisionGateAccessCommandHandler
    participant DB as Bear DB (EF tx)
    participant EX as Operation Executor
    participant TP as GateTokenProvider
    participant OT as OpenTech Access Control API
    participant N as Notifications (Phase 2)

    Staff->>API: Finalize move-in
    API->>H: ProvisionGateAccessCommand (via IMessageBus)
    H->>DB: load unit + facility mapping (validated)
    H->>H: generate code (CSPRNG, filters) + HMAC
    H->>DB: INSERT gate_access_credentials (pending_activation)<br/>INSERT gate_operations (create_visitor, pending)<br/>audit record
    DB-->>H: COMMIT (local intent durable)
    H->>EX: execute inline (5s budget)
    EX->>TP: GetTokenAsync(providerConfigId)
    TP-->>EX: bearer token (cached / refreshed, see §4.2)
    EX->>OT: POST /facilities/{fid}/visitors {isTenant, unitId, accessCode}
    alt Vendor OK
        OT-->>EX: visitor.id (+ unit status Rented)
        EX->>DB: credential → active (external_visitor_id)<br/>operation → succeeded
        H-->>Staff: 200 — code displayed on-screen
        H--)N: GateAccessProvisioned event → confirmation email
        N--)Staff: staff alert if email bounces (TM-009)
    else Vendor slow / 5xx / timeout
        EX->>DB: operation → failed, next_attempt_at set
        H-->>Staff: 200 — "code generated, gate activation in progress"
        Note over EX,OT: Worker dispatcher retries with backoff —<br/>before any create retry: list unit visitors,<br/>adopt existing match (reconcile-before-retry §9.3)
    else Non-retryable 400/403
        EX->>DB: operation → dead, credential → failed
        Note over Staff: staff alert + runbook action (§19)
    end
```

Differences from the original draft sequence diagram: code generation and the credential/outbox insert happen **before** the vendor call (intent-first, D3); the unit is validated as already mapped (unit creation belongs to onboarding §5.4, not move-in); failure paths and the email/bounce leg are explicit; "save visitorId/unitId/accessCode" is a single committed state transition rather than three separate writes after the fact.

---

## 11. Flows: Move-Out & Unit Transfer (MO-003/MO-005, TR-005)

### 11.1 Move-out bundle

All local mutations commit in **one** EF Core transaction — that is the real atomic boundary (§9.1): unit status change, autopay disable (Payment, Phase 3), inspection work order (Maintenance, future), credential → `revoke_pending`, `gate_operations` row. The vendor revocation then executes with retry.

Provider call selection (capability-gated, §6.2): **`vacate_unit` is primary** — it is the spec-documented path for a full tenant move-out *and* the only one that flips the vendor unit status to `Vacant`, keeping the vendor projection consistent (D8). `remove_visitor` is the fallback for partial cases (one of several visitors on a unit — future co-tenant scenario).

```mermaid
sequenceDiagram
    autonumber
    actor Staff as Staff
    participant API as Bear API
    participant H as RevokeGateAccessCommandHandler<br/>(part of move-out bundle)
    participant DB as Bear DB (single EF tx)
    participant W as Worker Dispatcher
    participant OT as OpenTech API

    Staff->>API: Confirm move-out
    API->>H: MoveOut bundle → RevokeGateAccessCommand
    H->>DB: BEGIN — unit status, autopay off, work order,<br/>credential → revoke_pending,<br/>INSERT gate_operations (vacate_unit, pending,<br/>idempotency_key revoke:{credId}:{date})
    DB-->>H: COMMIT — bundle is atomic locally
    H-->>Staff: 200 — move-out confirmed (revocation pending)
    W->>DB: claim operation (FOR UPDATE SKIP LOCKED)
    W->>OT: POST /facilities/{fid}/units/{uid}/vacate (empty body)
    alt OK or 409 (already vacant)
        OT-->>W: unit status = Vacant
        W->>DB: credential → revoked (revoked_at, reason)<br/>operation → succeeded<br/>audit record
    else 5xx / timeout
        W->>DB: attempt_count++, next_attempt_at (backoff)
        Note over W: after 15 min still failing →<br/>staff alert: "tenant may still have gate access"<br/>(fail-open SLA §11.3)
    end
```

Post-revocation state in Bear (mirrors the guide's §5.3 post-call actions): credential keeps its row forever (`revoked` — audit trail; the code ciphertext is retained but the code is released for reuse by the partial unique index), unit mapping stays (the unit will be re-rented), `Unit.Status` was already updated in the bundle.

### 11.2 Unit transfer (TR-005)

Two independent legs, deliberately asymmetric:

- **New unit — immediate:** a standard move-in flow (§10) issues a **new code** for the new unit right away. The tenant occupies the new unit from day one.
- **Old unit — scheduled:** revocation is **not** immediate. A `gate_operations` row is inserted with `status='scheduled'`, `trigger_source='transfer_scheduled'`, and `scheduled_for` = the old unit's move-out date at the configured cut-over time **in the property's timezone** (`Property.Timezone` exists on the entity; exact cut-over time — end of day? gate closing? — is client question C-3). The credential moves to `revoke_scheduled`.
- **Mutability:** if the move-out date changes, update `scheduled_for`; if the transfer is cancelled, the operation → `cancelled` and the credential returns to `active`. This mutability is exactly why the schedule lives in a queryable DB row and not inside a message broker (§13.1).

```mermaid
sequenceDiagram
    autonumber
    actor Staff as Staff
    participant H as TransferGateAccessCommandHandler
    participant DB as Bear DB
    participant W as Worker Dispatcher
    participant OT as OpenTech API

    Staff->>H: Confirm unit transfer (old unit U1 → new unit U2,<br/>old-unit move-out date D)
    H->>DB: BEGIN —<br/>new credential for U2 (pending_activation, new code)<br/>+ create_visitor operation (pending)<br/>old U1 credential → revoke_scheduled<br/>+ vacate_unit operation (scheduled, scheduled_for = D @ property tz)
    DB-->>H: COMMIT
    H->>OT: inline: create visitor on U2 (as §10)
    OT-->>H: visitor.id → U2 credential active
    H-->>Staff: new gate code shown for U2 —<br/>old code remains active until D
    Note over W,DB: …until D (schedule re-datable / cancellable)…
    W->>DB: scheduled_for reached → claim operation
    W->>OT: POST /facilities/{fid}/units/{U1}/vacate
    OT-->>W: Vacant
    W->>DB: U1 credential → revoked, operation → succeeded, audit
```

### 11.3 The fail-open SLA

A stuck revocation means the tenant **retains physical access** until a human intervenes — the module fails *open*, not closed, on this path. That residual risk is the honest price of D6 and must carry an explicit SLA: **staff alert after 15 minutes of failed revocation attempts** (configurable; client question C-4), surfaced on the operations dashboard (§18) and via notification. The runbook action is manual: staff can disable the visitor from the OpenTech operator console directly while Bear keeps retrying.

---

## 12. Delinquency Escalation Contract (PM-007)

The Payment module does not exist yet, so PM-007's escalation *engine* is Phase 3 — but its **integration contract is fixed now** so both modules build toward the same boundary, and the gate-side commands ship in Phase 1 (staff can already suspend/reinstate manually).

### 12.1 Which PM-007 actions belong to which module

| PM-007 step action | Owner | Gate module involvement |
|---|---|---|
| Notification | Notifications module | none |
| Document generation | Documents/DocumentTemplates | none |
| **Gate lock** | Gate module | `SuspendGateAccessCommand` → `suspend_visitor` operation → vendor `/disable`; credential → `suspended`, `is_gate_restricted = true` |
| **Gate unlock** | Gate module | `ReinstateGateAccessCommand` → `reinstate_visitor` → vendor `/enable`; credential → `active`, `is_gate_restricted = false` |
| **Overlock unit** | Maintenance/work-order module | ⚠ **Not a gate API call.** An overlock is a *physical padlock* placed on the unit door by staff — OpenTech's API disables keypad access but cannot padlock a door. PM-007's overlock step produces a **work-order/task**, optionally *combined with* a gate suspend |

### 12.2 The event contract (Payment module → gate module, Phase 3)

The Payment module publishes a Wolverine `INotification` after its own state commits:

```csharp
public sealed record TenantDelinquencyStageChanged(
    Guid OrganizationId,
    Guid TenantUserId,
    Guid UnitId,
    Guid? LeaseId,
    int OverdueDays,
    string Stage,               // entered escalation step key
    bool IsReinstatement) : INotification;
```

The escalation engine (Phase 3) maps profile steps (§7.8) to actions; the gate module's only obligations are the two commands above — which are the same commands staff use manually and the same operations the dispatcher already executes. **Bulk unit-level disable** (vendor `POST /units/{unitId}/disable`, which also flips the vendor unit to `Delinquent`) is available via capability flag if the client wants unit-status signaling at the gate; per-visitor disable is the default because it leaves the vendor unit status under Bear's exclusive control (D8).

Delinquency suspension is **reversible state, not revocation**: the visitor and code survive; only `isEnabled` flips. That is exactly the semantics TP-013 needs for "restricted" display (§16).

---

## 13. Scheduled Execution & Worker Architecture

### 13.1 Why a DB-polling dispatcher — and not Wolverine scheduled messages

`WolverineServiceExtensions.cs` configures pipeline behaviors and EF transactions but **no durable message persistence** (`PersistMessagesWith*` is absent). A Wolverine scheduled message therefore lives in process memory and **dies on deploy/restart**. Losing a security revocation scheduled three weeks out (transfer, §11.2) is disqualifying. Additionally, business schedules must be **queryable, auditable, cancellable, and re-datable** — properties a broker-internal message doesn't have. Hangfire is not in the stack. Hence: `gate_operations` is the durable schedule, and `BearMGMT.Worker` (today a stub — this module gives it its first real job) executes it.

Even if/when Wolverine gains durable Postgres transport (worth adopting later for lower dispatch latency), the *business* schedule stays in the table for the reasons above; Wolverine would only replace the polling wake-up.

### 13.2 `GateOperationDispatcher` (BackgroundService in BearMGMT.Worker)

- **Poll loop:** every ~30s (configurable):

```sql
SELECT * FROM gate_operations
WHERE (status = 'pending'   AND next_attempt_at <= now())
   OR (status = 'scheduled' AND scheduled_for  <= now())
ORDER BY scheduled_for NULLS FIRST, next_attempt_at
FOR UPDATE SKIP LOCKED
LIMIT 20;
```

`FOR UPDATE SKIP LOCKED` makes multiple Worker instances safe with no leader election — each claims disjoint rows.

- **Execution:** claimed rows → `in_progress` → resolve provider via `IGateProviderFactory` (per row's config) → execute the canonical operation → `succeeded` / `failed` per the retry taxonomy (§4.4). Every execution stamps `attempt_count`, `last_error` (sanitized), and audit records for state transitions.
- **Retry schedule** (beyond the in-process resilience handler's 3 quick attempts): `next_attempt_at = now() + min(2^attempt_count minutes, 30 min)`, max **10 dispatcher attempts**, then `dead` + staff alert. Revocation-class operations alert earlier per the fail-open SLA (§11.3).
- **Tenant context:** the Worker runs outside HTTP, so handlers invoked from it execute with a **system actor** (`TenantContext` populated from the operation row's `organization_id`; audit rows record NULL actor + `metadata.system_actor = "gate-dispatcher"` — the established system-actor pattern).
- **Observability:** queue-depth gauge (`pending`+`scheduled` due), oldest-due-age gauge, per-operation-type success/failure counters (§18).

Two further scheduled jobs share the Worker host: **`GateEventBackfillJob`** (nightly polling sweep, §14.4) and **`GateEventProcessingSweep`** (re-processes `gate_events.processing_status='pending'` rows whose in-process notification was lost, §14.3).

---

## 14. Event Ingestion: Webhooks + Polling Backfill

Real-time gate events (access granted/denied, door alarms, device health) arrive via **IOE webhooks — AWS SNS HTTPS subscriptions**. Polling the Access Event API is only for the initial historical load and a nightly backfill safety net. Guiding rule (D7): **events are observational** — they feed dashboards, alerts, and audit; no business state transition depends on webhook delivery, which is what makes the MVP safe without an SQS layer.

### 14.1 Endpoint & verification chain

`POST /api/v1/webhooks/gate/opentech/{providerConfigId}` — the first inbound-webhook surface in Bear:

- `AllowAnonymous` (SNS cannot authenticate with Bear JWTs), **excluded** from `TenantResolutionMiddleware`, dedicated tight rate-limit bucket.
- Reads the **raw body as string** — SNS posts with `Content-Type: text/plain` (POC-proven; model binding would fail).
- Verification chain, in order (all four proven in the `IoEWebhook` POC):
  1. **Per-config `webhook_token`** (query param) matches `gate_provider_configs.webhook_token` — cheap rejection of noise before any parsing.
  2. **`SigningCertURL` host allow-list** — must match `^https://sns\.[a-z0-9\-]+\.amazonaws\.com(\.cn)?/` **before** invoking SDK verification, otherwise an attacker supplies their own "verifying" certificate.
  3. **SNS signature verification** — `Message.IsMessageSignatureValid()` (AWS SDK).
  4. **`TopicArn` allow-list** — must be in `gate_provider_configs.allowed_topic_arns`.
- `SubscriptionConfirmation`: validate `SubscribeURL` against the same SNS host allow-list, then GET it to confirm; persist the subscription ARN on the config; audit the event. `UnsubscribeConfirmation`: acknowledge + staff alert (an unexpected unsubscribe means events stopped flowing).

### 14.2 Notification handling — fast, durable, dumb

`Notification` messages carry a two-layer envelope (`Message` → `{message_type: "GatewayEvent", message_data: "<json-string>"}` → `GatewayEventModel`). The handler: parse → normalize to canonical event → **`INSERT INTO gate_events … ON CONFLICT (provider_config_id, external_event_id) DO NOTHING`** → **return 200 immediately**. No vendor calls, no heavy processing inline — stay well under SNS's delivery timeout. The `gate_events` table *is* the durable queue for the MVP.

```mermaid
sequenceDiagram
    autonumber
    participant GWC as Gate Controller (facility)
    participant SNS as OpenTech IOE (AWS SNS)
    participant WH as Bear Webhook Endpoint<br/>/api/v1/webhooks/gate/opentech/{configId}
    participant DB as Bear DB (gate_events)
    participant P as Async Processor<br/>(Wolverine local + Worker sweep)

    Note over SNS,WH: One-time handshake per subscription
    SNS->>WH: POST SubscriptionConfirmation
    WH->>WH: verify token → cert-host allowlist → signature → topic ARN
    WH->>SNS: GET SubscribeURL (confirm)
    WH->>DB: store subscription ARN + audit
    WH-->>SNS: 200

    Note over GWC,P: Steady state — every keypad/gate interaction
    GWC->>SNS: gateway event
    SNS->>WH: POST Notification (raw text/plain body)
    WH->>WH: verify token → cert-host allowlist → signature → topic ARN
    WH->>WH: parse envelope → normalize (vendor enum → canonical type)
    WH->>DB: INSERT gate_events ON CONFLICT DO NOTHING (processing_status=pending)
    WH-->>SNS: 200 (fast — no vendor calls, no heavy work inline)
    WH--)P: Wolverine local message (low-latency nudge)
    P->>DB: claim pending events (sweep also covers lost nudges)
    P->>DB: resolve credential/unit/facility, set last_access_at,<br/>raise alerts (delinquent-denial, invalid-code bursts),<br/>flag state drift → processing_status=processed
```

Failure posture: any verification failure → 400 (SNS will retry per its policy); parse failures → 400 with sanitized logging; a *processing* failure never blocks ingestion (the row sits in `pending`/`error` for the sweep). Unknown `message_type` values are acknowledged with 200 and logged — not errors.

### 14.3 Asynchronous processing (belt and braces)

Post-insert, the endpoint publishes a lightweight Wolverine local message for low-latency processing, **and** the Worker's `GateEventProcessingSweep` periodically claims `processing_status='pending'` rows — covering nudges lost to restarts (Wolverine local messages are in-memory, §13.1). Processing responsibilities:

- Resolve `credential_id` / `facility_mapping_id` from external ids.
- `access_granted` → update `gate_access_credentials.last_access_at`.
- `access_denied_delinquent` and repeated `access_denied_invalid_code` from one device → staff alert (someone locked out at the physical gate is a support call about to happen).
- **Drift detection:** an `access_granted` for a credential Bear believes is `revoked`/`suspended` is a serious inconsistency → high-priority alert + reconciliation.

### 14.4 Polling backfill & reconciliation

Nightly (and on-demand per facility), `GateEventBackfillJob` pages `GET {eventUrl}/events/facilities/{facilityId}?minDate={last_event_sync_at}&maxDate={now}` with the same conflict-ignoring insert. `204` = empty page. Cursor: `gate_facility_mappings.last_event_sync_at`, advanced only after a fully ingested range. This closes SNS delivery gaps (SNS HTTP retries are finite) and doubles as the initial historical load during onboarding (§5.4).

### 14.5 Target state (Phase 3): SNS → SQS → Worker

Subscribe the SNS topic to an SQS queue and consume from the Worker instead of terminating webhooks in the API. Gains: ingestion decoupled from API deploys/uptime, native DLQ semantics, backpressure. This is also the natural moment to adopt Wolverine's durable transport. The MVP design stays honest without it because of D7 — nothing but observability latency degrades if webhook delivery lags.

### 14.6 Physical access flow (context)

What happens at the keypad is entirely vendor-side; Bear only ever *learns about it* through the event stream — worth keeping in mind when reasoning about latency (a suspend that hasn't reached the gate controller yet will still admit the tenant):

```mermaid
sequenceDiagram
    autonumber
    actor T as Tenant
    participant K as Keypad
    participant C as Gate Controller (GWC)
    participant IOE as OpenTech Cloud / IOE
    participant B as BearMGMT

    T->>K: Enter PIN
    K->>C: Validate PIN
    C->>C: check visitor (isEnabled, isLockedOut)
    C->>C: check access profile + time group
    alt Access granted
        C->>C: activate relay
        C-->>T: gate opens
        C->>IOE: AccessGranted event (enum 18)
        IOE->>B: SNS webhook → gate_events → last_access_at
    else Access denied
        C-->>T: denied (reason: invalid code 15 / wrong area 16 /<br/>delinquent 17 / outside time group 19 / loitering 24)
        C->>IOE: AccessDenied event
        IOE->>B: SNS webhook → gate_events → staff alerting
    end
```

---

## 15. Application Layer Design (Vertical Slices)

The module lives at `src/BearMGMT.Application/Gates/` following the mandatory conventions: one operation folder per slice; command record + handler class in **one** file (`{Verb}{Noun}CommandHandler.cs`); separate validator and result files; all dispatch via `IMessageBus.InvokeAsync`; `[RequiresPermission]` on every command/query; audit + structured logging in every handler; list queries extend `PagedQuery` and return `PagedResult<T>`.

### 15.1 Slice catalog

| Slice folder | Message | Kind | Permission (portal) | Notes |
|---|---|---|---|---|
| `ConfigureGateProvider/` | `ConfigureGateProviderCommand` | cmd | `gates.configuration.manage` (Admin) | write-only credentials → Secrets Manager; `VerifyCredentialsAsync` gates activation |
| `ListGateFacilities/` | `ListGateFacilitiesQuery` | qry | `gates.configuration.view` (Admin) | vendor discovery for the mapping UI |
| `LinkGateFacility/` | `LinkGateFacilityCommand` | cmd | `gates.configuration.manage` (Admin) | suggest-then-confirm link |
| `SyncGateUnits/` | `SyncGateUnitsCommand` | cmd | `gates.configuration.manage` (Admin) | import/create/reconcile unit mappings |
| `ProvisionGateAccess/` | `ProvisionGateAccessCommand` | cmd | `gates.access.manage` (Admin) | move-in (§10); Phase 2: invoked by Lease handler via `IMessageBus` composition |
| `SuspendGateAccess/` | `SuspendGateAccessCommand` | cmd | `gates.access.manage` (Admin) | delinquency lock / manual |
| `ReinstateGateAccess/` | `ReinstateGateAccessCommand` | cmd | `gates.access.manage` (Admin) | unlock |
| `RevokeGateAccess/` | `RevokeGateAccessCommand` | cmd | `gates.access.manage` (Admin) | move-out (§11.1) |
| `ScheduleGateRevocation/` | `ScheduleGateRevocationCommand` | cmd | `gates.access.manage` (Admin) | transfer old-unit leg (§11.2); also `CancelScheduledGateRevocation` |
| `RegenerateGateCode/` | `RegenerateGateCodeCommand` | cmd | `gates.access.manage` (Admin) | §8.4 |
| `GetGateCredential/` | `GetGateCredentialQuery` | qry | `gates.access.view` (Admin) | staff detail incl. reveal (audited) |
| `GetTenantGateCode/` | `GetTenantGateCodeQuery` | qry | Client portal + ownership | **server-side withholding** (§16); 404-not-403 on ownership miss |
| `ListGateEvents/` | `ListGateEventsQuery : PagedQuery` | qry | `gates.events.view` (Admin) | filters: property, unit, event type, date range; sort-field allowlist |
| `ListGateOperations/` | `ListGateOperationsQuery : PagedQuery` | qry | `gates.access.view` (Admin) | ops dashboard incl. dead-letter |
| `RetryGateOperation/` | `RetryGateOperationCommand` | cmd | `gates.access.manage` (Admin) | `dead` → `pending` |
| `IngestGateWebhook/` | `IngestGateWebhookCommand` | cmd | *(internal — dispatched by webhook endpoint, no portal)* | documented AuthorizationBehavior exception: endpoint-verified (§14.1), command not externally reachable |

Handlers stay single-job (rule 17): the move-in *bundle* (Phase 2) is composed by the Lease module's handler invoking `ProvisionGateAccessCommand` via `IMessageBus`, not by a mega-handler.

### 15.2 Representative combined-handler file (convention illustration)

```csharp
// src/BearMGMT.Application/Gates/SuspendGateAccess/SuspendGateAccessCommandHandler.cs

[RequiresPermission(Permissions.Gates.Manage, PortalScope.Admin)]
public sealed record SuspendGateAccessCommand(Guid CredentialId, string? Reason);

public sealed class SuspendGateAccessCommandHandler(
    IGateCredentialRepository credentialRepository,
    IGateOperationRepository operationRepository,
    IAuditService auditService,
    ICurrentUserService currentUser,
    ILogger<SuspendGateAccessCommandHandler> logger)
{
    public async Task<SuspendGateAccessResult> Handle(SuspendGateAccessCommand command, CancellationToken ct)
    {
        logger.LogInformation(BearAuthLogEvents.GateAccessSuspendRequested,
            "Suspending gate access for credential {CredentialId}", command.CredentialId);

        var credential = await credentialRepository.GetByIdAsync(command.CredentialId, ct)
            ?? throw new NotFoundException("Gate credential not found.");

        credential.Suspend(command.Reason);          // domain state machine: active → suspended, is_gate_restricted = true

        operationRepository.Enqueue(GateOperation.SuspendVisitor(credential, currentUser.CorrelationId));

        auditService.Record(new AuditLog
        {
            OrganizationId = credential.OrganizationId,
            ActorUserId = currentUser.UserId,
            ActorMembershipId = currentUser.MembershipId,
            TargetTable = AuditTargetTables.GateAccessCredentials,
            TargetId = credential.Id,
            Action = SecurityAuditActions.GateAccessSuspended,
            BeforeState = /* snapshot WITHOUT access_code_* fields */ null,
            AfterState = /* snapshot WITHOUT access_code_* fields */ null,
            PortalScope = AuditPortalScope.Admin,
            Metadata = "{}",
        });

        await credentialRepository.SaveChangesAsync(ct);   // credential + operation + audit commit atomically

        logger.LogInformation(BearAuthLogEvents.GateAccessSuspended,
            "Gate access suspended for credential {CredentialId}; operation enqueued", credential.Id);

        return SuspendGateAccessResult.FromEntity(credential);
    }
}
```

Domain entities (`GateProviderConfig`, `GateFacilityMapping`, `GateUnitMapping`, `GateAccessCredential`, `GateEvent`, `GateOperation`) live in `src/BearMGMT.Domain/Gates/` — sealed, `AuditableEntity` + `IHasTenant` (except `GateEvent`, which is append-only like `AuditLog`), state transitions as aggregate methods (`Suspend()`, `Activate(externalVisitorId)`, `ScheduleRevocation(at)`, …) raising domain events where useful, zero external dependencies.

### 15.3 Endpoint map

| Route | Verb | Dispatches | Portal policy |
|---|---|---|---|
| `/api/v1/admin/gates/configs` | GET/POST | list / `ConfigureGateProviderCommand` | AdminPortal |
| `/api/v1/admin/gates/configs/{id}/facilities` | GET | `ListGateFacilitiesQuery` (vendor discovery) | AdminPortal |
| `/api/v1/admin/gates/properties/{propertyId}/link` | POST | `LinkGateFacilityCommand` | AdminPortal |
| `/api/v1/admin/gates/properties/{propertyId}/sync-units` | POST | `SyncGateUnitsCommand` | AdminPortal |
| `/api/v1/admin/gates/credentials` | POST | `ProvisionGateAccessCommand` | AdminPortal |
| `/api/v1/admin/gates/credentials/{id}` | GET | `GetGateCredentialQuery` | AdminPortal |
| `/api/v1/admin/gates/credentials/{id}/suspend` `/reinstate` `/revoke` `/regenerate-code` | POST | respective commands | AdminPortal |
| `/api/v1/admin/gates/credentials/{id}/schedule-revocation` | POST/DELETE | schedule / cancel | AdminPortal |
| `/api/v1/admin/gates/events` | GET | `ListGateEventsQuery` (paged) | AdminPortal |
| `/api/v1/admin/gates/operations` | GET | `ListGateOperationsQuery` (paged) | AdminPortal |
| `/api/v1/admin/gates/operations/{id}/retry` | POST | `RetryGateOperationCommand` | AdminPortal |
| `/api/v1/client/gates/my-code` | GET | `GetTenantGateCodeQuery` | ClientPortal |
| `/api/v1/webhooks/gate/opentech/{providerConfigId}` | POST | `IngestGateWebhookCommand` | **AllowAnonymous** + §14.1 verification |

One static class `GateEndpoints` (plus `GateWebhookEndpoints`) in `src/BearMGMT.Api/Endpoints/Gates/`; portal policy on the group; thin bodies; RFC 9457 errors via the global middleware; auth-tier rate limiting on the webhook route.

---

## 16. Tenant Portal Display & the 2×2 Restriction Matrix (TP-004/TP-013)

Gate restriction (`gate_access_credentials.is_gate_restricted`, mirrors the vendor `isEnabled=false` state) and **portal-access restriction** (delinquency lockout of the Client Portal itself — a portal/user concern that deliberately does *not* live on the gate credential) are **independent states**:

| | Portal access OK | Portal restricted |
|---|---|---|
| **Gate access OK** | Normal: code displayed in plain text in Unit Detail | Tenant cannot reach the portal (or reaches a restricted shell) — gate still works physically; code not viewable online |
| **Gate restricted** | Portal loads; Unit Detail shows the code **struck-through with a lock icon** and tooltip "contact your property manager"; **the code value itself is absent from the response** | Both restricted: no portal, no gate |

**Server-side withholding is the load-bearing requirement:** when gate access is restricted, `GetTenantGateCodeQueryHandler` **omits the code from the DTO entirely** — the response carries `isRestricted: true` and no code field. The frontend renders the struck-through placeholder from the flag alone. "Hidden client-side" (code present in JSON but not rendered) is explicitly non-compliant with TP-004.

```csharp
// inside GetTenantGateCodeQueryHandler — ownership + withholding
if (credential.TenantUserId != currentUser.UserId)
    throw new NotFoundException("Gate code not found.");        // 404, not 403 — no enumeration leak

var showCode = credential.Status == GateCredentialStatus.Active && !credential.IsGateRestricted;
return new TenantGateCodeResult(
    UnitNumber: unit.UnitNumber,
    IsRestricted: !showCode,
    GateCode: showCode ? await codeCipher.DecryptAsync(credential, ct) : null,  // null = JSON field omitted
    LastAccessAt: credential.LastAccessAt);
```

Every reveal through this query is audited (`gate_code.revealed`, client portal scope). The "gate-system integration manages gate state independently of the portal/OMS" dependency in the requirement is exactly this module — the flags feeding both surfaces come from `gate_access_credentials`, which the OMS owns.

---

## 17. Authorization & Permissions

New permission codes (constants in `Permissions.cs` — never string literals):

```csharp
public static class Gates
{
    public const string ConfigurationView   = "gates.configuration.view";
    public const string ConfigurationManage = "gates.configuration.manage";
    public const string View                = "gates.access.view";
    public const string Manage              = "gates.access.manage";
    public const string EventsView          = "gates.events.view";
}
```

Seeded role grants (separate seed migration, `ON CONFLICT DO NOTHING`):

| Role | configuration.view | configuration.manage | access.view | access.manage | events.view |
|---|---|---|---|---|---|
| `super_admin` | ✔ (bypass) | ✔ | ✔ | ✔ | ✔ |
| `org_owner` / `org_admin` | ✔ | ✔ | ✔ | ✔ | ✔ |
| `manager` | ✔ | — | ✔ | ✔ | ✔ |
| `staff` | — | — | ✔ | ✔ | ✔ |
| `viewer` / `read_only` | — | — | ✔ | — | ✔ |
| `tenant` | — | — | own code only (ownership check, not permission) | — | — |

Enforcement layers as everywhere in Bear: portal policy on the route group → `AuthorizationBehavior` (fail-closed) on the command/query → handler ownership checks (404-not-403) → EF global tenant filters on every gate table. The one documented exception: `IngestGateWebhookCommand` carries no `[RequiresPermission]` because its caller is the §14.1-verified anonymous endpoint and the command is not dispatchable from any external surface — this exception is documented in code per the fail-closed rule's escape-hatch convention.

---

## 18. Observability, Audit & Alerting

### 18.1 Structured log events — block **9600–9699** (next free module block)

| Range | Events (examples) |
|---|---|
| 9600–9609 | Provider config: created, verified, verification_failed, disabled, secret_rotated |
| 9610–9619 | Mapping: facility_linked, facility_link_failed, units_synced, unit_sync_conflict |
| 9620–9634 | Credential lifecycle: provision_requested, activated, activation_failed, suspended, reinstated, revoke_requested, revoke_scheduled, revoked, revocation_failed, code_regenerated |
| 9635–9639 | Code security: code_revealed (staff), code_revealed_tenant, reveal_denied |
| 9640–9654 | Operations: dispatched, succeeded, retry_scheduled, dead_lettered, manually_retried, reconcile_adopted_visitor |
| 9655–9669 | Webhook/events: received, signature_rejected, topic_rejected, subscription_confirmed, unsubscribe_received, event_ingested, event_processing_failed, drift_detected |
| 9670–9679 | Backfill/polling: started, page_ingested, completed, failed |
| 9680–9689 | Token/vendor client: token_acquired, token_refresh_failed, vendor_call_failed |

**Never logged:** gate codes, vendor API secrets, token request bodies, full `codeEntered` values. IDs and correlation tokens only (no tenant PII in tags — `bear.gates.credential_id`, `bear.gates.facility_mapping_id`, `bear.gates.provider_config_id`).

### 18.2 Audit constants

- `AuditTargetTables`: `GateProviderConfigs`, `GateFacilityMappings`, `GateUnitMappings`, `GateAccessCredentials`, `GateOperations`.
- `SecurityAuditActions`: `gate_config.created`, `gate_config.verified`, `gate_facility.linked`, `gate_units.synced`, `gate_access.provisioned`, `gate_access.activated`, `gate_access.suspended`, `gate_access.reinstated`, `gate_access.revoke_scheduled`, `gate_access.revoked`, `gate_code.regenerated`, `gate_code.revealed`, `gate_operation.dead_lettered`, `gate_operation.retried`, `gate_webhook.subscription_confirmed`.
- Credential audit snapshots **exclude** `access_code_ciphertext/nonce/hmac` (§8.5).

### 18.3 Metrics (OTel counters/gauges alongside `BearAuthMetrics` — a `BearGateMetrics` class)

| Metric | Type | Tags |
|---|---|---|
| `bear.gates.operations.dispatched` / `.succeeded` / `.failed` / `.dead` | counter | operation_type, provider_type |
| `bear.gates.operations.queue_depth` / `.oldest_due_seconds` | gauge | — |
| `bear.gates.revocation.pending_over_sla` | gauge | — (drives the fail-open alert §11.3) |
| `bear.gates.webhook.received` / `.rejected` | counter | reason |
| `bear.gates.events.ingested` | counter | source (webhook/poll), event_type |
| `bear.gates.vendor.calls` / `.failures` | counter | host, status_class |
| `bear.gates.token.acquisitions` / `.refresh_failures` | counter | provider_config (id tag) |

### 18.4 Alerts

| Condition | Severity | Channel |
|---|---|---|
| Revocation pending > SLA (default 15 min) | **High** — tenant retains access | Staff notification + dashboard |
| Operation dead-lettered | High | Staff task |
| Provision failed terminally (TM-009 failure message) | Medium | Staff task + on-screen state |
| Drift detected (granted event for revoked credential) | **High** | Staff + engineering |
| Webhook signature/topic rejections spike | Medium | Engineering (possible probing) |
| `UnsubscribeConfirmation` received | Medium | Engineering (event flow stopped) |
| Confirmation-email bounce (Phase 2) | Medium | Staff task (TM-009) |
| Backfill job failure ×2 consecutive | Medium | Engineering |

---

## 19. Failure Modes & Runbook

| # | Failure | System behavior | Staff/engineer remediation |
|---|---|---|---|
| F1 | Code generation exhausts 10 collision attempts | Command fails with TM-009 failure message; nothing persisted | Investigate code-space saturation / blocklist config; retry provisioning |
| F2 | Vendor down during move-in | Credential `pending_activation`; UX: "activation in progress"; dispatcher retries | None usually; if dead-lettered → retry from ops dashboard; verbal/manual gate entry per property procedure |
| F3 | Ambiguous timeout on create_visitor | Reconcile-before-retry adopts existing visitor if present (§9.3) | If reconcile mismatches (name collision), resolve manually in ops dashboard: adopt id or remove vendor duplicate |
| F4 | Revocation failing > SLA | `revoke_pending` + high-severity alert (fail-open §11.3) | Disable visitor directly in OpenTech operator console; Bear retry will land 409 → success |
| F5 | Scheduled transfer revocation missed (Worker outage) | Rows execute on next Worker start (schedule is durable in DB) | Check Worker health; oldest-due-age gauge catches silent stalls |
| F6 | Webhook flow stops (unsubscribe / SNS misconfig) | Nightly backfill still ingests events; alert on unsubscribe | Re-establish subscription with vendor support; run on-demand backfill |
| F7 | 403 from vendor (credential rotation/expiry) | Config → `sync_error`; operations pause for that config; alert | Rotate secret in Secrets Manager (config UI); `VerifyCredentialsAsync` re-activates |
| F8 | Facility linked to wrong vendor facility | Mutations target wrong property — **highest-impact human error** | Prevention: suggest-then-confirm + `VerifyCredentialsAsync` + sanity checks (unit-count comparison shown at link time). Remediation: unlink (blocked while live credentials exist), relink, resync |
| F9 | Drift: vendor grants access for revoked credential | Drift alert (§14.3) | Immediate manual disable at vendor; reconciliation job comparing Bear live credentials vs `GET /facilities/{fid}/unitvisitorstatus` |
| F10 | Email bounce on move-in confirmation | `GateCodeEmailBounced` → staff task (Phase 2) | Contact tenant through alternate channel; code remains visible in both portals |

---

## 20. Testing Strategy

| Layer | Project | What |
|---|---|---|
| Handler unit tests | `BearMGMT.UnitTests` | Every command/query handler ≥ 90% branch/line: state-machine transitions (all edges in §7.4/§7.7 diagrams), code generation (filters, collision retry, cap), withholding logic (all four matrix cells §16), reconcile-before-retry branches, retry taxonomy per status code. `NSubstitute` mocks for `IGateAccessProvider`/repos; naming `MethodName_Condition_ExpectedResult()` |
| Domain tests | `BearMGMT.UnitTests` | `GateAccessCredential`/`GateOperation` aggregate methods reject invalid transitions (e.g., `Suspend()` on `revoked` throws `ConflictException`) |
| Adapter tests | `BearMGMT.UnitTests` | OpenTech adapter against a fake `HttpMessageHandler`: header injection (`api-version`, correlation id), password/refresh grant bodies, 401-refresh-retry-once, 409-as-success, error sanitization; SNS processor against captured POC payloads (signature verification with test certs, cert-host allowlist bypass attempts, envelope parsing) |
| Integration tests | `BearMGMT.IntegrationTests` | `WebApplicationFactory<Program>`: endpoint auth (admin vs client vs anonymous webhook), tenant isolation across orgs (org A cannot read org B's credentials), dedup insert under concurrency, dispatcher `SKIP LOCKED` with two concurrent claimants, migration up/down |
| Architecture tests | `BearMGMT.ArchitectureTests` | NetArchTest: no vendor DTO types outside Infrastructure; Application has no reference to AWS SDKs; Domain purity holds |
| Contract tests | `BearMGMT.ContractTests` | Canonical record shapes frozen; vendor-facing request shapes pinned against the QA-validated examples (Appendix A) |
| QA sandbox smoke | `BearMGMT.SmokeTests` (opt-in, credentials via user-secrets) | The POC's proven end-to-end loop: token → facility → unit → visitor create/disable/enable/vacate → events poll — run against the vendor QA environment before releases |
| Webhook simulation | dev tooling | Replay captured SNS payloads (SubscriptionConfirmation + Notification) against a local endpoint; ngrok-style tunnel for live QA subscription tests (as done in the `IoEWebhook` POC) |

---

## 21. Phased Delivery Plan & Module Dependencies

### 21.1 What ships when

| Phase | Deliverables | Depends on |
|---|---|---|
| **1 — MVP core** | Domain entities + migrations (7 tables), permissions seed, provider port + OpenTech adapter + token provider, Secrets Manager + KMS cipher, all §15 slices (staff-triggered), Worker dispatcher + backfill + sweep, webhook endpoint + SNS pipeline, admin endpoints, ops dashboard queries, observability | Nothing — Properties & Units exist |
| **2 — Lease & tenant experience** | Lease module invokes `ProvisionGateAccessCommand`/`RevokeGateAccessCommand`/transfer composition; `lease_id` FK added; move-out bundle wiring; tenant portal `my-code` endpoint + 2×2 display; confirmation email + bounce alert | **Lease/Contract module**, **Notifications module** |
| **3 — Delinquency & hardening** | Escalation profiles/steps + engine consuming `TenantDelinquencyStageChanged`; overlock work-order integration; SNS→SQS; `gate_events` partitioning; optional additional provider adapter | **Payment module** (and Maintenance for overlock tasks) |

### 21.2 Contracts future modules must fulfil

- **Lease/Contract module:** on move-in finalization, invoke `ProvisionGateAccessCommand(unitId, tenantUserId, leaseId)`; on move-out confirmation, include `RevokeGateAccessCommand` in its bundle transaction; on transfer, invoke provision + `ScheduleGateRevocationCommand`; publish lease-end domain events so scheduled revocation can also key off lease expiry (TM-009 "auto-deactivates when lease ends").
- **Notifications module:** consume `GateAccessProvisioned` → move-in confirmation email containing the code (pending C-2); consume provider delivery-status callbacks and publish `GateCodeEmailBounced(tenantUserId, credentialId)` → staff task (TM-009 bounce alert).
- **Payment module:** publish `TenantDelinquencyStageChanged` (§12.2) after its own commit; never call the vendor directly.
- **Maintenance module:** accept work-order creation from the move-out bundle and from escalation `overlock_task` steps.

---

## 22. Open Questions, Risks & Decision Log

### 22.1 Questions for the vendor (OpenTech)

| # | Question | Blocks |
|---|---|---|
| V-1 | How is the SNS webhook subscription provisioned per account — self-service API/portal or support ticket? (Developer-portal IOE access must be requested early — identify as the "Bear MGMT" dev team) | Onboarding automation (§5.4), Phase 1 |
| V-2 | Can `accessCode` be updated **in place** on an existing visitor, or is remove+recreate required? | Regeneration design (§8.4) |
| V-3 | Rate limits per account/host? Recommended concurrency for bulk unit sync? | Dispatcher throughput tuning |
| V-4 | Is calling `/visitors/{vid}/remove` directly on an `isTenant` visitor officially supported (worked in QA; spec text says vacate the unit)? | Capability flag default (§4.3) |
| V-5 | Limits on multiple enabled visitors per unit? (Co-tenant/spouse codes are an inevitable future ask) | Data model headroom |
| V-6 | Webhook delivery retry policy, ordering guarantees, and authoritative field mapping of the SNS `GatewayEvent` payload vs `FacilityEventModel`? | §14 hardening |
| V-7 | Is `timeGroupId`/`accessProfileId` = `0` a reserved "use default" sentinel, or must the fields be omitted/null for documented defaults? | §4.3 request shape |
| V-8 | Idempotency guidance for `create_visitor` after an ambiguous timeout (no client-supplied key exists) | §9.3 residual risk |
| V-9 | Production credential provisioning + QA credential rotation process | Go-live |

### 22.2 Decisions for the client (Bear MGMT product)

| # | Question | Recommendation |
|---|---|---|
| C-1 | **Vendor account model:** one OpenTech account per organization, or a single Bear-owned (reseller) account? Commercial/liability question — schema supports both (§5.2) | Per-organization accounts |
| C-2 | Gate code **in the confirmation email** = plaintext credential at rest in the tenant's mailbox. Accept the risk, or send a portal deep-link ("view your gate code") instead? | Deep-link; code-in-email only as an explicitly accepted risk |
| C-3 | Exact cut-over time for scheduled transfer revocation (midnight? end of business day? gate closing time?) — evaluated in the property's timezone | End of day, property-local |
| C-4 | Fail-open SLA: how long may a failed revocation persist before staff are alerted / considered an incident? | 15 minutes to alert |
| C-5 | Confirm PM-007 "overlock" = physical padlock work-order (not a gate-API action), optionally combined with gate suspend | As stated (§12.1) |
| C-6 | Sequencing: gate MVP ships staff-triggered (no Lease module); confirm this is acceptable for early properties | Yes — same commands get automated later |
| C-7 | Default code length (6 digits proposed) and whether tenants may choose/regenerate their own code from the portal | 6 digits; staff-regenerate only in Phase 2 |
| C-8 | Delinquency gate-lock scope: per-visitor disable (recommended — vendor unit status stays Bear-controlled) or unit-level disable (vendor unit shows `Delinquent`)? | Per-visitor |

### 22.3 Internal risks

| Risk | Mitigation |
|---|---|
| Wolverine has no durable transport | DB-backed `gate_operations` is the durability layer (D4); revisit when durable transport lands |
| Worker gains its first production responsibility | Needs deployment/scaling/health story before Phase 1 ships; `SKIP LOCKED` already makes it horizontally safe |
| `gate_events` unbounded growth | BRIN + pagination now; monthly partitioning in Phase 3 |
| Secrets cache vs rotation | 401-after-refresh evicts cache (§6.3) |
| No tenant-person entity — credentials bind to auth users | Acceptable now; `lease_id` reserved column is the Phase 2 re-anchor; Auth0 `sub`/ExternalIdentity remains the durable person key per identity-resolution conventions |
| QA credentials present in POC `appsettings.json` | **Rotate before production; never migrate into Bear source**; Bear stores only `secret_ref` |

### 22.4 Decision log

| # | Decision | Status |
|---|---|---|
| D1–D8 | See §1 | Proposed (this document) |
| — | Output of C-1…C-8 | Pending client review |
| — | Output of V-1…V-9 | Pending vendor responses |

---

## Appendix A — Sample Payloads (Sanitized)

**Create visitor (move-in) — QA-confirmed shape** (`POST /facilities/{facilityId}/visitors`, `api-version: 2.0`):

```json
{
  "firstName": "Jane",
  "lastName": "Smith",
  "isTenant": true,
  "unitId": 38616,
  "accessCode": "REDACTED-6-DIGITS",
  "email": "jane@example.com",
  "mobilePhoneNumber": "5550100000",
  "contactMethodPreference": "Unspecified",
  "countryCode": "US",
  "suppressCommands": false
}
```

Response essentials: `visitor.id` (**store immediately**), `visitor.isEnabled`, `visitor.unitId`, `unit.status: "Rented"`. Note `visitor.code` is populated only on the create response in some deployments and never on reads — Bear relies on its own encrypted copy regardless.

**SNS Notification envelope (webhook):**

```json
{
  "Type": "Notification",
  "MessageId": "…",
  "TopicArn": "arn:aws:sns:us-west-2:…:…PublishedWebhookGatewayEvents",
  "Message": "{\"message_type\":\"GatewayEvent\",\"message_data\":\"{\\\"id\\\":\\\"…\\\",\\\"facility\\\":{\\\"id\\\":2979},\\\"unit\\\":{\\\"id\\\":36528},\\\"type\\\":{\\\"enum\\\":18},…}\"}",
  "Timestamp": "…",
  "SignatureVersion": "1",
  "Signature": "…",
  "SigningCertURL": "https://sns.us-west-2.amazonaws.com/….pem"
}
```

Note `message_data` is a **JSON-encoded string**, not a nested object — two-stage deserialization is required (POC-proven).

**Event backfill page** (`GET {eventUrl}/events/facilities/{facilityId}`): `{ "metadata": { "limit", "offset", "totalResults", "totalPages", "pageNumber" }, "items": [FacilityEventModel…] }`; `204` = empty page.

## Appendix B — Configuration Reference

Bear-side configuration (per environment; **no vendor values in `appsettings.json` beyond structure**):

```jsonc
"GateAccess": {
  "Kms": { "KeyArn": "SET-VIA-USER-SECRETS-OR-ENV" },
  "HmacKeySecretRef": "SET-VIA-USER-SECRETS-OR-ENV",
  "Dispatcher": { "PollSeconds": 30, "BatchSize": 20, "MaxAttempts": 10, "RevocationSlaMinutes": 15 },
  "Codes": { "DefaultLength": 6, "MinLength": 4, "MaxLength": 10, "MaxGenerationAttempts": 10 },
  "Backfill": { "Cron": "nightly", "PageSize": 200 }
}
```

Per-organization vendor credentials live **only** in AWS Secrets Manager, referenced by `gate_provider_configs.secret_ref`; secret JSON shape: `{ "authBaseUrl", "controlBaseUrl", "eventBaseUrl", "clientId", "clientSecret", "apiKey", "apiSecret" }`. QA base URLs: `https://auth.insomniaccia-qa.com`, `https://accesscontrol.insomniaccia-qa.com`, `https://accessevent.insomniaccia-qa.com`. Vendor support contacts and the IOE developer-portal request process are listed in the vendor integration guide (§10 there).

---

*End of document. Companion sources: `access-control-integration-guide_v2_poc-validated.docx` (vendor detail), `C:\repo\AccessControlPoc` (outbound spike), `C:\repo\IoEWebhook` (inbound spike).*





