v1.2.0 - unreleased
- Added Discovery job (`CertStores.GcpCertMgr.Discovery`) that enumerates all
  GCP projects accessible to the orchestrator's service account and emits one
  candidate store path per (project, location) pair in canonical
  `projects/{projectId}/locations/{location}` form.
  - Discovery-job ClientMachine is interpreted as the GCP Organization ID for
    logging only; the actual project set is bounded by the service account's
    IAM bindings (the customer scopes that at the org root).
  - "Directories to search" is repurposed as a comma-separated list of GCP
    locations (regions); defaults to `global` when blank.
  - Service account credentials default to Application Default Credentials,
    matching the recommended deployment on a GCE VM / GKE pod with workload
    identity. An optional `ServiceAccountKey` JobProperty (file name relative
    to the orchestrator extension dir) is supported for parity with the
    inventory/management job configuration.
- Added `FlowLogger` and `JobBase` infrastructure shared across all three jobs
  (Inventory, Management, Discovery). FlowLogger captures step-by-step traces
  with timing, and the summary is appended to `JobResult.FailureMessage` on
  every job result so operators can see what happened from job history alone.
- Added typed exception unwrapping (`JobBase.DescribeException`) that surfaces
  `GoogleApiException` HTTP status + ErrorResponseContent through `AggregateException`
  walls instead of letting them be flattened into generic `.Message`s.
- Added PAM secret resolution support via `IPAMSecretResolver` injected into
  every job constructor. The existing `ServiceAccountKey` store property is a
  file path (not a secret) so PAM has no effect on it today, but the plumbing
  is in place for future PAM-eligible properties.
- Bumped `Keyfactor.Orchestrators.IOrchestratorJobExtensions` 0.6.0 → 0.7.0 to
  pick up `IPAMSecretResolver` (no breaking changes for existing job behavior).
- Added `Google.Apis.CloudResourceManager.v3` package to support the project
  enumeration that Discovery requires.

### Known limitations
- Discovery emits candidate store paths in canonical GCP form; on approval the
  operator must set the new store's ClientMachine to the project ID and the
  Location custom property to the region (the canonical path encodes both,
  but Keyfactor Command does not auto-populate them). This is documented in
  `docsource/gcpcertmgr.md`.
- The discovery-job ClientMachine field (Organization ID) is informational; if
  the service account has visibility into multiple organizations, Discovery
  will emit projects from all of them. Constrain at IAM if that's not desired.

v1.1.0
- Implemented dual build for .net6/8
- Converted README to use doctool

v1.0.2
- Initial Public Version
