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
- The discovery-job ClientMachine field (Organization ID) is informational; if
  the service account has visibility into multiple organizations, Discovery
  will emit projects from all of them. Constrain at IAM if that's not desired.
- Discovery does not probe each (project, location) candidate to confirm the
  Certificate Manager API is enabled or that any certificates exist. Operators
  can leave `Create Certificate Store If Missing` checked to auto-approve every
  candidate and let dead-end stores fail their first inventory; or leave it
  unchecked and approve only the candidates they want to track.

### Changed
- Inventory and Management now derive the GCP resource path from the store's
  `StorePath` when it is in canonical `projects/{p}/locations/{l}` form.
  Discovery emits paths in this form, so Discovery-approved stores now work
  without operators needing to edit `ClientMachine`/`Location` after approval -
  whether `Create Certificate Store If Missing` was checked or not. Manually
  created v1.1-shaped stores (where `StorePath` is `n/a`) keep building the
  path from `ClientMachine` + the `Location` custom property, so existing
  stores continue to work unchanged. See `JobBase.ResolveGcpResourcePath`.

v1.1.0
- Implemented dual build for .net6/8
- Converted README to use doctool

v1.0.2
- Initial Public Version
