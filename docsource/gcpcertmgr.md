## Overview

The GCP Certificate Manager Certificate Store Type in Keyfactor Command enables the seamless management of SSL/TLS certificates on the Google Cloud Platform Certificate Manager product. This Certificate Store Type represents a logical grouping of certificates managed within the GCP environment. It allows for various management operations on these certificates, including Inventory, Management Add, and Management Remove jobs.

One of the critical capabilities of this Certificate Store Type is the support for adding certificates with private keys, facilitating more secure communications and authentication within the cloud platform. However, there are significant caveats to be aware of — the GCP Certificate Manager Certificate Store Type supports the replacement of both unbound certificates and certificates bound to existing map entries, but it does not support specifying map entry bindings when adding new certificates.

This Certificate Store Type leverages a Google Service Account for authentication, requiring specific permissions and API access. Depending on the setup, either a service account JSON credential file or a VM residing within Google Cloud is needed for proper authentication. Be sure to configure the required custom fields and settings as specified, to avoid potential issues and confusion during deployment.

By adhering to these guidelines and understanding the limitations and capabilities, users can effectively manage their certificates within the GCP Certificate Manager environment using Keyfactor Command.

## Requirements

No requirements found

