namespace Microsoft.Azure.IIoT.OpcUa.Edge.Publisher.Storage.Models {
    using Microsoft.Azure.IIoT.OpcUa.Edge.Publisher.Storage.Models.Data;
    using Microsoft.Azure.IIoT.OpcUa.Edge.Publisher.Storage.Models.Events;
    using System;
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    /// <summary>
    /// Contains the nodes which should be
    /// </summary>
    [DataContract]
    public class PublishedNodesEntryModel {
        /// <summary> The endpoint URL of the OPC UA server. </summary>
        [DataMember(IsRequired = true)]
        public Uri EndpointUrl { get; set; }

        /// <summary> Secure transport should be used to </summary>
        [DataMember(EmitDefaultValue = false)]
        public bool? UseSecurity { get; set; }

        /// <summary> The node to monitor in "ns=" syntax. </summary>
        [DataMember(EmitDefaultValue = false)]
        public NodeIdModel NodeId { get; set; }

        /// <summary> authentication mode </summary>
        [DataMember(EmitDefaultValue = false)]
        public OpcAuthenticationMode OpcAuthenticationMode { get; set; }

        /// <summary> encrypted username </summary>
        [DataMember(EmitDefaultValue = false)]
        public string EncryptedAuthUsername { get; set; }

        /// <summary> encrypted password </summary>
        [DataMember]
        public string EncryptedAuthPassword { get; set; }

        /// <summary> plain username </summary>
        [DataMember(EmitDefaultValue = false)]
        public string OpcAuthenticationUsername { get; set; }

        /// <summary> plain password </summary>
        [DataMember]
        public string OpcAuthenticationPassword { get; set; }

        /// <summary> Nodes defined in the collection. </summary>
        [DataMember(EmitDefaultValue = false)]
        public List<OpcDataNodeModel> OpcNodes { get; set; }

        /// <summary> Nodes defined in the collection. </summary>
        [DataMember(EmitDefaultValue = false)]
        public List<OpcEventNodeModel> OpcEvents { get; set; }
    }
}
