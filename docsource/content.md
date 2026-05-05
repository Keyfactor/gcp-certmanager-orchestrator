## Overview

The GCP Certificate Manager Orchestrator Extension remotely manages certificates on the Google Cloud Platform Certificate Manager Product.

This orchestrator extension implements four job types - Inventory, Management Add, Management Remove, and Discovery. It supports adding certificates with private keys only. The orchestrator supports the replacement of unbound certificates as well as certificates bound to existing map entries, but it does **not** support specifying map entry bindings when adding new certificates.

### Configuration model

Every `GcpCertMgr` store identifies its target Certificate Manager instance through the canonical GCP resource path in **Store Path**:

```
projects/{projectId}/locations/{location}
```

This applies equally to manually-created stores and Discovery-approved stores. The `Location` custom property is deprecated as of v1.2 and used only as a v1.1 backwards-compatibility fallback. **Client Machine** is a display label for grouping in Command's UI - the recommended value is the GCP Organization ID.

The Discovery job enumerates every GCP project that the orchestrator's service account can see and proposes one candidate store per (project, location) pair, with Store Path pre-populated in canonical form. The actual scope of discovery is bounded by IAM - grant the service account the appropriate role at the organization root and Discovery will return everything underneath. See the GCP Certificate Manager store-type page for full operator-facing details.


## GCP setup prerequisites

Before configuring the orchestrator, make sure your Google Cloud project is ready. Read the official [Google Certificate Manager](https://cloud.google.com/certificate-manager/docs) documentation for product background. The steps below are intentionally text-only; Google's Cloud Console UI changes regularly and the underlying APIs and `gcloud` commands are the stable interface.

### 1. Enable the required Google Cloud APIs

In the project that will host the orchestrator's service account ("the SA project"), enable both:

- **Cloud Resource Manager API** - lets the Discovery job enumerate projects via `projects.search`. Required even if you only use Inventory/Management today, because the API enablement check runs against the SA project regardless of what target project the call reads.
- **Certificate Manager API** - read/write access to certificate resources. This must additionally be enabled in **every project** you intend to inventory or manage certs in.

`gcloud` (one-shot for both APIs in the SA project):

```
gcloud services enable cloudresourcemanager.googleapis.com certificatemanager.googleapis.com --project=<sa-project-id>
```

### 2. Create a service account and grant organization-level roles

Service account credentials are *identity*, not authorization - the IAM bindings determine what the SA can see and do. Bind these roles **at the organization** so the SA inherits visibility into every folder and project:

| Role | Why |
|---|---|
| `roles/browser` | So `projects.search` returns projects nested in folders, not just top-level projects |
| `roles/certificatemanager.viewer` | Inventory: list certificates in each store |
| `roles/certificatemanager.editor` | Management/Add and Management/Remove |

```
gcloud iam service-accounts create kf-orchestrator \
    --project=<sa-project-id> \
    --display-name="Keyfactor Universal Orchestrator"

ORG=<organization-id>
SA=kf-orchestrator@<sa-project-id>.iam.gserviceaccount.com

gcloud organizations add-iam-policy-binding $ORG --member="serviceAccount:$SA" --role="roles/browser"
gcloud organizations add-iam-policy-binding $ORG --member="serviceAccount:$SA" --role="roles/certificatemanager.viewer"
gcloud organizations add-iam-policy-binding $ORG --member="serviceAccount:$SA" --role="roles/certificatemanager.editor"
```

### 3. Provide credentials to the orchestrator host (Application Default Credentials)

The orchestrator authenticates exclusively via [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials) (ADC). There are two supported deployment modes:

**Orchestrator runs inside GCP (recommended)** - on a GCE VM or GKE pod with the service account attached via workload identity. ADC discovers the service account from the metadata server automatically. No further configuration on the host.

**Orchestrator runs outside GCP** - on a Windows host, on-prem Linux, etc.:

1. Create a JSON key for the service account: `gcloud iam service-accounts keys create kf-orchestrator.json --iam-account=$SA`. Google never re-displays this key, so save it somewhere safe.
2. Copy the JSON key to a secured location on the orchestrator host. Lock down filesystem permissions so only the account that runs the Keyfactor Orchestrator service can read it.
3. Set the `GOOGLE_APPLICATION_CREDENTIALS` machine-level environment variable to the absolute path of the JSON key. Restart the Keyfactor Orchestrator service so it picks up the variable.

> **Note on the deprecated `Service Account Key File Path` store property.** Earlier versions of the orchestrator accepted a JSON filename in a per-store custom property and read the file from the orchestrator extension directory. That mechanism is deprecated in v1.2 because the Discovery job has no way to surface custom store properties in Keyfactor Command's discovery-job UI - so file-based auth can't be configured uniformly across all four job types. Existing v1.1 stores with the property populated continue to work, but every job run logs a deprecation warning. The field is scheduled for removal in v2.0.
