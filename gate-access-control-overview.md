# Gate Access Control — Visual Overview

**BearMGMT ↔ OpenTech IoE integration — the whole design in pictures.**

Self-storage properties have physical gates with keypads. BearMGMT issues each tenant a unique PIN (gate code); the gate hardware is run by a vendor platform — **OpenTech Alliance IoE** — that Bear drives over REST and listens to via webhooks. **Bear decides, OpenTech executes at the gate.**

> Condensed companion to the full design: [gate-access-control-architecture.md](gate-access-control-architecture.md)

---

## 1. The Tenant's Journey in One Picture

```mermaid
flowchart LR
    A([Move-In]) -->|"code generated + visitor created"| B[Code works at gate]
    B -->|"payment overdue"| C[Code rejected - delinquent]
    C -->|"pays up"| B
    B -->|"unit transfer"| D[New code on new unit NOW<br/>old code dies on move-out date]
    B -->|"move-out"| E([Code revoked<br/>unit vacant])
    C -->|"move-out"| E
    D --> E

    style A fill:#2e7d32,color:#fff
    style B fill:#388e3c,color:#fff
    style C fill:#e65100,color:#fff
    style D fill:#1565c0,color:#fff
    style E fill:#616161,color:#fff
```

Every arrow above is one Bear command that (1) commits to Bear's database first, then (2) calls OpenTech — never the other way round.

---

## 2. The Big Picture

```mermaid
flowchart LR
    subgraph Bear["🐻 BearMGMT"]
        API[API<br/>staff + tenant endpoints<br/>+ webhook receiver]
        DB[(PostgreSQL<br/>mappings · credentials<br/>work queue · events)]
        WK[Worker<br/>executes queued vendor calls<br/>+ nightly event backfill]
        API --> DB
        WK --> DB
    end
    subgraph OT["🏢 OpenTech IoE"]
        AC[Access Control API<br/>facilities · units · visitors]
        EV[Event API<br/>history]
        SNS[Webhooks<br/>real-time push]
    end
    GATE["🚪 Gate keypad"]

    API -->|commands| AC
    WK -->|retries + scheduled| AC
    WK -->|backfill| EV
    SNS -->|access events| API
    AC -.->|config push| GATE
    GATE -.->|every PIN entry| SNS
```

**Four rules carry the design:**

| # | Rule | Why |
|---|---|---|
| 1 | 🔌 **Provider port** — business logic talks to an interface, OpenTech is just an adapter | Swap vendors later without touching business code |
| 2 | 💾 **DB first, vendor second** — intent committed locally before any vendor call | Failures become visible, retryable rows — never silent loss |
| 3 | 📋 **Work-queue table** (`gate_operations`) — Worker retries, schedules, dead-letters + alerts | Survives restarts; future-dated rows = scheduled revocations |
| 4 | 👁 **Events are observational** — webhooks feed dashboards/alerts, never decisions | A missed webhook can't corrupt state; nightly poll closes gaps |

---

## 3. BearMGMT ↔ OpenTech Mapping

```mermaid
flowchart LR
    subgraph B["BearMGMT world"]
        ORG[🏛 Organization]
        PROP[🏘 Property]
        UNIT[📦 Unit]
        TEN[👤 Tenant on unit]
        ORG --> PROP --> UNIT --> TEN
    end
    subgraph M["Mapping tables in Bear DB"]
        M1[gate_provider_configs<br/>credentials ref → Secrets Manager]
        M2[gate_facility_mappings<br/>1 : 1]
        M3[gate_unit_mappings<br/>1 : 1]
        M4[gate_access_credentials<br/>code + visitorId + status]
    end
    subgraph O["OpenTech world"]
        ACC[🔑 Account<br/>API credentials]
        FAC[🏭 Facility<br/>facilityId]
        VUN[📦 Unit<br/>unitId]
        VIS[🎫 Visitor<br/>visitorId + accessCode]
        ACC --> FAC --> VUN --> VIS
    end
    ORG === M1 === ACC
    PROP === M2 === FAC
    UNIT === M3 === VUN
    TEN === M4 === VIS
```

