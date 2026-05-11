v1.2.0 - unreleased
- Added Discovery job (`CertStores.GcpCertMgr.Discovery`) that enumerates all
  GCP projects accessible to the orchestrator's service account and emits one
  candidate store path per (project, location) pair in canonical
  `projects/{projectId}/locations/{location}` form.
  - The Schedule Discovery form in Keyfactor Command does not expose a
    Client Machine field for GCP - Command auto-populates it (typically the
    orchestrator hostname) and the orchestrator logs it for traceability
    only. The actual project set is bounded by the service account's IAM
    bindings, not by anything the operator types into the discovery form.
  - "Directories to search" is repurposed as a comma-separated list of GCP
    locations (regions). The Keyfactor Command UI requires this field be
    non-empty; the recommended value is just `global`. The orchestrator also
    falls back to `global` defensively if it ever receives a blank value via
    a non-UI submission path.
  - Service account credentials use Application Default Credentials, matching
    the recommended deployment on a GCE VM / GKE pod with workload identity,
    or the `GOOGLE_APPLICATION_CREDENTIALS` environment variable on the
    orchestrator host when running outside GCP.
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
- The discovery-job ClientMachine value is auto-populated by Keyfactor Command
  (typically the orchestrator hostname) and is not operator-configurable from
  the Schedule Discovery dialog for GCP. It is logged for traceability but
  never used by the orchestrator for filtering. If the service account has
  visibility into multiple GCP organizations, Discovery emits projects from
  all of them - constrain at IAM if that is not desired.
- Discovery does not probe each (project, location) candidate to confirm the
  Certificate Manager API is enabled or that any certificates exist. Operators
  can leave `Create Certificate Store If Missing` checked to auto-approve every
  candidate and let dead-end stores fail their first inventory; or leave it
  unchecked and approve only the candidates they want to track.

### Changed (schema unification)
- Unified the store-type schema so manually-created and Discovery-approved
  stores configure the same way. **Store Path** is now the single source of
  truth for which Certificate Manager instance the store targets, in canonical
  form `projects/{projectId}/locations/{location}`. Inventory and Management
  read the GCP resource path from this field for both flows. Full design
  rationale (constraint, alternatives considered, trade-offs accepted) lives
  in `docsource/gcpcertmgr.md` under "Design rationale: why Store Path is the
  source of truth".
- **Client Machine** is repurposed as a display-only label. The recommended
  value is the GCP Organization ID; the orchestrator does not parse a project
  ID out of it. Documented in the updated store-type description.
- The **Location** custom property is deprecated. New stores leave it blank;
  the value is parsed out of Store Path. The field remains in the manifest
  with `Required: false` and a deprecation note so existing v1.1 stores keep
  rendering correctly in Command's UI.

### Changed (authentication)
- Authentication consolidates around Application Default Credentials (ADC).
  This is the only credential mechanism that works uniformly across all four
  job types - the previous `Service Account Key File Path` custom store
  property was readable only by Inventory/Management because the Keyfactor
  Command discovery-job UI does not surface store-type custom properties.
  ADC works whether the orchestrator runs inside GCP (via workload identity
  / GCE metadata server) or outside GCP (via the
  `GOOGLE_APPLICATION_CREDENTIALS` environment variable on the orchestrator
  host).
- The `Service Account Key File Path` (`ServiceAccountKey`) custom store
  property is deprecated. New stores leave it blank. v1.1 stores that have
  it populated continue to work via a deprecation-logged fallback in
  `GcpCertificateManagerClient.LoadCredentials`. Removal is scheduled for
  v2.0.

### Fixed
- `integration-manifest.json` previously advertised `Create: true` under
  `SupportedOperations`, but `Management.cs` has only ever handled `Add` and
  `Remove` - a Create job from Keyfactor Command's UI fell through to the
  default case and returned `Invalid Management Operation`. The manifest now
  correctly reports `Create: false`. There is no meaningful semantic for
  Create on a GCP Certificate Manager store anyway: the (project, location)
  container is implicit in the GCP project, so there is no per-store
  "creation" the orchestrator can usefully perform. Operators who relied on
  scheduling Create jobs (and getting failures) should remove those job
  definitions; everyone else is unaffected.

