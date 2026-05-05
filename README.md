<h1 align="center" style="border-bottom: none">
    Google Cloud Provider Certificate Manager Universal Orchestrator Extension
</h1>

<p align="center">
  <!-- Badges -->
<img src="https://img.shields.io/badge/integration_status-production-3D1973?style=flat-square" alt="Integration Status: production" />
<a href="https://github.com/Keyfactor/gcp-certmanager-orchestrator/releases"><img src="https://img.shields.io/github/v/release/Keyfactor/gcp-certmanager-orchestrator?style=flat-square" alt="Release" /></a>
<img src="https://img.shields.io/github/issues/Keyfactor/gcp-certmanager-orchestrator?style=flat-square" alt="Issues" />
<img src="https://img.shields.io/github/downloads/Keyfactor/gcp-certmanager-orchestrator/total?style=flat-square&label=downloads&color=28B905" alt="GitHub Downloads (all assets, all releases)" />
</p>

<p align="center">
  <!-- TOC -->
  <a href="#support">
    <b>Support</b>
  </a>
  ·
  <a href="#installation">
    <b>Installation</b>
  </a>
  ·
  <a href="#license">
    <b>License</b>
  </a>
  ·
  <a href="https://github.com/orgs/Keyfactor/repositories?q=orchestrator">
    <b>Related Integrations</b>
  </a>
</p>

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



## Compatibility

This integration is compatible with Keyfactor Universal Orchestrator version 10.4.1 and later.

## Support
The Google Cloud Provider Certificate Manager Universal Orchestrator extension is supported by Keyfactor. If you require support for any issues or have feature request, please open a support ticket by either contacting your Keyfactor representative or via the Keyfactor Support Portal at https://support.keyfactor.com.

> If you want to contribute bug fixes or additional enhancements, use the **[Pull requests](../../pulls)** tab.

## Requirements & Prerequisites

