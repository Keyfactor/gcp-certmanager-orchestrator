using System.ComponentModel;
using Newtonsoft.Json;

namespace Keyfactor.Extensions.Orchestrator.GcpCertManager
{
    internal class StorePath
    {

        [JsonProperty("Location")]
        [DefaultValue("global")]
        public string Location { get; set; }
        [JsonProperty("Project Number")]
        public string ProjectNumber { get; set; }

    }

}