### Removed (docs)
- Removed three Google Cloud Console screenshot GIFs from `docsource/` that
  documented the service-account creation, API enablement, and JSON-key
  download flows: `ServiceAccountSettings.gif`, `ApiAccessNeeded.gif`,
  `GoogleKeyJsonDownload.gif`. Replaced with verbal step-by-step
  instructions and `gcloud` commands in `docsource/content.md` so the docs
  do not go stale when Google redesigns the Console UI. The Keyfactor
  Command store-type dialog screenshots in `docsource/images/` are
  doctool-managed and remain.

### Added (validation)
- Pre-flight alias validation in Management/Add. The orchestrator now checks
  the certificate alias against GCP Certificate Manager's resource-ID rule
  (`[a-z]([-a-z0-9]*[a-z0-9])?`, max 63 chars) before doing any API work or
  PFX parsing. A non-conforming alias produces a clear `[FAIL] ValidateAlias`
  flow step with a suggested normalized form (e.g. `Cert1` → `cert1`),
  replacing the previous behavior of failing 700ms later with a wall-of-JSON
  HTTP 400 from GCP. See `JobBase.ValidateGcpCertificateId`.

### Added (Scope as entry parameter)
- Added a new `Scope` **entry parameter** (per-certificate, not per-store) that
  controls the GCP Certificate Manager `scope` value on each newly-created
  certificate. Previous releases hard-coded `Scope = "DEFAULT"` and never read
  scope back during Inventory, which made the orchestrator unusable for
  environments that depend on cross-region internal Application Load Balancers
  (`ALL_REGIONS`), Media CDN (`EDGE_CACHE`), or mTLS trust-config /
  authorized-client server certs (`CLIENT_AUTH`). Those customers had to
  pre-create empty placeholder certificates in GCP via Terraform with the right
  scope and then attach Keyfactor to the existing shell.
  - Modeled as `EntryParameters` (not `Properties`) because scope is per-cert
    in GCP - a single (project, location) container can legitimately hold
    certificates at different scopes. A single Keyfactor store can now hold a
    mix of scopes without needing one store per scope.
  - Rendered as a `MultipleChoice` dropdown in Command with the four allowed
    values pre-populated, so typos cannot reach the orchestrator.
  - Allowed values: `DEFAULT`, `ALL_REGIONS`, `EDGE_CACHE`, `CLIENT_AUTH`.
    `JobBase.ResolveScope` validates defence-in-depth: trims, uppercases,
    rejects anything not in the set with a `[FAIL] ResolveScope` flow step
    before any GCP API call.
  - Default is `DEFAULT`. Blank, null, or missing all resolve to `DEFAULT`,
    matching the pre-v1.2.1 behavior so existing stores upgrade with no
    operator action.
  - Inventory now reads each cert's actual `scope` from GCP's
    `certificates.list` response and writes it into the cert's
    `CurrentInventoryItem.Parameters` dict. GCP elides the field when the
    cert is at `DEFAULT`; the orchestrator normalizes null/blank to `DEFAULT`
    so Command always sees a concrete value. On subsequent renewals /
    reenrollments Keyfactor replays the inventoried value into
    `JobProperties`, so the cert keeps its scope through its lifecycle
    automatically.
  - GCP's Scope field is **create-only and immutable**. Replace (overwrite)
    paths do not change scope: the `Patch` call's `UpdateMask` is `SelfManaged`,
    so GCP only updates the cert/key bytes. To change a certificate's scope,
    delete the certificate and re-add it.

### Backwards compatibility
- v1.1-shape stores (Store Path blank or `n/a`, Client Machine = Project ID,
  Location custom property = region) continue to work via a deprecation-logged
  fallback path in `JobBase.ResolveGcpResourcePath`. Every inventory or
  management run against such a store emits a single `LogWarning` naming the
  store and the migration step. The fallback is scheduled for removal in v2.0.
- Migration: edit each affected store, set Store Path to
  `projects/{ClientMachine-value}/locations/{Location-value}`, optionally
  change Client Machine to the GCP Organization ID, optionally clear Location.

v1.1.0
- Implemented dual build for .net6/8
- Converted README to use doctool

v1.0.2
- Initial Public Version For Release