Before installing the Google Cloud Provider Certificate Manager Universal Orchestrator extension, we recommend that you install [kfutil](https://github.com/Keyfactor/kfutil). Kfutil is a command-line tool that simplifies the process of creating store types, installing extensions, and instantiating certificate stores in Keyfactor Command.


**Google Cloud Configuration**

1. Read up on [Google Certificate Manager](https://cloud.google.com/certificate-manager/docs) and how it works.

2. Either a Google Service Account is needed with the following permissions (Note: Workload Identity Management Should be used but at the time of the writing it was not available in the .net library yet), or the virtual machine running the Keyfactor Orchestrator Service must reside within Google Cloud.
![](docsource/images/ServiceAccountSettings.gif)

3. The following Api Access is needed:
![](docsource/images/ApiAccessNeeded.gif)

4. If authenticating via service account, download the Json Credential file as shown below:
![](docsource/images/GoogleKeyJsonDownload.gif)


## GcpCertMgr Certificate Store Type

To use the Google Cloud Provider Certificate Manager Universal Orchestrator extension, you **must** create the GcpCertMgr Certificate Store Type. This only needs to happen _once_ per Keyfactor Command instance.



The `GcpCertMgr` store type represents a single (Project, Location) pair within Google Cloud Certificate Manager. The orchestrator manages self-managed certificates inside that container - listing them for inventory, uploading new PFX certificates, and deleting existing certificates by alias.

#### Configuration model (v1.2+)

Every `GcpCertMgr` store - whether Discovery-approved or manually created - identifies its target Certificate Manager instance through the **Store Path** field:

```
projects/{projectId}/locations/{location}
```

That single value carries both the GCP project and the location (region or `global`). Inventory and Management read it directly; **Client Machine** is a display label for grouping in Command's UI and is not parsed by the orchestrator.

##### Field semantics

| Field | What it carries | Read by |
|---|---|---|
| **Store Path** | Canonical GCP resource path: `projects/{projectId}/locations/{location}` | Inventory, Management, Discovery (emit) |
| **Client Machine** | Display label only. Recommended: GCP Organization ID (e.g. `1005564431893`). Not parsed. | UI grouping in Command |
| **Service Account Key File Path** (custom) | Filename of the JSON key in the orchestrator extension directory. Blank → Application Default Credentials. | Credential loader |
| **Location** (custom, *deprecated*) | v1.1 shape only. New stores leave it blank. Used as a fallback when Store Path is empty or `n/a`. | v1.1 fallback path; emits a deprecation warning when read |

##### Manually creating a store

Set:

- **Client Machine**: GCP Organization ID
- **Store Path**: `projects/{projectId}/locations/{location}` - e.g. `projects/edgecerts/locations/global`
- **Service Account Key File Path**: `kf-orchestrator.json` (or blank for ADC)
- **Location**: leave blank

##### Approving a Discovery-discovered store

Discovery emits one candidate per (project, location) pair in canonical form, so the only field you might want to set on approval is **Service Account Key File Path** (recommended: type the JSON filename for explicit control; leave blank to inherit ADC). Click SAVE without further edits.

If `Create Certificate Store If Missing` is checked on the discovery job, every candidate auto-approves with no operator review. Discovery sets Store Path correctly on each, so all auto-created stores are immediately usable.

#### Discovery job configuration

Discovery is configured against the GCP Certificate Manager store type and enumerates candidate stores across an entire GCP organization. It uses the Cloud Resource Manager v3 API (`projects.search`) to list every active project the orchestrator's service account can see, then emits one candidate store path per (project, location) combination.

| Field on the discovery-job form | What to put |
|---|---|
| **Client Machine** | The GCP Organization ID (e.g. `1005564431893`). Logged for traceability; not used as a query filter. |
| **Server Username / Server Password** | Not used. Leave blank - GCP authentication uses a service account, not username/password. |
| **Directories to search** | Comma-separated list of GCP locations (regions) to enumerate, e.g. `global,us-central1,europe-west1`. Leave blank to default to `global`. |

The candidate count is `projects × locations`, so be deliberate about how many regions you list - listing 8 regions for an org with 100 projects yields 800 candidate stores, most of which will be empty.

##### Service account credentials

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

#### Architecture and logging

Every job (Discovery, Inventory, Management) uses a shared `FlowLogger` to record step-by-step progress with timing. The flow summary is appended to `JobResult.FailureMessage` on **both** success and failure paths so operators reading job history can see what happened without having to pull orchestrator-side trace logs. Errors arising from the GCP SDK are unwrapped through `AggregateException` walls and reported with HTTP status + the GCP error response body, so quota errors / IAM denials / malformed certificates surface clearly in Command's UI.

#### Migrating v1.1 stores

A v1.1-shape store has `Store Path` empty or `n/a`, `Client Machine` set to the GCP Project ID, and the `Location` custom property set to the region. These continue to work in v1.2 through a fallback path, but every inventory/management run logs a deprecation warning naming the store. To migrate, edit each affected store:

1. Set **Store Path** to `projects/{the-current-Client-Machine-value}/locations/{the-current-Location-value}`.
2. Optionally change **Client Machine** to the GCP Organization ID for cleaner UI grouping.
3. Optionally clear the **Location** field (no longer required).
4. Save.

The deprecation warning will stop on the next job run once Store Path is populated. The fallback will be removed in v2.0.

#### Vendor docs

- [Google Cloud Certificate Manager](https://cloud.google.com/certificate-manager/docs)
- [Cloud Resource Manager v3 - projects.search](https://cloud.google.com/resource-manager/reference/rest/v3/projects/search)
- [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials)




#### Supported Operations

| Operation    | Is Supported                                                                                                           |
|--------------|------------------------------------------------------------------------------------------------------------------------|
| Add          | ✅ Checked        |
| Remove       | ✅ Checked     |
| Discovery    | ✅ Checked  |
| Reenrollment | 🔲 Unchecked |
| Create       | ✅ Checked     |

#### Store Type Creation

##### Using kfutil:
`kfutil` is a custom CLI for the Keyfactor Command API and can be used to create certificate store types.
For more information on [kfutil](https://github.com/Keyfactor/kfutil) check out the [docs](https://github.com/Keyfactor/kfutil?tab=readme-ov-file#quickstart)
   <details><summary>Click to expand GcpCertMgr kfutil details</summary>

   ##### Using online definition from GitHub:
   This will reach out to GitHub and pull the latest store-type definition
   ```shell
   # GCP Certificate Manager
   kfutil store-types create GcpCertMgr
   ```

   ##### Offline creation using integration-manifest file:
   If required, it is possible to create store types from the [integration-manifest.json](./integration-manifest.json) included in this repo.
   You would first download the [integration-manifest.json](./integration-manifest.json) and then run the following command
   in your offline environment.
   ```shell
   kfutil store-types create --from-file integration-manifest.json
   ```
   </details>


#### Manual Creation
Below are instructions on how to create the GcpCertMgr store type manually in
the Keyfactor Command Portal
   <details><summary>Click to expand manual GcpCertMgr details</summary>

   Create a store type called `GcpCertMgr` with the attributes in the tables below:

   ##### Basic Tab
   | Attribute | Value | Description |
   | --------- | ----- | ----- |
   | Name | GCP Certificate Manager | Display name for the store type (may be customized) |
   | Short Name | GcpCertMgr | Short display name for the store type |
   | Capability | GcpCertMgr | Store type name orchestrator will register with. Check the box to allow entry of value |
   | Supports Add | ✅ Checked | Check the box. Indicates that the Store Type supports Management Add |
   | Supports Remove | ✅ Checked | Check the box. Indicates that the Store Type supports Management Remove |
   | Supports Discovery | ✅ Checked | Check the box. Indicates that the Store Type supports Discovery |
   | Supports Reenrollment | 🔲 Unchecked |  Indicates that the Store Type supports Reenrollment |
   | Supports Create | ✅ Checked | Check the box. Indicates that the Store Type supports store creation |
   | Needs Server | 🔲 Unchecked | Determines if a target server name is required when creating store |
   | Blueprint Allowed | 🔲 Unchecked | Determines if store type may be included in an Orchestrator blueprint |
   | Uses PowerShell | 🔲 Unchecked | Determines if underlying implementation is PowerShell |
   | Requires Store Password | 🔲 Unchecked | Enables users to optionally specify a store password when defining a Certificate Store. |
   | Supports Entry Password | 🔲 Unchecked | Determines if an individual entry within a store can have a password. |

   The Basic tab should look like this:

   ![GcpCertMgr Basic Tab](docsource/images/GcpCertMgr-basic-store-type-dialog.png)

   ##### Advanced Tab
   | Attribute | Value | Description |
   | --------- | ----- | ----- |
   | Supports Custom Alias | Required | Determines if an individual entry within a store can have a custom Alias. |
   | Private Key Handling | Required | This determines if Keyfactor can send the private key associated with a certificate to the store. Required because IIS certificates without private keys would be invalid. |
   | PFX Password Style | Default | 'Default' - PFX password is randomly generated, 'Custom' - PFX password may be specified when the enrollment job is created (Requires the Allow Custom Password application setting to be enabled.) |

   The Advanced tab should look like this:

   ![GcpCertMgr Advanced Tab](docsource/images/GcpCertMgr-advanced-store-type-dialog.png)

   > For Keyfactor **Command versions 24.4 and later**, a Certificate Format dropdown is available with PFX and PEM options. Ensure that **PFX** is selected, as this determines the format of new and renewed certificates sent to the Orchestrator during a Management job. Currently, all Keyfactor-supported Orchestrator extensions support only PFX.

   ##### Custom Fields Tab
   Custom fields operate at the certificate store level and are used to control how the orchestrator connects to the remote target server containing the certificate store to be managed. The following custom fields should be added to the store type:

   | Name | Display Name | Description | Type | Default Value/Options | Required |
   | ---- | ------------ | ---- | --------------------- | -------- | ----------- |
   | Location | Location (deprecated) | **Deprecated in v1.2.** The GCP location is parsed from Store Path. Leave blank for new stores. v1.1-shape stores (where Store Path is blank or `n/a`) still read this value as a fallback; expect a deprecation warning in the orchestrator log when that path is used. | String |  | 🔲 Unchecked |
   | ServiceAccountKey | Service Account Key File Path | File name of the Google Cloud service account key (JSON) installed in the same folder as the orchestrator extension (e.g. `kf-orchestrator.json`). Leave blank to fall back to Application Default Credentials (typical when the orchestrator runs on a GCE VM / GKE pod with workload identity, or when `GOOGLE_APPLICATION_CREDENTIALS` is set as an environment variable on the orchestrator host). | String |  | 🔲 Unchecked |

   The Custom Fields tab should look like this:

   ![GcpCertMgr Custom Fields Tab](docsource/images/GcpCertMgr-custom-fields-store-type-dialog.png)


   ###### Location (deprecated)
   **Deprecated in v1.2.** The GCP location is parsed from Store Path. Leave blank for new stores. v1.1-shape stores (where Store Path is blank or `n/a`) still read this value as a fallback; expect a deprecation warning in the orchestrator log when that path is used.

   ![GcpCertMgr Custom Field - Location](docsource/images/GcpCertMgr-custom-field-Location-dialog.png)
   ![GcpCertMgr Custom Field - Location](docsource/images/GcpCertMgr-custom-field-Location-validation-options-dialog.png)



   ###### Service Account Key File Path
   File name of the Google Cloud service account key (JSON) installed in the same folder as the orchestrator extension (e.g. `kf-orchestrator.json`). Leave blank to fall back to Application Default Credentials (typical when the orchestrator runs on a GCE VM / GKE pod with workload identity, or when `GOOGLE_APPLICATION_CREDENTIALS` is set as an environment variable on the orchestrator host).

   ![GcpCertMgr Custom Field - ServiceAccountKey](docsource/images/GcpCertMgr-custom-field-ServiceAccountKey-dialog.png)
   ![GcpCertMgr Custom Field - ServiceAccountKey](docsource/images/GcpCertMgr-custom-field-ServiceAccountKey-validation-options-dialog.png)





   </details>

## Installation

1. **Download the latest Google Cloud Provider Certificate Manager Universal Orchestrator extension from GitHub.**

    Navigate to the [Google Cloud Provider Certificate Manager Universal Orchestrator extension GitHub version page](https://github.com/Keyfactor/gcp-certmanager-orchestrator/releases/latest). Refer to the compatibility matrix below to determine the asset should be downloaded. Then, click the corresponding asset to download the zip archive.

   | Universal Orchestrator Version | Latest .NET version installed on the Universal Orchestrator server | `rollForward` condition in `Orchestrator.runtimeconfig.json` | `gcp-certmanager-orchestrator` .NET version to download |
   | --------- | ----------- | ----------- | ----------- |
   | Older than `11.0.0` | | | `net6.0` |
   | Between `11.0.0` and `11.5.1` (inclusive) | `net6.0` | | `net6.0` |
   | Between `11.0.0` and `11.5.1` (inclusive) | `net8.0` | `Disable` | `net6.0` || Between `11.0.0` and `11.5.1` (inclusive) | `net8.0` | `LatestMajor` | `net8.0` |
   | `11.6` _and_ newer | `net8.0` | | `net8.0` | 

    Unzip the archive containing extension assemblies to a known location.

    > **Note** If you don't see an asset with a corresponding .NET version, you should always assume that it was compiled for `net6.0`.

2. **Locate the Universal Orchestrator extensions directory.**

    * **Default on Windows** - `C:\Program Files\Keyfactor\Keyfactor Orchestrator\extensions`
    * **Default on Linux** - `/opt/keyfactor/orchestrator/extensions`

3. **Create a new directory for the Google Cloud Provider Certificate Manager Universal Orchestrator extension inside the extensions directory.**

    Create a new directory called `gcp-certmanager-orchestrator`.
    > The directory name does not need to match any names used elsewhere; it just has to be unique within the extensions directory.

4. **Copy the contents of the downloaded and unzipped assemblies from __step 2__ to the `gcp-certmanager-orchestrator` directory.**

5. **Restart the Universal Orchestrator service.**

    Refer to [Starting/Restarting the Universal Orchestrator service](https://software.keyfactor.com/Core-OnPrem/Current/Content/InstallingAgents/NetCoreOrchestrator/StarttheService.htm).



> The above installation steps can be supplemented by the [official Command documentation](https://software.keyfactor.com/Core-OnPrem/Current/Content/InstallingAgents/NetCoreOrchestrator/CustomExtensions.htm?Highlight=extensions).



## Defining Certificate Stores



### Store Creation

#### Manually with the Command UI

<details><summary>Click to expand details</summary>

1. **Navigate to the _Certificate Stores_ page in Keyfactor Command.**

    Log into Keyfactor Command, toggle the _Locations_ dropdown, and click _Certificate Stores_.

2. **Add a Certificate Store.**

    Click the Add button to add a new Certificate Store. Use the table below to populate the **Attributes** in the **Add** form.

   | Attribute | Description                                             |
   | --------- |---------------------------------------------------------|
   | Category | Select "GCP Certificate Manager" or the customized certificate store name from the previous step. |
   | Container | Optional container to associate certificate store with. |
   | Client Machine | Display label for grouping certificate stores in Keyfactor Command. Recommended value is the GCP Organization ID (e.g. `1005564431893`); the orchestrator does not parse a project ID out of this field. The actual GCP project + location are read from Store Path. |
   | Store Path | Canonical GCP resource path in the form `projects/{projectId}/locations/{location}` (e.g. `projects/edgecerts/locations/global`). This is the single source of truth for which Certificate Manager instance the store targets. For Discovery-approved stores Keyfactor Command auto-fills this from the discovered candidate; for manually-created stores the operator types it directly. |
   | Orchestrator | Select an approved orchestrator capable of managing `GcpCertMgr` certificates. Specifically, one with the `GcpCertMgr` capability. |
   | Location | **Deprecated in v1.2.** The GCP location is parsed from Store Path. Leave blank for new stores. v1.1-shape stores (where Store Path is blank or `n/a`) still read this value as a fallback; expect a deprecation warning in the orchestrator log when that path is used. |
   | ServiceAccountKey | File name of the Google Cloud service account key (JSON) installed in the same folder as the orchestrator extension (e.g. `kf-orchestrator.json`). Leave blank to fall back to Application Default Credentials (typical when the orchestrator runs on a GCE VM / GKE pod with workload identity, or when `GOOGLE_APPLICATION_CREDENTIALS` is set as an environment variable on the orchestrator host). |

</details>



#### Using kfutil CLI

<details><summary>Click to expand details</summary>

1. **Generate a CSV template for the GcpCertMgr certificate store**

    ```shell
    kfutil stores import generate-template --store-type-name GcpCertMgr --outpath GcpCertMgr.csv
    ```
2. **Populate the generated CSV file**

    Open the CSV file, and reference the table below to populate parameters for each **Attribute**.

   | Attribute | Description |
   | --------- | ----------- |
   | Category | Select "GCP Certificate Manager" or the customized certificate store name from the previous step. |
   | Container | Optional container to associate certificate store with. |
   | Client Machine | Display label for grouping certificate stores in Keyfactor Command. Recommended value is the GCP Organization ID (e.g. `1005564431893`); the orchestrator does not parse a project ID out of this field. The actual GCP project + location are read from Store Path. |
   | Store Path | Canonical GCP resource path in the form `projects/{projectId}/locations/{location}` (e.g. `projects/edgecerts/locations/global`). This is the single source of truth for which Certificate Manager instance the store targets. For Discovery-approved stores Keyfactor Command auto-fills this from the discovered candidate; for manually-created stores the operator types it directly. |
   | Orchestrator | Select an approved orchestrator capable of managing `GcpCertMgr` certificates. Specifically, one with the `GcpCertMgr` capability. |
   | Properties.Location | **Deprecated in v1.2.** The GCP location is parsed from Store Path. Leave blank for new stores. v1.1-shape stores (where Store Path is blank or `n/a`) still read this value as a fallback; expect a deprecation warning in the orchestrator log when that path is used. |
   | Properties.ServiceAccountKey | File name of the Google Cloud service account key (JSON) installed in the same folder as the orchestrator extension (e.g. `kf-orchestrator.json`). Leave blank to fall back to Application Default Credentials (typical when the orchestrator runs on a GCE VM / GKE pod with workload identity, or when `GOOGLE_APPLICATION_CREDENTIALS` is set as an environment variable on the orchestrator host). |

3. **Import the CSV file to create the certificate stores**

    ```shell
    kfutil stores import csv --store-type-name GcpCertMgr --file GcpCertMgr.csv
    ```

</details>



> The content in this section can be supplemented by the [official Command documentation](https://software.keyfactor.com/Core-OnPrem/Current/Content/ReferenceGuide/Certificate%20Stores.htm?Highlight=certificate%20store).





## License

Apache License 2.0, see [LICENSE](LICENSE).

## Related Integrations

See all [Keyfactor Universal Orchestrator extensions](https://github.com/orgs/Keyfactor/repositories?q=orchestrator).