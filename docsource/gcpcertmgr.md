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

Discovered store paths arrive in Keyfactor Command in the form `projects/{projectId}/locations/{location}`. As of v1.2.0 the inventory and management jobs read the GCP resource path **from `StorePath` when it is in this canonical form**, so Discovery-approved stores work end-to-end without operators having to retype the project ID into `ClientMachine` after approval. The only field that still needs a value is the **Service Account Key File Path** custom property:

1. (Optional) Set the **Service Account Key File Path** to the JSON key filename in the orchestrator extension directory. Leave blank to use Application Default Credentials.
2. Approve.

`ClientMachine` and the `Location` custom property are still respected for **manually-created** stores (where `StorePath` is left as `n/a`) - that's the v1.1 shape and continues to work unchanged. For Discovery-approved stores those fields are advisory only; the canonical `StorePath` wins.

After approval the store is treated like any other `GcpCertMgr` store - the inventory job will run against it on its configured schedule.

### Architecture and logging

Every job (Discovery, Inventory, Management) uses a shared `FlowLogger` to record step-by-step progress with timing. The flow summary is appended to `JobResult.FailureMessage` on **both** success and failure paths so operators reading job history can see what happened without having to pull orchestrator-side trace logs. Errors arising from the GCP SDK are unwrapped through `AggregateException` walls and reported with HTTP status + the GCP error response body, so quota errors / IAM denials / malformed certificates surface clearly in Command's UI.

### Vendor docs

- [Google Cloud Certificate Manager](https://cloud.google.com/certificate-manager/docs)
- [Cloud Resource Manager v3 - projects.search](https://cloud.google.com/resource-manager/reference/rest/v3/projects/search)
- [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials)
