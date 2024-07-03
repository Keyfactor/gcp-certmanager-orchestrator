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

The Google Cloud Provider (GCP) Certificate Manager Universal Orchestrator extension remotely manages certificates on the Google Cloud Platform Certificate Manager product. This extension facilitates three job types: Inventory, Management Add, and Management Remove. It supports adding certificates with private keys only.

In the context of the GCP Certificate Manager, certificates are used to secure communications and authenticate identities for various services and applications. The GCP Certificate Manager simplifies the process of provisioning, managing, and deploying SSL/TLS certificates.

Defined Certificate Stores of the Certificate Store Type represent a logical grouping or container of certificates that reside on the remote platform, in this case, the Google Cloud Platform. These Certificate Stores can include unbound certificates as well as certificates bound to existing map entries, enabling streamlined management of your certificates in the cloud environment.

## Compatibility

This integration is compatible with Keyfactor Universal Orchestrator version 10.4.1 and later.

## Support
The Google Cloud Provider Certificate Manager Universal Orchestrator extension is supported by Keyfactor for Keyfactor customers. If you have a support issue, please open a support ticket with your Keyfactor representative. If you have a support issue, please open a support ticket via the Keyfactor Support Portal at https://support.keyfactor.com. 
 
> To report a problem or suggest a new feature, use the **[Issues](../../issues)** tab. If you want to contribute actual bug fixes or proposed enhancements, use the **[Pull requests](../../pulls)** tab.

## Installation
Before installing the Google Cloud Provider Certificate Manager Universal Orchestrator extension, it's recommended to install [kfutil](https://github.com/Keyfactor/kfutil). Kfutil is a command-line tool that simplifies the process of creating store types, installing extensions, and instantiating certificate stores in Keyfactor Command.


1. Follow the [requirements section](docs/gcpcertmgr.md#requirements) to configure a Service Account and grant necessary API permissions.

    <details><summary>Requirements</summary>

    No requirements found



    </details>

2. Create Certificate Store Types for the Google Cloud Provider Certificate Manager Orchestrator extension. 

    * **Using kfutil**:

        ```shell
        # GCP Certificate Manager
        kfutil store-types create GcpCertMgr
        ```

    * **Manually**:
        * [GCP Certificate Manager](docs/gcpcertmgr.md#certificate-store-type-configuration)

3. Install the Google Cloud Provider Certificate Manager Universal Orchestrator extension.
    
    * **Using kfutil**: On the server that that hosts the Universal Orchestrator, run the following command:

        ```shell
        # Windows Server
        kfutil orchestrator extension -e gcp-certmanager-orchestrator@latest --out "C:\Program Files\Keyfactor\Keyfactor Orchestrator\extensions"

        # Linux
        kfutil orchestrator extension -e gcp-certmanager-orchestrator@latest --out "/opt/keyfactor/orchestrator/extensions"
        ```

    * **Manually**: Follow the [official Command documentation](https://software.keyfactor.com/Core-OnPrem/Current/Content/InstallingAgents/NetCoreOrchestrator/CustomExtensions.htm?Highlight=extensions) to install the latest [Google Cloud Provider Certificate Manager Universal Orchestrator extension](https://github.com/Keyfactor/gcp-certmanager-orchestrator/releases/latest).

4. Create new certificate stores in Keyfactor Command for the Sample Universal Orchestrator extension.

    * [GCP Certificate Manager](docs/gcpcertmgr.md#certificate-store-configuration)



## License

Apache License 2.0, see [LICENSE](LICENSE).

## Related Integrations

See all [Keyfactor Universal Orchestrator extensions](https://github.com/orgs/Keyfactor/repositories?q=orchestrator).