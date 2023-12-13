using System.ComponentModel;
using Newtonsoft.Json;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager
{
    internal class StoreProperties
    {
        [DefaultValue("global")]
        public string Location { get; set; }

        public string ProjectId { get; set; }

        public string JsonKey { get; set; }
    }
}