- **Recommended:** each organization has **its own OpenTech account** (isolation, blast radius). A single shared Bear account fits the same schema — pending business decision **C-1** (§9).
- **Setup:** Org Admin enters credentials once (write-only → AWS Secrets Manager), Bear test-calls to verify, admin links each property to its facility (Bear suggests by `propertyNumber`, human confirms), then runs unit sync.
- **Two vendor quirks drive the whole data model:** OpenTech assigns `visitorId` itself with **no external key** (Bear must capture it from the create response instantly), and the gate code is **never echoed back** (Bear's encrypted copy is the only one).

---

## 4. Credential Lifecycle (the `status` column)

```mermaid
stateDiagram-v2
    [*] --> pending_activation : move-in (code saved locally)
    pending_activation --> active : vendor create OK
    pending_activation --> failed : retries exhausted → staff alert
    failed --> pending_activation : staff retry
    active --> suspended : delinquent → gate locked
    suspended --> active : paid → gate unlocked
    active --> revoke_scheduled : transfer (future-dated)
    revoke_scheduled --> active : transfer cancelled
    revoke_scheduled --> revoke_pending : move-out date reached
    active --> revoke_pending : move-out
    suspended --> revoke_pending : move-out
    revoke_pending --> revoked : vendor confirmed
    revoked --> [*]
```

| Business event | Vendor call | Effect at the gate |
|---|---|---|
| Move-in | `POST …/visitors` | Code works, unit → Rented |
| Delinquent | `POST …/visitors/{vid}/disable` | Code rejected, record kept |
| Paid | `POST …/visitors/{vid}/enable` | Code works again |
| Move-out | `POST …/units/{uid}/vacate` | All unit codes dead, unit → Vacant |
| Transfer | new visitor now + **scheduled** vacate later | Both codes live during overlap |

---

## 5. The Two Key Tables

### 🔗 `gate_unit_mappings` — *"which vendor unit is this Bear unit?"*

| Column | Plain English |
|---|---|
| `unit_id` *(unique)* | The Bear unit |
| `external_unit_id` | OpenTech's numeric `unitId` — needed for every visitor call |
| `external_unit_number` | Human-readable matching key during sync |
| `sync_status` | `pending` / `synced` / `error` |

No row here → move-in cannot proceed (visitor create needs the vendor `unitId`).

### 🎫 `gate_access_credentials` — *"who holds which code, and is it live?"*

| Column | Plain English |
|---|---|
| `tenant_user_id`, `unit_id` | Who and where |
| `lease_id` *(nullable)* | Reserved — binds to the future Lease module |
| `external_visitor_id` | OpenTech's `visitorId` — the only correlation key that exists |
| `access_code_ciphertext` | The code, **encrypted** (AES-256-GCM, KMS-managed keys) |
| `access_code_hmac` | Keyed hash → unique index = **no duplicate live codes per facility** |
| `status` | Lifecycle state (§4) |
| `is_gate_restricted` | Gate-locked flag — tenant portal then **withholds the code server-side** |
| `last_access_at` | Last successful gate entry (from the event stream) |

**Why encrypted and not hashed — the code's whole life:**

```mermaid
flowchart LR
    G[🎲 Generate<br/>6 digits, CSPRNG] --> E[🔒 Encrypt<br/>AES-256-GCM via KMS]
    G --> H[#️⃣ HMAC<br/>uniqueness check]
    E --> S[(stored ciphertext)]
    H --> S
    S -->|decrypt only for| V[📤 vendor create call]
    S -->|decrypt only for| P[🖥 staff screen +<br/>tenant portal display]
    S -.->|never| L[🚫 logs · audit JSON ·<br/>work queue · URLs]

    style L fill:#b71c1c,color:#fff
```

The vendor never returns the code and the tenant portal must display it → Bear's copy must be recoverable → encryption, not hashing. Every reveal is audited.

---

## 6. API Calls in a Nutshell (QA-validated)

```mermaid
flowchart LR
    T["1️⃣ POST /auth/token<br/>password grant<br/>→ Bearer, ~1h, cached"] --> U["2️⃣ POST /facilities/{fid}/units<br/>{unitNumber}<br/>→ store unit.id"]
    U --> V["3️⃣ POST /facilities/{fid}/visitors<br/>{isTenant, unitId, accessCode}<br/>→ store visitor.id"]
    V --> W["4️⃣ …/visitors/{vid}/disable | enable<br/>…/units/{uid}/vacate<br/>empty-body POSTs"]
```

**Create visitor** (the important one — move-in):

```json
// POST /facilities/8594/visitors        headers: Bearer + api-version: 2.0
{ "firstName": "Jane", "lastName": "Smith", "isTenant": true,
  "unitId": 38616, "accessCode": "REDACTED-6-DIGITS" }
```
```json
// response — capture visitor.id IMMEDIATELY, note code is NOT echoed
{ "visitor": { "id": 71035, "isEnabled": true, "unitId": 38616, "code": null },
  "unit":    { "id": 38616, "status": "Rented" } }
```

**Error rules:** `401` → refresh token, retry once · `409` → already done, treat as success · `4xx` → don't retry (data bug) · `5xx` → backoff retry.

---

## 7. Database at a Glance

```mermaid
erDiagram
    gate_provider_configs ||--o{ gate_facility_mappings : owns
    gate_facility_mappings ||--o{ gate_unit_mappings : contains
    gate_facility_mappings ||--o{ gate_access_credentials : scopes
    gate_unit_mappings ||--o{ gate_access_credentials : "unit link"
    gate_access_credentials ||--o{ gate_operations : "acted on by"
    gate_provider_configs ||--o{ gate_events : receives

    gate_provider_configs {
        uuid organization_id "the org"
        text provider_type "open_tech"
        text secret_ref "Secrets Manager ARN - no credentials in DB"
    }
    gate_facility_mappings {
        uuid property_id "UNIQUE - one gate system per property"
        text external_facility_id "vendor facilityId"
    }
    gate_unit_mappings {
        uuid unit_id "UNIQUE"
        text external_unit_id "vendor unitId"
        text sync_status "pending-synced-error"
    }
    gate_access_credentials {
        uuid tenant_user_id "the renter"
        text external_visitor_id "vendor visitorId"
        bytea access_code_ciphertext "AES-256-GCM"
        text status "lifecycle state"
        boolean is_gate_restricted
    }
    gate_operations {
        text operation_type "create_visitor, vacate_unit..."
        text status "pending-succeeded-failed-dead"
        timestamptz scheduled_for "future = scheduled revocation"
    }
    gate_events {
        text external_event_id "dedup key"
        text event_type "access_granted, access_denied..."
        jsonb raw_payload
    }
```

Plus `gate_encryption_keys` (wrapped per-org keys) and the standard audit trail. Everything is organization-scoped under Bear's tenant filters.

---

## 8. The Three Flows That Matter

### 🟢 Move-In — *local first, vendor second, never blocks the counter*

```mermaid
sequenceDiagram
    autonumber
    actor Staff
    participant H as Bear Handler
    participant DB as Bear DB
    participant OT as OpenTech

    Staff->>H: Finalize move-in
    H->>H: generate unique code
    H->>DB: save credential (pending_activation) + queue operation
    Note over DB: ✅ committed — intent durable
    H->>OT: create visitor (code, unitId)
    alt Vendor OK
        OT-->>H: visitor.id
        H->>DB: credential → active
        H-->>Staff: 🎫 code on-screen + emailed
    else Vendor down
        H-->>Staff: "code generated — activation in progress"
        Note over DB,OT: Worker retries until success or staff alert
    end
```

### 🔴 Move-Out — *one atomic local transaction, guaranteed-eventual revocation*

```mermaid
sequenceDiagram
    autonumber
    actor Staff
    participant H as Bear Handler
    participant DB as Bear DB
    participant W as Worker
    participant OT as OpenTech

    Staff->>H: Confirm move-out
    H->>DB: ONE transaction — unit status, autopay off,<br/>work order, credential → revoke_pending, queue vacate
    H-->>Staff: ✅ move-out confirmed
    W->>OT: vacate unit
    alt OK (or 409 already vacant)
        W->>DB: credential → revoked
    else Failing > 15 min
        Note over W,Staff: 🚨 alert — tenant may still have gate access<br/>(manual disable at vendor console)
    end
```

### 🔄 Unit Sync — *make vendor units match Bear units at onboarding*

```mermaid
sequenceDiagram
    autonumber
    actor Admin as Org Admin
    participant H as Bear Handler
    participant DB as Bear DB
    participant OT as OpenTech

    Admin->>H: Sync units for property
    H->>OT: list vendor units
    OT-->>H: existing units
    H->>DB: match by unitNumber → mappings (synced)
    loop Bear units missing vendor-side
        H->>OT: create unit
        OT-->>H: unit.id
        H->>DB: save mapping
    end
    H-->>Admin: 📊 matched / created / unmatched report
```

---

## 9. Open Points to Clear ⚠

### Decisions needed from the client (Bear MGMT product)

| # | Question | Recommendation |
|---|---|---|
| C-1 | **Account model** — one OpenTech account per organization, or single Bear-owned (reseller) account? Commercial/liability call; schema supports both | Per-organization |
| C-2 | Gate code **inside the confirmation email** = plaintext credential sitting in a mailbox. Accept, or send a portal deep-link instead? | Deep-link |
| C-3 | Transfer cut-over time for old-unit revocation (midnight? end of business? gate closing?) — in property timezone | End of day, property-local |
| C-4 | **Fail-open SLA** — how long may a failed revocation persist before staff are alerted? | 15 minutes |
| C-5 | Confirm "overlock" (PM-007) = physical padlock **work order**, not a gate API action | As stated |
| C-6 | Gate MVP ships **staff-triggered** (Lease module doesn't exist yet) — acceptable sequencing? | Yes |
| C-7 | Code length (6 digits proposed) and may tenants self-regenerate from the portal? | 6 digits, staff-only regen first |
| C-8 | Delinquency lock scope: per-visitor (recommended) or whole-unit (vendor unit shows Delinquent)? | Per-visitor |

### Questions for the vendor (OpenTech)

| # | Question | Blocks |
|---|---|---|
| V-1 | How is the **SNS webhook subscription** provisioned per account — self-service or support ticket? (Request IOE developer-portal access early, as "Bear MGMT" dev team) | Onboarding automation |
| V-2 | Can `accessCode` be **updated in place**, or is remove + recreate required? | Code regeneration design |
| V-3 | Rate limits per account/host? Safe concurrency for bulk unit sync? | Dispatcher tuning |
| V-4 | Is `/visitors/{vid}/remove` on a **tenant** visitor officially supported? (Worked in QA; spec says vacate the unit instead) | Move-out fallback path |
| V-5 | Limits on **multiple enabled visitors per unit**? (Co-tenant/spouse codes will be asked for) | Data model headroom |
| V-6 | Webhook **retry policy + ordering guarantees**, and authoritative field map of the SNS payload | Event pipeline hardening |
| V-7 | Is `timeGroupId`/`accessProfileId` = `0` a "use default" sentinel, or must fields be omitted? (POC sent 0) | Request shape |
| V-8 | Idempotency guidance for **create-visitor after a timeout** (no client key exists) | Duplicate-visitor risk |
| V-9 | **Production credential** provisioning + QA credential rotation process | Go-live |

### Internal must-dos before production

| Item | Why |
|---|---|
| 🔑 Rotate the QA credentials currently in the POC `appsettings.json`; never copy them into Bear source | They're live vendor secrets |
| ⚙️ Deployment/scaling story for `BearMGMT.Worker` | It gains its first production job (the dispatcher) |
| 📨 Lease, Notifications, Payment module contracts (§21 of full doc) | They trigger/consume the gate commands in Phases 2–3 |

---

*Full detail — schema DDL, security, webhook verification, portal display matrix, runbook, testing, phase plan: [gate-access-control-architecture.md](gate-access-control-architecture.md)*
