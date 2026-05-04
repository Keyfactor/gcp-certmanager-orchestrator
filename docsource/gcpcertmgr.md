## Overview

The `GcpCertMgr` store type represents a single (Project, Location) pair within Google Cloud Certificate Manager. The orchestrator manages self-managed certificates inside that container - listing them for inventory, uploading new PFX certificates, and deleting existing certificates by alias.

### Discovery

The Discovery job is configured against the GCP Certificate Manager store type and enumerates candidate stores across an entire GCP organization. It uses the Cloud Resource Manager v3 API (`projects.search`) to list every active project the orchestrator's service account can see, then emits one candidate store path per (project, location) combination in the canonical GCP form `projects/{projectId}/locations/{location}`.

#### Configuring the discovery job

| Field on the discovery-job form | What to put |
|---|---|
| **Client Machine** | The GCP Organization ID (e.g. `123456789012`). Recorded in logs only - actual project visibility is enforced by IAM. |
| **Server Username / Server Password** | Not used. Leave blank. The orchestrator authenticates via the configured GCP service account, not via username/password. |
| **Directories to search** | Comma-separated list of GCP locations (regions) to enumerate, e.g. `global,us-central1,europe-west1`. Leave blank to default to `global`. |

#### Service account credentials

Discovery resolves credentials in the same way the inventory and management jobs do:

1. If the optional `ServiceAccountKey` job property is provided, the JSON key file with that name is read from the orchestrator extension directory.
2. Otherwise, `GoogleCredential.GetApplicationDefault()` is used. This is the recommended path when the orchestrator runs on a GCE VM or GKE pod with a workload-identity-bound service account.

The service account needs at minimum the `resourcemanager.projects.list` permission at the organization root (or wherever you want discovery to be scoped). If you also want operators to be able to inventory the discovered stores immediately after approval, the same service account needs `certificatemanager.certificates.list` on those projects.

#### Approving discovered stores

Discovered store paths arrive in Keyfactor Command in the form `projects/{projectId}/locations/{location}`. **Command does not auto-populate `ClientMachine` or the `Location` custom property from the discovered path** - the operator must edit each candidate before approving:

1. Set **Client Machine** on the new store to the project ID (e.g. `my-pki-project`).
2. Set the **Location** custom property to the region (e.g. `global`).
3. Set the **Service Account Key File Path** custom property to the JSON key filename (or leave blank to use Application Default Credentials).
4. Approve.

After approval the store is treated like any other manually-created `GcpCertMgr` store - the inventory job will run against it on its configured schedule.

### Architecture and logging

Every job (Discovery, Inventory, Management) uses a shared `FlowLogger` to record step-by-step progress with timing. The flow summary is appended to `JobResult.FailureMessage` on **both** success and failure paths so operators reading job history can see what happened without having to pull orchestrator-side trace logs. Errors arising from the GCP SDK are unwrapped through `AggregateException` walls and reported with HTTP status + the GCP error response body, so quota errors / IAM denials / malformed certificates surface clearly in Command's UI.

### Vendor docs

- [Google Cloud Certificate Manager](https://cloud.google.com/certificate-manager/docs)
- [Cloud Resource Manager v3 - projects.search](https://cloud.google.com/resource-manager/reference/rest/v3/projects/search)
- [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials)
