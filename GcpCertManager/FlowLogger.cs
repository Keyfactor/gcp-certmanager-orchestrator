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
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager
{
    public class FlowLogger : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _flowName;
        private readonly Stopwatch _overallStopwatch;
        private readonly List<FlowStep> _steps = new List<FlowStep>();
        private readonly Stack<string> _branchStack = new Stack<string>();

        public FlowLogger(ILogger logger, string flowName)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _flowName = flowName ?? throw new ArgumentNullException(nameof(flowName));
            _overallStopwatch = Stopwatch.StartNew();
            _logger.LogTrace("[FLOW:{FlowName}] === BEGIN ===", _flowName);
        }

        public void Step(string name, string detail = null)
        {
            var step = new FlowStep { Name = name, Detail = detail, Status = StepStatus.Success };
            _steps.Add(step);
            var prefix = GetPrefix();
            if (detail != null)
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[OK] {StepName} - {Detail}", _flowName, prefix, name, detail);
            else
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[OK] {StepName}", _flowName, prefix, name);
        }

        public void Step(string name, Action action, string detail = null)
        {
            var sw = Stopwatch.StartNew();
            var step = new FlowStep { Name = name, Detail = detail };
            try
            {
                action();
                sw.Stop();
                step.Status = StepStatus.Success;
                step.ElapsedMs = sw.ElapsedMilliseconds;
                _steps.Add(step);
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[OK] {StepName} ({Elapsed}ms){DetailSuffix}",
                    _flowName, prefix, name, sw.ElapsedMilliseconds, FormatDetail(detail));
            }
            catch (Exception ex)
            {
                sw.Stop();
                step.Status = StepStatus.Failed;
                step.ElapsedMs = sw.ElapsedMilliseconds;
                step.ErrorMessage = ex.Message;
                _steps.Add(step);
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[FAIL] {StepName} ({Elapsed}ms) - {Error}",
                    _flowName, prefix, name, sw.ElapsedMilliseconds, ex.Message);
                throw;
            }
        }

        public async Task StepAsync(string name, Func<Task> action, string detail = null)
        {
            var sw = Stopwatch.StartNew();
            var step = new FlowStep { Name = name, Detail = detail };
            try
            {
                await action();
                sw.Stop();
                step.Status = StepStatus.Success;
                step.ElapsedMs = sw.ElapsedMilliseconds;
                _steps.Add(step);
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[OK] {StepName} ({Elapsed}ms){DetailSuffix}",
                    _flowName, prefix, name, sw.ElapsedMilliseconds, FormatDetail(detail));
            }
            catch (Exception ex)
            {
                sw.Stop();
                step.Status = StepStatus.Failed;
                step.ElapsedMs = sw.ElapsedMilliseconds;
                step.ErrorMessage = ex.Message;
                _steps.Add(step);
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[FAIL] {StepName} ({Elapsed}ms) - {Error}",
                    _flowName, prefix, name, sw.ElapsedMilliseconds, ex.Message);
                throw;
            }
        }

        public T Step<T>(string name, Func<T> action, string detail = null)
        {
            var sw = Stopwatch.StartNew();
            var step = new FlowStep { Name = name, Detail = detail };
            try
            {
                var result = action();
                sw.Stop();
                step.Status = StepStatus.Success;
                step.ElapsedMs = sw.ElapsedMilliseconds;
                _steps.Add(step);
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[OK] {StepName} ({Elapsed}ms){DetailSuffix}",
                    _flowName, prefix, name, sw.ElapsedMilliseconds, FormatDetail(detail));
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                step.Status = StepStatus.Failed;
                step.ElapsedMs = sw.ElapsedMilliseconds;
                step.ErrorMessage = ex.Message;
                _steps.Add(step);
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[FAIL] {StepName} ({Elapsed}ms) - {Error}",
                    _flowName, prefix, name, sw.ElapsedMilliseconds, ex.Message);
                throw;
            }
        }

        public void Fail(string name, string reason)
        {
            var step = new FlowStep { Name = name, Status = StepStatus.Failed, ErrorMessage = reason };
            _steps.Add(step);
            var prefix = GetPrefix();
            _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[FAIL] {StepName} - {Reason}", _flowName, prefix, name, reason);
        }

        public void Skip(string name, string reason)
        {
            var step = new FlowStep { Name = name, Status = StepStatus.Skipped, Detail = reason };
            _steps.Add(step);
            var prefix = GetPrefix();
            _logger.LogTrace("[FLOW:{FlowName}] {Prefix}[SKIP] {StepName} - {Reason}", _flowName, prefix, name, reason);
        }

        public void Branch(string name)
        {
            _branchStack.Push(name);
            var prefix = GetPrefix();
            _logger.LogTrace("[FLOW:{FlowName}] {Prefix}>> {BranchName}", _flowName, prefix, name);
        }

        public void EndBranch()
        {
            if (_branchStack.Count > 0)
            {
                var name = _branchStack.Pop();
                var prefix = GetPrefix();
                _logger.LogTrace("[FLOW:{FlowName}] {Prefix}<< {BranchName}", _flowName, prefix, name);
            }
        }

        public bool HasFailures => _steps.Any(s => s.Status == StepStatus.Failed);

        public string GetSummary()
        {
            var hasFailures = HasFailures;
            var overallStatus = hasFailures ? "FAILED" : "OK";
            var total = _steps.Count;
            var succeeded = _steps.Count(s => s.Status == StepStatus.Success);
            var failed = _steps.Count(s => s.Status == StepStatus.Failed);
            var skipped = _steps.Count(s => s.Status == StepStatus.Skipped);
            var elapsed = _overallStopwatch.ElapsedMilliseconds;

            var sb = new StringBuilder();
            sb.AppendLine($"Flow: {_flowName}  [{overallStatus}]  Total: {elapsed}ms");
            sb.AppendLine($"Steps: {total} total, {succeeded} ok, {failed} failed, {skipped} skipped");
            sb.AppendLine("----------------------------------------");
            foreach (var step in _steps)
            {
                var icon = step.Status == StepStatus.Success ? "[OK]  "
                    : step.Status == StepStatus.Failed ? "[FAIL]"
                    : step.Status == StepStatus.Skipped ? "[SKIP]"
                    : "[...]";
                var time = step.ElapsedMs.HasValue ? $" ({step.ElapsedMs}ms)" : "";
                var detail = !string.IsNullOrEmpty(step.ErrorMessage)
                    ? $" - {step.ErrorMessage}"
                    : !string.IsNullOrEmpty(step.Detail)
                        ? $" - {step.Detail}"
                        : "";
                sb.AppendLine($"  {icon} {step.Name}{time}{detail}");
            }
            sb.Append("----------------------------------------");

            return sb.ToString();
        }

        public void Dispose()
        {
            _overallStopwatch.Stop();
            var summary = GetSummary();
            _logger.LogTrace("[FLOW:{FlowName}] === END ===\n{Summary}", _flowName, summary);
        }

        private string GetPrefix()
        {
            if (_branchStack.Count == 0) return "";
            return new string(' ', _branchStack.Count * 2) + "| ";
        }

        private static string FormatDetail(string detail)
        {
            return string.IsNullOrEmpty(detail) ? "" : $" - {detail}";
        }

        private enum StepStatus
        {
            Success,
            Failed,
            Skipped,
            InProgress
        }

        private class FlowStep
        {
            public string Name { get; set; }
            public string Detail { get; set; }
            public StepStatus Status { get; set; } = StepStatus.InProgress;
            public long? ElapsedMs { get; set; }
            public string ErrorMessage { get; set; }
        }
    }
}
