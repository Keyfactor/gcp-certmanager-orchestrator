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
| **Store Path** | Canonical GCP resource path: `projects/{projectId}/locations/{location}` | Inventory, Management, Discovery (emit) |
| **Client Machine** | Display label only. Recommended: GCP Organization ID (e.g. `1005564431893`). Not parsed. | UI grouping in Command |
| **Service Account Key File Path** (custom) | Filename of the JSON key in the orchestrator extension directory. Blank → Application Default Credentials. | Credential loader |
| **Location** (custom, *deprecated*) | v1.1 shape only. New stores leave it blank. Used as a fallback when Store Path is empty or `n/a`. | v1.1 fallback path; emits a deprecation warning when read |

#### Manually creating a store

Set:

- **Client Machine**: GCP Organization ID
- **Store Path**: `projects/{projectId}/locations/{location}` - e.g. `projects/edgecerts/locations/global`
- **Service Account Key File Path**: `kf-orchestrator.json` (or blank for ADC)
- **Location**: leave blank

#### Approving a Discovery-discovered store

Discovery emits one candidate per (project, location) pair in canonical form, so the only field you might want to set on approval is **Service Account Key File Path** (recommended: type the JSON filename for explicit control; leave blank to inherit ADC). Click SAVE without further edits.

If `Create Certificate Store If Missing` is checked on the discovery job, every candidate auto-approves with no operator review. Discovery sets Store Path correctly on each, so all auto-created stores are immediately usable.

### Discovery job configuration

Discovery is configured against the GCP Certificate Manager store type and enumerates candidate stores across an entire GCP organization. It uses the Cloud Resource Manager v3 API (`projects.search`) to list every active project the orchestrator's service account can see, then emits one candidate store path per (project, location) combination.

| Field on the discovery-job form | What to put |
|---|---|
| **Client Machine** | The GCP Organization ID (e.g. `1005564431893`). Logged for traceability; not used as a query filter. |
| **Server Username / Server Password** | Not used. Leave blank - GCP authentication uses a service account, not username/password. |
| **Directories to search** | Comma-separated list of GCP locations (regions) to enumerate, e.g. `global,us-central1,europe-west1`. Leave blank to default to `global`. |

The candidate count is `projects × locations`, so be deliberate about how many regions you list - listing 8 regions for an org with 100 projects yields 800 candidate stores, most of which will be empty.

#### Service account credentials

Both the discovery job and the inventory/management jobs resolve credentials in the same order:

1. If a `ServiceAccountKey` value is configured (custom store property for inventory/management; not exposed in the discovery-job UI - see env-var fallback below), the JSON key file with that name is read from the orchestrator extension directory.
2. Otherwise, `GoogleCredential.GetApplicationDefault()` is used. On Windows hosts this means setting `GOOGLE_APPLICATION_CREDENTIALS` as a machine-level environment variable to the absolute path of the JSON key, then restarting the Keyfactor Orchestrator service. On a GCE VM / GKE pod with workload identity, ADC works automatically.

The service account needs at minimum:

- `roles/browser` at the **organization** root - for `projects.search` to see projects nested in folders.
- `roles/certificatemanager.viewer` per project (or at the org root for inheritance) - for inventory to list certificates.
- `roles/certificatemanager.editor` - for management to add/remove certificates.

Required APIs to enable in the **service account's home project**:

- Cloud Resource Manager API
- Certificate Manager API (also needs to be enabled in every project you actually inventory)

### Architecture and logging

Every job (Discovery, Inventory, Management) uses a shared `FlowLogger` to record step-by-step progress with timing. The flow summary is appended to `JobResult.FailureMessage` on **both** success and failure paths so operators reading job history can see what happened without having to pull orchestrator-side trace logs. Errors arising from the GCP SDK are unwrapped through `AggregateException` walls and reported with HTTP status + the GCP error response body, so quota errors / IAM denials / malformed certificates surface clearly in Command's UI.

### Migrating v1.1 stores

A v1.1-shape store has `Store Path` empty or `n/a`, `Client Machine` set to the GCP Project ID, and the `Location` custom property set to the region. These continue to work in v1.2 through a fallback path, but every inventory/management run logs a deprecation warning naming the store. To migrate, edit each affected store:

1. Set **Store Path** to `projects/{the-current-Client-Machine-value}/locations/{the-current-Location-value}`.
2. Optionally change **Client Machine** to the GCP Organization ID for cleaner UI grouping.
3. Optionally clear the **Location** field (no longer required).
4. Save.

The deprecation warning will stop on the next job run once Store Path is populated. The fallback will be removed in v2.0.

### Vendor docs

- [Google Cloud Certificate Manager](https://cloud.google.com/certificate-manager/docs)
- [Cloud Resource Manager v3 - projects.search](https://cloud.google.com/resource-manager/reference/rest/v3/projects/search)
- [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials)
