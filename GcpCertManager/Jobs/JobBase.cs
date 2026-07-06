// Copyright 2026 Keyfactor
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Google;
using Keyfactor.Orchestrators.Common.Enums;
using Keyfactor.Orchestrators.Extensions;
using Keyfactor.Orchestrators.Extensions.Interfaces;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager.Jobs
{
    /// <summary>
    /// Shared plumbing for GCP Certificate Manager orchestrator jobs. Provides PAM
    /// resolution with warn-on-empty fallback, JobResult helpers that append the
    /// <see cref="FlowLogger"/> summary, and exception unwrapping that surfaces
    /// <see cref="GoogleApiException"/> details (HTTP status + response body).
    /// </summary>
    public abstract class JobBase
    {
        protected ILogger Logger;
        protected readonly IPAMSecretResolver Resolver;

        protected JobBase(IPAMSecretResolver resolver)
        {
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>
        /// Resolves a PAM-eligible field. Returns the value as-is when it is null/empty
        /// (with a warning), otherwise hands it to the PAM resolver. This avoids passing
        /// empty strings into PAM providers which often misinterpret them as keys.
        /// </summary>
        protected string ResolvePamField(string name, string value)
        {
            Logger.LogTrace("Attempting to resolve PAM eligible field {FieldName}", name);
            if (string.IsNullOrWhiteSpace(value))
            {
                Logger.LogWarning("PAM field {FieldName} has a null/empty value, returning as-is.", name);
                return value;
            }
            return Resolver.Resolve(value);
        }

        protected static JobResult SuccessResult(long jobHistoryId, string message = "")
        {
            return new JobResult
            {
                Result = OrchestratorJobStatusJobResult.Success,
                JobHistoryId = jobHistoryId,
                FailureMessage = message ?? ""
            };
        }

        protected static JobResult WarningResult(long jobHistoryId, string message)
        {
            return new JobResult
            {
                Result = OrchestratorJobStatusJobResult.Warning,
                JobHistoryId = jobHistoryId,
                FailureMessage = message ?? ""
            };
        }

        protected static JobResult FailureResult(long jobHistoryId, string message, FlowLogger flow = null)
        {
            var combined = message ?? "Unknown error";
            if (flow != null)
            {
                combined = $"{combined}\n\n{flow.GetSummary()}";
            }
            return new JobResult
            {
                Result = OrchestratorJobStatusJobResult.Failure,
                JobHistoryId = jobHistoryId,
                FailureMessage = combined
            };
        }

        /// <summary>
        /// Unwraps an exception chain and produces a human-readable description. When a
        /// <see cref="GoogleApiException"/> is anywhere in the chain (including inside an
        /// <see cref="AggregateException"/>), prefer its HTTP status + error response
        /// content over the generic <c>.Message</c> - operators need to see what GCP
        /// actually returned (quota errors, IAM denials, malformed certs, etc).
        /// </summary>
        protected static string DescribeException(Exception ex)
        {
            if (ex == null) return "Unknown error";

            var apiEx = FindGoogleApiException(ex);
            if (apiEx != null)
            {
                var body = string.IsNullOrWhiteSpace(apiEx.Error?.ErrorResponseContent)
                    ? string.Empty
                    : $" - body: {Trim(apiEx.Error.ErrorResponseContent, 500)}";
                return $"GCP API error: HTTP {(int)apiEx.HttpStatusCode} {apiEx.HttpStatusCode}{body}";
            }

            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                return agg.InnerExceptions[0].Message;
            }

            return ex.InnerException?.Message ?? ex.Message;
        }

        private static GoogleApiException FindGoogleApiException(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is GoogleApiException g) return g;
                if (cur is AggregateException agg)
                {
                    foreach (var inner in agg.InnerExceptions)
                    {
                        var found = FindGoogleApiException(inner);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        private static string Trim(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "...";
        }

        /// <summary>
        /// Resolve the GCP resource path (<c>projects/{projectId}/locations/{location}</c>)
        /// for a certificate store. As of v1.2 the canonical source is the store's
        /// <c>StorePath</c>; both Discovery-approved and manually-created stores set it
        /// to <c>projects/{projectId}/locations/{location}</c>. The fallback to
        /// ClientMachine + the Location custom property exists only to keep v1.1-shape
        /// stores (where StorePath is blank or <c>n/a</c>) working through an upgrade,
        /// and it logs a deprecation warning when it fires.
        /// </summary>
        protected string ResolveGcpResourcePath(string storePath, string projectId, string location)
        {
            if (!string.IsNullOrWhiteSpace(storePath))
            {
                var trimmed = storePath.Trim();
                // Reject the historical "n/a" placeholder before pattern-matching.
                if (!string.Equals(trimmed, "n/a", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.StartsWith("projects/", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.IndexOf("/locations/", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    return trimmed;
                }
            }

            // v1.1 fallback. Log a deprecation warning so operators reading orchestrator
            // logs (or running this in dev) know the store should be migrated to the v1.2
            // schema (set StorePath to projects/{projectId}/locations/{location}).
            Logger?.LogWarning(
                "Store is using v1.1-shape configuration (ClientMachine={ProjectId}, Location={Location}, StorePath blank or 'n/a'). " +
                "This is deprecated as of v1.2 and the fallback will be removed in v2.0. " +
                "Edit the store and set Store Path to 'projects/{ProjectId}/locations/{Location}' to migrate.",
                projectId, location, projectId, location);

            return $"projects/{projectId}/locations/{location}";
        }

        // GCP Certificate Manager certificate IDs must match Google's resource-id rule:
        //   [a-z]([-a-z0-9]*[a-z0-9])?    length 1..63
        // i.e. lowercase letter first, lowercase letters/digits/hyphens after, must not
        // end with a hyphen. The API rejects anything else with HTTP 400 INVALID_ARGUMENT.
        // Source: https://cloud.google.com/certificate-manager/docs/reference/rest/v1/projects.locations.certificates/create#path-parameters
        private static readonly Regex GcpCertificateIdPattern = new Regex(
            @"^[a-z]([-a-z0-9]{0,61}[a-z0-9])?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Throws <see cref="ArgumentException"/> when <paramref name="alias"/> is not a
        /// legal GCP Certificate Manager resource ID. Call this before doing any
        /// expensive PFX parsing or API work so the operator sees a clear error in the
        /// flow trace instead of a 400 wall-of-JSON from GCP.
        /// </summary>
        protected static void ValidateGcpCertificateId(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                throw new ArgumentException("Certificate alias is required.", nameof(alias));

            if (alias.Length > 63)
                throw new ArgumentException(
                    $"GCP Certificate Manager requires the alias to be 63 characters or fewer; got '{alias}' ({alias.Length} chars).",
                    nameof(alias));

            if (!GcpCertificateIdPattern.IsMatch(alias))
            {
                var suggestion = SuggestValidAlias(alias);
                throw new ArgumentException(
                    $"GCP Certificate Manager rejects the alias '{alias}'. Aliases must match [a-z]([-a-z0-9]*[a-z0-9])? - " +
                    $"start with a lowercase letter, contain only lowercase letters/digits/hyphens, and not end with a hyphen. " +
                    $"Try renaming the certificate in Keyfactor Command to '{suggestion}' and retry.",
                    nameof(alias));
            }
        }

        // GCP Certificate Manager's create-only Scope values. Anything else produces an
        // HTTP 400 INVALID_ARGUMENT from the create call, so validate up front and reject
        // typos with a clear message instead of letting them reach the API.
        // Source: https://cloud.google.com/certificate-manager/docs/reference/rest/v1/projects.locations.certificates#Certificate.Scope
        private static readonly System.Collections.Generic.HashSet<string> AllowedScopes =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                "DEFAULT",
                "EDGE_CACHE",
                "ALL_REGIONS",
                "CLIENT_AUTH"
            };

        /// <summary>
        /// Normalize the per-store <c>Scope</c> custom property to a value GCP will
        /// accept. Blank → <c>DEFAULT</c> (matches pre-v1.2 behavior, so unmigrated
        /// stores keep working). Other values are uppercased and validated against the
        /// set GCP allows; an unknown value throws <see cref="ArgumentException"/> so
        /// the operator sees a clear failure before any API call.
        /// </summary>
        protected static string ResolveScope(string configuredScope)
        {
            if (string.IsNullOrWhiteSpace(configuredScope)) return "DEFAULT";

            var normalized = configuredScope.Trim().ToUpperInvariant();
            if (!AllowedScopes.Contains(normalized))
            {
                throw new ArgumentException(
                    $"Unsupported Scope '{configuredScope}'. GCP Certificate Manager accepts only " +
                    "DEFAULT, EDGE_CACHE, ALL_REGIONS, or CLIENT_AUTH. Edit the store's Scope custom property and retry.",
                    nameof(configuredScope));
            }
            return normalized;
        }

        /// <summary>
        /// Parses the "labels" entry parameter - a comma delimited list of
        /// <c>key:value</c> pairs (e.g. <c>env:prod,team:pki</c>) - into a label map
        /// suitable for <see cref="Google.Apis.CertificateManager.v1.Data.Certificate.Labels"/>.
        /// Pairs are split on the first colon only, so values containing a colon (e.g. a
        /// URL) are preserved. Malformed pairs (no colon) are silently dropped rather than
        /// failing the job. Returns an empty dictionary for null/blank input.
        /// </summary>
        protected static IDictionary<string, string> ParseLabels(string labels)
        {
            if (string.IsNullOrWhiteSpace(labels)) return new Dictionary<string, string>();

            return labels.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split(new[] { ':' }, 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => (Key: parts[0].Trim(), Value: parts[1].Trim()))
                .Where(kv => kv.Key.Length > 0)
                .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        }

        /// <summary>
        /// Formats a GCP label map back into the same comma delimited <c>key:value</c>
        /// string shape consumed by <see cref="ParseLabels"/>, so Inventory can round-trip
        /// existing labels back into the "labels" entry parameter. Returns an empty string
        /// for a null/empty map.
        /// </summary>
        protected static string FormatLabels(IDictionary<string, string> labels)
        {
            if (labels == null || labels.Count == 0) return string.Empty;

            return string.Join(",", labels.Select(kv => $"{kv.Key}:{kv.Value}"));
        }

        private static string SuggestValidAlias(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return "cert";
            // Best-effort lowercase + replace illegal chars with '-' + trim leading non-letters and trailing hyphens.
            var lowered = alias.ToLowerInvariant();
            var chars = new System.Text.StringBuilder(lowered.Length);
            foreach (var c in lowered)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-') chars.Append(c);
                else chars.Append('-');
            }
            var s = chars.ToString().Trim('-');
            // Resource IDs must start with a letter.
            while (s.Length > 0 && !(s[0] >= 'a' && s[0] <= 'z')) s = s.Substring(1);
            if (s.Length == 0) return "cert";
            return s.Length > 63 ? s.Substring(0, 63).TrimEnd('-') : s;
        }
    }
}
