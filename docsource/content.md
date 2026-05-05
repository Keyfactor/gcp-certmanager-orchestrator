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


## Requirements

**Google Cloud Configuration**

1. Read up on [Google Certificate Manager](https://cloud.google.com/certificate-manager/docs) and how it works.

2. Either a Google Service Account is needed with the following permissions (Note: Workload Identity Management Should be used but at the time of the writing it was not available in the .net library yet), or the virtual machine running the Keyfactor Orchestrator Service must reside within Google Cloud.
![](docsource/images/ServiceAccountSettings.gif)

3. The following Api Access is needed:
![](docsource/images/ApiAccessNeeded.gif)

4. If authenticating via service account, download the Json Credential file as shown below:
![](docsource/images/GoogleKeyJsonDownload.gif)