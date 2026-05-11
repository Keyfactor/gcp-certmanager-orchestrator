## Overview

The `GcpCertMgr` store type represents a single (Project, Location) pair within Google Cloud Certificate Manager. The orchestrator manages self-managed certificates inside that container - listing them for inventory, uploading new PFX certificates, and deleting existing certificates by alias.

### Configuration model (v1.2+)

Every `GcpCertMgr` store - whether Discovery-approved or manually created - identifies its target Certificate Manager instance through the **Store Path** field:

```
projects/{projectId}/locations/{location}
```

That single value carries both the GCP project and the location (region or `global`). Inventory and Management read it directly; **Client Machine** is a display label for grouping in Command's UI and is not parsed by the orchestrator.

#### Field semantics

| Field | What it carries | Read by |
|---|---|---|
| **Store Path** | Canonical GCP resource path: `projects/{projectId}/locations/{location}`. The `{location}` segment is the GCP region (or `global`) the store targets - this is the only place the orchestrator actually reads the location from for new stores. | Inventory, Management, Discovery (emit) |
| **Client Machine** | Display label only. Recommended: GCP Organization ID (e.g. `1005564431893`). Not parsed. | UI grouping in Command |
| **Service Account Key File Path** (custom, *deprecated*) | v1.1 shape only. Leave blank for new stores - authentication uses Application Default Credentials. | Credential loader fallback; emits a deprecation warning when read |
| **Location** (custom, *deprecated*) | v1.1 shape only. New stores leave it blank. Used as a fallback when Store Path is empty or `n/a`. | v1.1 fallback path; emits a deprecation warning when read |
| **Certificate Scope** (custom) | GCP `scope` to apply to every new certificate created in this store. One of `DEFAULT`, `ALL_REGIONS`, `EDGE_CACHE`, `CLIENT_AUTH`. Blank → `DEFAULT`. Immutable on each cert once set in GCP - use one store per scope. See "Certificate scope" below. | Management/Add |

#### Location semantics: where the GCP region lives

GCP region names (`global`, `us-central1`, `europe-west1`, ...) appear in three distinct places across the orchestrator. They look related but they are **not interchangeable**, and only one of them is load-bearing for new stores. Operators who only skim the field semantics table often miss this and end up confused about which Location field to set where.

| # | Where it appears | What it is | What reads it |
|---|---|---|---|
| 1 | **The `{location}` segment of `Store Path`** (e.g. the `global` in `projects/edgecerts/locations/global`) | The actual GCP region the store targets. Source of truth. | Inventory and Management both call `JobBase.ResolveGcpResourcePath` which returns Store Path verbatim when it matches the canonical form. The location segment is parsed back out (string split) when the Inventory job needs to populate the `Location` parameter on each returned certificate. |
| 2 | **The `Location` custom store property** | A v1.1-shape field. New stores leave it blank. | Only the v1.1 fallback path inside `JobBase.ResolveGcpResourcePath` - it builds `projects/{ClientMachine}/locations/{Location}` when Store Path is blank or `n/a`, and emits a `LogWarning` each time naming the migration step. Removal scheduled for v2.0. |
| 3 | **The Discovery job's "Directories to search" form field** | Operator INPUT to the discovery job. A comma-separated list of regions to enumerate, e.g. `global,us-central1,europe-west1`. | `Discovery.ResolveLocations` parses the list. Discovery then emits one candidate store path per `(project × location)` combination, where the location segment of each emitted Store Path comes from this list. The list itself does **not** propagate to the resulting stores - it has no afterlife once Discovery has emitted candidates. |

##### How the three relate

For a **v1.2 store created via Discovery**:

1. Operator types `global,us-central1` into the discovery job's "Directories to search" - this is place #3.
2. Discovery emits `projects/edgecerts/locations/global` and `projects/edgecerts/locations/us-central1`. Each emitted string is consumed as place #1 (Store Path) when the candidate is approved.
3. The auto-created store has Store Path populated; the **Location custom property is blank** and unused. Place #2 has no role.
4. Inventory reads Store Path (place #1) for the GCP API path, and parses the location segment back out for the `Location` parameter on each cert item.
5. Discovery's "Directories to search" value (place #3) is gone - it never landed on the store.

For a **v1.2 store created manually**:

- Type the full canonical path into Store Path (place #1) - e.g. `projects/edgecerts/locations/global`.
- Leave the Location custom property (place #2) blank.

For a **v1.1 store mid-migration**:

- Store Path is blank or `n/a`; the orchestrator falls back to building the GCP path from `Client Machine` + `Location` (place #2). A deprecation warning is logged every job run. Migrate by populating Store Path (place #1) and clearing place #2.

##### Quick reference

- "Where do I tell the orchestrator which GCP region to use for *this store*?" → **the `{location}` segment of Store Path** (place #1).
- "Where do I tell Discovery which regions to enumerate across *the whole org*?" → **Directories to search** on the discovery job (place #3).
- "What's the Location custom field for?" → Nothing, unless you're maintaining a v1.1 store and haven't migrated yet (place #2).
- "Why does the inventory result still have a `Location` parameter on each certificate?" → That's parsed out of Store Path's location segment for downstream filtering in Command. It mirrors place #1, not place #2.

#### Manually creating a store

Set:

- **Client Machine**: GCP Organization ID
- **Store Path**: `projects/{projectId}/locations/{location}` - e.g. `projects/edgecerts/locations/global`
- **Service Account Key File Path**: leave blank (deprecated; ADC is used)
- **Location**: leave blank
- **Certificate Scope**: `DEFAULT` for global external Application Load Balancers (the common case). Set to `ALL_REGIONS` if this store provisions certs for cross-region internal Application Load Balancers; `EDGE_CACHE` for Media CDN; `CLIENT_AUTH` for mTLS trust configs. See "Certificate scope" below.

Authentication uses Application Default Credentials - see "Service account credentials" below.

#### Approving a Discovery-discovered store

After the discovery job runs, candidates appear in **Locations → Certificate Stores → Discover** (a new tab next to "Certificate Stores"). Tick the candidates you want to track, click **MANAGE**, and Command opens the per-candidate edit dialog. The relevant fields:

| Field | Action |
|---|---|
| **Client Machine** | Pre-filled by Command (often the orchestrator hostname). Display label only - the orchestrator does not parse it. Leave as-is, or change to the GCP Organization ID for cleaner UI grouping. |
| **Store Path** | Pre-filled with the canonical GCP path Discovery emitted (e.g. `projects/edgecerts/locations/global`). **Don't edit** - this is what Inventory and Management read. |
| **Application** | Optional, free-form. |
| **Location** (custom) | Leave blank. Deprecated v1.1 field; the location is parsed from Store Path. |
| **Service Account Key File Path** (custom) | Leave blank. Deprecated v1.1 field; authentication uses Application Default Credentials. |
| **Certificate Scope** (custom) | Defaults to `DEFAULT`. Change only if this discovered store is going to back a non-default scope (`ALL_REGIONS`, `EDGE_CACHE`, `CLIENT_AUTH`) - Discovery does not know which scope your downstream load balancers need, so the operator sets it at approval time. Discovery emits one candidate per (project, location); if you need both `DEFAULT` and `ALL_REGIONS` certs in the same (project, location), reject this candidate and create two stores manually instead. See "Certificate scope" below. |
| **Create Certificate Store If Missing** | **Check this.** Tells Command to create a new certificate store record from this candidate. Without it, the candidate sits in the discover tab with no store backing it. |
| **Inventory Schedule** | Pick a cadence (e.g. Daily) for the inventory job to run after the store is created. |

Click **SAVE** and the store is created. The next inventory run on its schedule will populate it with whatever certificates exist in that (project, location).

### Discovery job configuration

Discovery is configured against the GCP Certificate Manager store type and enumerates candidate stores across an entire GCP organization. It uses the Cloud Resource Manager v3 API (`projects.search`) to list every active project the orchestrator's service account can see, then emits one candidate store path per (project, location) combination.

The "Schedule Discovery" dialog inherits its layout from Keyfactor Command's generic Discovery UI, which was designed for filesystem-based store types (Java keystores, PEM files). Most fields don't apply to GCP. Here is what each field is and what to do with it:

| Field on Schedule Discovery | What to put |
|---|---|
| **Category** | `GCP Certificate Manager` - already populated when reaching this dialog from the GCP store type. |
| **Orchestrator** | Select an approved orchestrator with the `GcpCertMgr` capability. |
| **Schedule** | When discovery should run. `Immediate` runs once on save; pick a recurring schedule for periodic re-enumeration. |
| **Directories to search** | **Required.** Type `global` for the default behavior of searching only GCP's global Certificate Manager location, which is what almost every operator wants. See "Should I ever put something other than `global`?" below for the rare exceptions. |
| **Directories to ignore** | Leave blank. Filesystem-store concept; not used by GCP discovery. |
| **Extensions** | Leave blank. Filesystem-store concept; not used. |
| **File name patterns to match** | Leave blank. Filesystem-store concept; not used. |
| **Follow SymLinks** | Leave unchecked. Filesystem-store concept; not used. |
| **Include PKCS12 Files?** | Leave unchecked. Filesystem-store concept; not used. |

> **Why are most fields irrelevant to GCP?** Command's Discovery UI is one form template shared across every store type. For filesystem-based store types like Java keystores or PEM files, fields like *Directories to ignore*, *Extensions*, *File name patterns to match*, *Follow SymLinks*, and *Include PKCS12 Files?* are useful - they let the orchestrator narrow which files on disk it should treat as candidate stores. GCP Certificate Manager isn't a filesystem; the orchestrator uses Cloud Resource Manager + Certificate Manager APIs, so these fields are not consulted. The orchestrator does not raise an error if you fill them in; it just ignores them.

#### Should I ever put something other than `global`?

Almost never. Concrete guidance:

- **Just type `global` → searches the `global` GCP location only.** This is the right answer for the vast majority of GCP Certificate Manager deployments, because certificates attached to GCP's *global* external Application Load Balancer (the most common load balancer in GCP) are stored in the `global` Certificate Manager location.
- **Add specific regions** (e.g. `global,us-central1,europe-west1`) only if your organization runs **regional** external Application Load Balancers, or has data-residency requirements that pin certificates to specific regions. If you're not sure whether that describes your environment, the answer is "you don't need this" and you should just type `global`.
- **Don't list every GCP region** (`us-central1,us-east1,...`). Discovery does not probe candidates - it emits one (project × location) pair regardless of whether that combination has any certs. Listing 40 regions for a 100-project org produces 4,000 candidate stores, most empty, all cluttering Command's certificate store list.

The format is a comma-separated list of GCP location names exactly as GCP names them. `global` is the universal location; regional names follow GCP's standard `<area>-<region><number>` form (e.g. `us-central1`, `europe-west1`, `asia-southeast1`). See the [Certificate Manager supported locations list](https://cloud.google.com/certificate-manager/docs/locations) for the canonical set.

The candidate count is always `projects × locations`, so each region you add multiplies the size of the discovery result by the number of accessible projects.

#### Service account credentials

The orchestrator authenticates exclusively via Application Default Credentials. Two supported deployment modes:

- **Inside GCP** - on a GCE VM or GKE pod with the service account attached via workload identity. ADC discovers the service account from the metadata server automatically. No host configuration needed.
- **Outside GCP** - on a Windows host or on-prem Linux. Set the `GOOGLE_APPLICATION_CREDENTIALS` machine-level environment variable to the absolute path of the service account's JSON key, then restart the Keyfactor Orchestrator service so it picks up the variable. The account that runs the orchestrator service must have read access to the JSON key file.

The legacy `Service Account Key File Path` custom store property (a JSON filename relative to the orchestrator extension directory) is **deprecated as of v1.2** because the Discovery job has no way to surface custom store properties in Keyfactor Command's discovery-job UI - so file-based auth can't be configured uniformly across all four job types. v1.1 stores with the property populated continue to work, but every job run logs a deprecation warning. The field is scheduled for removal in v2.0; new stores should leave it blank.

The service account needs at minimum:

- `roles/browser` at the **organization** root - for `projects.search` to see projects nested in folders.
- `roles/certificatemanager.viewer` per project (or at the org root for inheritance) - for inventory to list certificates.
- `roles/certificatemanager.editor` - for management to add/remove certificates.

Required APIs to enable in the **service account's home project**:

- Cloud Resource Manager API
- Certificate Manager API (also needs to be enabled in every project you actually inventory)

### Certificate alias rules

GCP Certificate Manager constrains certificate resource IDs to a strict shape:

- 1 to 63 characters
- Lowercase letters, digits, hyphens only
- Must start with a lowercase letter
- Must not end with a hyphen
- Regex: `[a-z]([-a-z0-9]*[a-z0-9])?`

The orchestrator validates the alias against this rule **before** any API calls or PFX parsing during Management/Add. A non-conforming alias fails fast with a `[FAIL] ValidateAlias` step in the flow trace and a suggestion of a normalized alias (e.g. `Cert1` → `cert1`). Rename the certificate in Keyfactor Command to the suggested form and retry the Management/Add job.

### Certificate scope

GCP Certificate Manager attaches a `scope` to every certificate that determines which load balancer / service families can consume it. The `Scope` custom store property tells the orchestrator which value to pass to GCP on create.

| Value | What it is for |
|---|---|
| `DEFAULT` | Global external Application Load Balancers - the standard GCP load balancer for internet-facing traffic. This is the GCP default and the right answer for most stores. |
| `ALL_REGIONS` | Cross-region **internal** Application Load Balancers. Use this when the consuming load balancer is regional/internal and replicated across regions. |
| `EDGE_CACHE` | Google Cloud Media CDN edge-cache certs. |
| `CLIENT_AUTH` | Certificates used by mTLS trust configs, or server certificates that are authorized for client authentication. |

#### Immutability

The `scope` field is **create-only** in the GCP API. Once GCP creates a certificate with a given scope, that scope cannot be changed by any patch operation. The orchestrator's Management/Replace path uses `UpdateMask = "SelfManaged"`, so re-adding a certificate over an existing one preserves its original scope - even if the store's Scope property has changed. To migrate a certificate to a different scope, delete it (Management/Remove) and re-add it with the new scope.

This is why the recommended deployment pattern is **one store per (project, location, scope) tuple**. The Scope property is store-wide, not per-cert.

#### What happened before v1.2.1

Prior to v1.2.1 the orchestrator hard-coded `Scope = "DEFAULT"` on every certificate it created. Customers who needed non-default scopes (typically `ALL_REGIONS` for cross-region internal ALBs) had to pre-create empty placeholder certificate resources in GCP via Terraform with `scope = "ALL_REGIONS"`, then point Keyfactor at the existing resource as a Replace target. The new property removes that workaround: a store with Scope = `ALL_REGIONS` will create new certificate resources directly at the right scope.

#### How the orchestrator validates the value

`JobBase.ResolveScope` runs as the `ResolveScope` flow step on every Management/Add. It trims and uppercases the configured value, then validates it against the set GCP accepts. An unsupported value (typo, lowercase letters that don't normalize to a valid token, anything outside the four allowed values) fails with `[FAIL] ResolveScope` and a clear message naming the four legal values. Blank or null resolves to `DEFAULT`.

#### Quick reference

- "Where do I see what scope a certificate ended up with?" → GCP's Certificate Manager Console, or `gcloud certificate-manager certificates describe <name> --location=<loc> --project=<proj>`. The orchestrator's Inventory job does not surface scope today.
- "Can I change a certificate's scope?" → No. Delete and re-add.
- "I have one store with DEFAULT certs and I want to add an ALL_REGIONS cert" → Create a second store with the same (project, location) but Scope = `ALL_REGIONS`.

### Architecture and logging

Every job (Discovery, Inventory, Management) uses a shared `FlowLogger` to record step-by-step progress with timing. The flow summary is appended to `JobResult.FailureMessage` on **both** success and failure paths so operators reading job history can see what happened without having to pull orchestrator-side trace logs. Errors arising from the GCP SDK are unwrapped through `AggregateException` walls and reported with HTTP status + the GCP error response body, so quota errors / IAM denials / malformed certificates surface clearly in Command's UI.

### Migrating v1.1 stores

A v1.1-shape store has `Store Path` empty or `n/a`, `Client Machine` set to the GCP Project ID, the `Location` custom property set to the region, and possibly the `Service Account Key File Path` custom property pointing at a JSON key in the orchestrator extension directory. These continue to work in v1.2 through fallback paths, but every inventory/management run logs deprecation warnings naming the store. To migrate, edit each affected store:

1. Set **Store Path** to `projects/{the-current-Client-Machine-value}/locations/{the-current-Location-value}`.
2. Optionally change **Client Machine** to the GCP Organization ID for cleaner UI grouping.
3. Optionally clear the **Location** field (no longer required).
4. Configure ADC on the orchestrator host (see "Service account credentials") and clear the **Service Account Key File Path** field.
5. Save.

The deprecation warnings will stop on the next job run once the store is fully migrated. Both fallbacks will be removed in v2.0.

### Design rationale: why Store Path is the source of truth

In v1.1 the orchestrator built the GCP resource path from **Client Machine** (= GCP Project ID) + the **Location** custom property, with **Store Path** unused (defaulted to `n/a`). Adding Discovery in v1.2 forced this model to change. Here's why.

The Keyfactor `IDiscoveryJobExtension` contract emits a plain `List<string>` of discovered locations - there is no hook to set per-candidate Client Machine values. When an operator approves a discovered candidate (in the per-candidate edit dialog with `Create Certificate Store If Missing` checked), Keyfactor Command creates the new store with:

- Store Path = the discovered location string (e.g. `projects/edgecerts/locations/global`)
- Client Machine = whatever Command auto-populated on the discovery job (typically the orchestrator hostname) - one value shared across every candidate, *not* something the operator can set per-candidate
- Custom properties = their store-type defaults

Under the v1.1 model that meant every Discovery-approved store ended up with the *same* Client Machine across every project in the organization, which is wrong: each store needs its project ID to make GCP API calls. The first time inventory ran against a Discovery-approved store, that's exactly what produced an `HTTP 403 CONSUMER_INVALID` error against `projects/<orchestrator-hostname>/locations/global` - GCP correctly saying "that's not a valid project ID."

#### Alternatives considered

| Option | Why we didn't pick it |
|---|---|
| Force the operator to manually edit Client Machine after every Discovery approval | Friction. Discovery should produce working stores without an extra editing step per candidate. |
| One discovery job per project (so each job's Client Machine = that project's ID) | Impractical: an organization with 100 projects would need 100 discovery jobs, each independently configured and scheduled. |
| Have Discovery POST stores directly via Keyfactor Command's REST API instead of the standard `SubmitDiscoveryUpdate` callback | Non-standard pattern, much larger code surface, and diverges from how every other Keyfactor orchestrator works - making this orchestrator harder to maintain alongside the rest of the Keyfactor extension catalog. |
| **Make Store Path the canonical source for both manual and Discovery flows** | Picked. The discovered storepath already encodes both project and location, so reading it directly (instead of reconstructing from Client Machine + Location) means Discovery-approved stores work with zero operator edits, and manually-created stores configure the same way. Smallest code change for the cleanest user-facing schema. |

#### Trade-offs we accepted

- **Client Machine is now a display label**, not load-bearing. Some other Keyfactor orchestrators use Client Machine as a literal target host; for GCP that does not fit, because the orchestrator talks to a single GCP API endpoint regardless of which project a store targets - there is no per-store host to put there. The recommended value (GCP Organization ID) at least groups GCP stores together usefully in Command's UI.
- **The Location custom property is deprecated, not removed**. Keeping it in the manifest with `Required: false` preserves v1.1 stores' UI rendering during the transition. The fallback path in `JobBase.ResolveGcpResourcePath` reads it for v1.1-shaped stores (Store Path blank or `n/a`) and emits a `LogWarning` each time naming the migration step. Removal is scheduled for v2.0.
- **The Service Account Key File Path custom property is deprecated, not removed**, for the same backwards-compatibility reason. Authentication consolidates around Application Default Credentials, the GCP-recommended pattern, which works uniformly across all four job types - the deprecated property only ever worked for Inventory/Management because Discovery's UI doesn't expose store-type custom properties. Removal is scheduled for v2.0.

### Vendor docs

- [Google Cloud Certificate Manager](https://cloud.google.com/certificate-manager/docs)
- [Cloud Resource Manager v3 - projects.search](https://cloud.google.com/resource-manager/reference/rest/v3/projects/search)
- [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials)
