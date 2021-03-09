﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.IIoT.OpcUa.Edge.Publisher.Models {
    using Microsoft.Azure.IIoT.OpcUa.Publisher.Models;
    using Microsoft.Azure.IIoT.OpcUa.Publisher;
    using Microsoft.Azure.IIoT.OpcUa.Protocol.Models;
    using Microsoft.Azure.IIoT.OpcUa.Core.Models;
    using Microsoft.Azure.IIoT.Crypto;
    using Microsoft.Azure.IIoT.Serializers;
    using Serilog;
    using System;
    using System.Runtime.Serialization;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Newtonsoft.Json;

    /// <summary>
    /// Published nodes
    /// </summary>
    public class PublishedNodesJobConverter {

        /// <summary>
        /// Create converter
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="serializer"></param>
        /// <param name="config"></param>
        /// <param name="cryptoProvider"></param>
        public PublishedNodesJobConverter(ILogger logger,
            IJsonSerializer serializer, IEngineConfiguration config = null,
            ISecureElement cryptoProvider = null) {
            _config = config;
            _cryptoProvider = cryptoProvider;
            _serializer = serializer ?? throw new ArgumentNullException(nameof(logger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Read monitored item job from reader
        /// </summary>
        /// <param name="publishedNodesFile"></param>
        /// <param name="legacyCliModel">The legacy command line arguments</param>
        /// <returns></returns>
        public IEnumerable<WriterGroupJobModel> Read(TextReader publishedNodesFile,
            LegacyCliModel legacyCliModel) {
            var sw = Stopwatch.StartNew();
            _logger.Debug("Reading published nodes file ({elapsed}", sw.Elapsed);
            var items = _serializer.Deserialize<List<PublishedNodesEntryModel>>(
                publishedNodesFile);
            _logger.Information(
                "Read {count} items from published nodes file in {elapsed}",
                items.Count, sw.Elapsed);
            sw.Restart();
            var jobs = ToWriterGroupJobs(items, legacyCliModel);
            _logger.Information("Converted items to jobs in {elapsed}", sw.Elapsed);
            return jobs;
        }

        /// <summary>
        /// Read monitored item job from reader
        /// </summary>
        /// <param name="items"></param>
        /// <param name="legacyCliModel">The legacy command line arguments</param>
        /// <returns></returns>
        internal IEnumerable<WriterGroupJobModel> ToWriterGroupJobs(
             IEnumerable<PublishedNodesEntryModel> items, LegacyCliModel legacyCliModel) {
            if (items == null) {
                return Enumerable.Empty<WriterGroupJobModel>();
            }
            try {
                IEnumerable<WriterGroupJobModel> writerGroupJobs = new List<WriterGroupJobModel>();
                writerGroupJobs = items
                    // Group by connection
                    .GroupBy(item => new ConnectionModel {
                        OperationTimeout = legacyCliModel.OperationTimeout,
                        Endpoint = new EndpointModel {
                            Url = item.EndpointUrl.OriginalString,
                            SecurityMode = item.UseSecurity == false &&
                                item.OpcAuthenticationMode != OpcAuthenticationMode.UsernamePassword ?
                                    SecurityMode.None : SecurityMode.Best
                        },
                        User = item.OpcAuthenticationMode != OpcAuthenticationMode.UsernamePassword ?
                                null : ToUserNamePasswordCredentialAsync(item).Result
                    },
                        // Select and batch nodes into published data set sources
                        item => GetModels(item, legacyCliModel.ScaleTestCount.GetValueOrDefault(1)),
                        // Comparer for connection information
                        new FuncCompare<ConnectionModel>((x, y) => x.IsSameAs(y)))
                    .Select(group => group
                        // Flatten all nodes for the same connection and group by publishing interval
                        // then batch in chunks for max 1000 nodes and create data sets from those.
                        .Flatten()
                        .GroupBy(n => n.OpcPublishingInterval)
                        .SelectMany(n => n
                            .Distinct((a, b) => {
                                if (a.Id != b.Id || a.DisplayName != b.DisplayName) {
                                    return false;
                                }
                                if (a is OpcNodeModel node1 && b is OpcNodeModel node2 && node1.OpcSamplingInterval != node2.OpcSamplingInterval) {
                                    return false;
                                }
                                return true;
                            })
                            .Batch(1000)) // NOTE: Probably batches things together...
                        // time to create the internal structure for events
                        .Select(opcEntities => new PublishedDataSetSourceModel {
                            Connection = group.Key.Clone(),
                            SubscriptionSettings = new PublishedDataSetSettingsModel {
                                PublishingInterval = GetPublishingIntervalFromNodes(opcEntities, legacyCliModel),
                                ResolveDisplayName = legacyCliModel.FetchOpcNodeDisplayName
                            },
                            // NOTE: Why not just combine?
                            PublishedVariables = new PublishedDataItemsModel {
                                    PublishedData = opcEntities
                                        .OfType<OpcNodeModel>()
                                        .Select(node => new PublishedDataSetVariableModel {
                                            // this is the monitored item id, not the nodeId!
                                            // Use the display name if any otherwisw the nodeId
                                            Id = string.IsNullOrEmpty(node.DisplayName)
                                                ? node.Id : node.DisplayName,
                                            PublishedVariableNodeId = node.Id,
                                            PublishedVariableDisplayName = node.DisplayName,
                                            SamplingInterval = node.OpcSamplingIntervalTimespan ??
                                                legacyCliModel.DefaultSamplingInterval,
                                            HeartbeatInterval = node.HeartbeatInterval.HasValue ?
                                                TimeSpan.FromSeconds(node.HeartbeatInterval.Value) :
                                                legacyCliModel.DefaultHeartbeatInterval,
                                            QueueSize = legacyCliModel.DefaultQueueSize,
                                            // TODO: skip first?
                                            // SkipFirst = opcNode.SkipFirst,
                                        }).ToList()
                                },
                            PublishedEvents = new PublishedEventItemsModel {
                                    PublishedEvents = opcEntities
                                        .OfType<OpcEventModel>()
                                        .Select(eventNotifier => new PublishedDataSetEventModel {
                                            Id = string.IsNullOrEmpty(eventNotifier.DisplayName) ? eventNotifier.Id : eventNotifier.DisplayName,
                                            EventNotifier = eventNotifier.Id, // NOTE: IMO should just be NodeId...
                                            SelectedFields = eventNotifier.SelectClauses.Select(selectedField => new SimpleAttributeOperandModel {
                                                NodeId = selectedField.TypeId,
                                                BrowsePath = selectedField.BrowsePaths.ToArray()
                                            }).ToList(),
                                            Filter = new ContentFilterModel {
                                                Elements = eventNotifier.WhereClauses.Select(whereClause => new ContentFilterElementModel {
                                                    FilterOperator = Enum.Parse<FilterOperatorType>(whereClause.Operator),
                                                    FilterOperands = whereClause.Operands.Select(filterOperand => new FilterOperandModel {
                                                        Value = filterOperand.Literal
                                                    }).ToList()
                                                }).ToList()
                                            },
                                            QueueSize = legacyCliModel.DefaultQueueSize, 
                                            // NOTE: BrowsePath, TriggerId, etc. huh?
                                        }).ToList()
                                },
                        }))
                    .Select(dataSetSourceBatches => new WriterGroupJobModel {
                        MessagingMode = legacyCliModel.MessagingMode,
                        Engine = _config == null ? null : new EngineConfigurationModel {
                            BatchSize = _config.BatchSize,
                            BatchTriggerInterval = _config.BatchTriggerInterval,
                            DiagnosticsInterval = _config.DiagnosticsInterval,
                            MaxMessageSize = _config.MaxMessageSize,
                            MaxOutgressMessages = _config.MaxOutgressMessages
                        },
                        WriterGroup = new WriterGroupModel {
                            MessageType = legacyCliModel.MessageEncoding,
                            WriterGroupId = $"{dataSetSourceBatches.First().Connection.Endpoint.Url}_" +
                                $"{new ConnectionIdentifier(dataSetSourceBatches.First().Connection)}",
                            DataSetWriters = dataSetSourceBatches.Select(dataSetSource => new DataSetWriterModel {
                                DataSetWriterId = $"{dataSetSource.Connection.Endpoint.Url}_" +
                                    $"{dataSetSource.GetHashSafe()}",
                                DataSet = new PublishedDataSetModel {
                                    DataSetSource = dataSetSource.Clone(),
                                },
                                DataSetFieldContentMask =
                                        DataSetFieldContentMask.StatusCode |
                                        DataSetFieldContentMask.SourceTimestamp |
                                        (legacyCliModel.FullFeaturedMessage ? DataSetFieldContentMask.ServerTimestamp : 0) |
                                        DataSetFieldContentMask.NodeId |
                                        DataSetFieldContentMask.DisplayName |
                                        DataSetFieldContentMask.ApplicationUri |
                                        (legacyCliModel.FullFeaturedMessage ? DataSetFieldContentMask.EndpointUrl : 0) |
                                        (legacyCliModel.FullFeaturedMessage ? DataSetFieldContentMask.ExtensionFields : 0),
                                MessageSettings = new DataSetWriterMessageSettingsModel() {
                                    DataSetMessageContentMask =
                                            (legacyCliModel.FullFeaturedMessage ? DataSetContentMask.Timestamp : 0) |
                                            DataSetContentMask.MetaDataVersion |
                                            DataSetContentMask.DataSetWriterId |
                                            DataSetContentMask.MajorVersion |
                                            DataSetContentMask.MinorVersion |
                                            (legacyCliModel.FullFeaturedMessage ? DataSetContentMask.SequenceNumber : 0)
                                }
                            }).ToList(),
                            MessageSettings = new WriterGroupMessageSettingsModel() {
                                NetworkMessageContentMask =
                                        NetworkMessageContentMask.PublisherId |
                                        NetworkMessageContentMask.WriterGroupId |
                                        NetworkMessageContentMask.NetworkMessageNumber |
                                        NetworkMessageContentMask.SequenceNumber |
                                        NetworkMessageContentMask.PayloadHeader |
                                        NetworkMessageContentMask.Timestamp |
                                        NetworkMessageContentMask.DataSetClassId |
                                        NetworkMessageContentMask.NetworkMessageHeader |
                                        NetworkMessageContentMask.DataSetMessageHeader
                            }
                        }
                    })
                    .ToList();
                return writerGroupJobs;
            }
            catch (Exception ex) {
                _logger.Error(ex, "failed to convert the published nodes.");
            }
            return Enumerable.Empty<WriterGroupJobModel>();
        }

        /// <summary>
        /// Get the node models from entry
        /// </summary>
        /// <param name="item"></param>
        /// <param name="scaleTestCount"></param>
        /// <returns></returns>
        private IEnumerable<BaseOpcNodeModel> GetModels(PublishedNodesEntryModel item,
            int scaleTestCount = 1) {
            if (item.OpcNodes != null) {
                foreach (var node in item.OpcNodes) {
                    if (string.IsNullOrEmpty(node.Id)) {
                        node.Id = node.ExpandedNodeId;
                    }
                    if (scaleTestCount == 1) {
                        yield return node;
                    }
                    else {
                        for (var i = 0; i < scaleTestCount; i++) {
                            yield return new OpcNodeModel {
                                Id = node.Id,
                                DisplayName = string.IsNullOrEmpty(node.DisplayName) ?
                                    $"{node.Id}_{i}" : $"{node.DisplayName}_{i}",
                                ExpandedNodeId = node.ExpandedNodeId,
                                HeartbeatInterval = node.HeartbeatInterval,
                                HeartbeatIntervalTimespan = node.HeartbeatIntervalTimespan,
                                OpcPublishingInterval = node.OpcPublishingInterval,
                                OpcPublishingIntervalTimespan = node.OpcPublishingIntervalTimespan,
                                OpcSamplingInterval = node.OpcSamplingInterval,
                                OpcSamplingIntervalTimespan = node.OpcSamplingIntervalTimespan,
                                SkipFirst = node.SkipFirst
                            };
                        }
                    }
                }
            }

            if (item.OpcEvents != null) {
                foreach (var node in item.OpcEvents) {
                    if (string.IsNullOrEmpty(node.Id)) {
                        node.Id = node.ExpandedNodeId;
                    }
                    if (scaleTestCount == 1) {
                        yield return node;
                    }
                    else {
                        for (var i = 0; i < scaleTestCount; i++) {
                            yield return new OpcEventModel {
                                Id = node.Id,
                                DisplayName = string.IsNullOrEmpty(node.DisplayName) ?
                                    $"{node.Id}_{i}" : $"{node.DisplayName}_{i}",
                                ExpandedNodeId = node.ExpandedNodeId,
                                OpcPublishingInterval = node.OpcPublishingInterval,
                                OpcPublishingIntervalTimespan = node.OpcPublishingIntervalTimespan,
                                SelectClauses = node.SelectClauses,
                                WhereClauses = node.WhereClauses,
                            };
                        }
                    }
                }
            }

            // NOTE: Why these? Just for the query... really?
            if (item.NodeId?.Identifier != null) {
                yield return new OpcNodeModel {
                    Id = item.NodeId.Identifier,
                };
            }
        }
        /// <summary>
        /// Extract publishing interval from nodes
        /// </summary>
        /// <param name="opcNodes"></param>
        /// <param name="legacyCliModel">The legacy command line arguments</param>
        /// <returns></returns>
        private static TimeSpan? GetPublishingIntervalFromNodes(IEnumerable<BaseOpcNodeModel> opcNodes,
            LegacyCliModel legacyCliModel) {
            var interval = opcNodes
                .FirstOrDefault(x => x.OpcPublishingInterval != null)?.OpcPublishingIntervalTimespan;
            return interval ?? legacyCliModel.DefaultPublishingInterval;
        }

        /// <summary>
        /// Convert to credential model
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        private async Task<CredentialModel> ToUserNamePasswordCredentialAsync(
            PublishedNodesEntryModel entry) {
            var user = entry.OpcAuthenticationUsername;
            var password = entry.OpcAuthenticationPassword;
            if (string.IsNullOrEmpty(user)) {
                if (_cryptoProvider == null || string.IsNullOrEmpty(entry.EncryptedAuthUsername)) {
                    return null;
                }

                const string kInitializationVector = "alKGJdfsgidfasdO"; // See previous publisher
                var userBytes = await _cryptoProvider.DecryptAsync(kInitializationVector,
                    Convert.FromBase64String(entry.EncryptedAuthUsername));
                user = Encoding.UTF8.GetString(userBytes);
                if (entry.EncryptedAuthPassword != null) {
                    var passwordBytes = await _cryptoProvider.DecryptAsync(kInitializationVector,
                        Convert.FromBase64String(entry.EncryptedAuthPassword));
                    password = Encoding.UTF8.GetString(passwordBytes);
                }
            }
            return new CredentialModel {
                Type = CredentialType.UserName,
                Value = _serializer.FromObject(new { user, password })
            };
        }

        /// <summary>
        /// Describing an entry in the node list
        /// </summary>
        [DataContract]
        public abstract class BaseOpcNodeModel {

            /// <summary> Node Identifier </summary>
            [DataMember(EmitDefaultValue = false)]
            public string Id { get; set; }

            /// <summary> Also </summary>
            [DataMember(EmitDefaultValue = false)]
            public string ExpandedNodeId { get; set; }

            /// <summary> Publishing interval </summary>
            [DataMember(EmitDefaultValue = false)]
            public int? OpcPublishingInterval { get; set; }

            /// <summary>
            /// OpcPublishingInterval as TimeSpan.
            /// </summary>
            [IgnoreDataMember]
            public TimeSpan? OpcPublishingIntervalTimespan {
                get => OpcPublishingInterval.HasValue ?
                    TimeSpan.FromMilliseconds(OpcPublishingInterval.Value) : (TimeSpan?)null;
                set => OpcPublishingInterval = value != null ?
                    (int)value.Value.TotalMilliseconds : (int?)null;
            }

            /// <summary> Display name </summary>
            [DataMember(EmitDefaultValue = false)]
            public string DisplayName { get; set; }
        }

        /// <summary>
        /// Describing an entry in the node list
        /// </summary>
        [DataContract]
        public class OpcNodeModel : BaseOpcNodeModel {

            /// <summary> Sampling interval </summary>
            [DataMember(EmitDefaultValue = false)]
            public int? OpcSamplingInterval { get; set; }

            /// <summary>
            /// OpcSamplingInterval as TimeSpan.
            /// </summary>
            [IgnoreDataMember]
            public TimeSpan? OpcSamplingIntervalTimespan {
                get => OpcSamplingInterval.HasValue ?
                    TimeSpan.FromMilliseconds(OpcSamplingInterval.Value) : (TimeSpan?)null;
                set => OpcSamplingInterval = value != null ?
                    (int)value.Value.TotalMilliseconds : (int?)null;
            }

            /// <summary> Heartbeat </summary>
            [DataMember(EmitDefaultValue = false)]
            public int? HeartbeatInterval { get; set; }

            /// <summary>
            /// Heartbeat interval as TimeSpan.
            /// </summary>
            [IgnoreDataMember]
            public TimeSpan? HeartbeatIntervalTimespan {
                get => HeartbeatInterval.HasValue ?
                    TimeSpan.FromSeconds(HeartbeatInterval.Value) : (TimeSpan?)null;
                set => HeartbeatInterval = value != null ?
                    (int)value.Value.TotalSeconds : (int?)null;
            }

            /// <summary> Skip first value </summary>
            [DataMember(EmitDefaultValue = false)]
            public bool? SkipFirst { get; set; }
        }

        /// <summary> OpcEventModel </summary>
        [DataContract]
        public class OpcEventModel : BaseOpcNodeModel {
            /// <summary>
            /// The SelectClauses used to select the fields which should be published for an event.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public List<SelectClauseModel> SelectClauses;

            /// <summary>
            /// The WhereClause to specify which events are of interest.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public List<WhereClauseElementModel> WhereClauses;
        }

        /// <summary>
        /// Node id serialized as object
        /// </summary>
        [DataContract]
        public class NodeIdModel {
            /// <summary> Identifier </summary>
            [DataMember(EmitDefaultValue = false)]
            public string Identifier { get; set; }
        }

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
            public List<OpcNodeModel> OpcNodes { get; set; }

            /// <summary> Nodes defined in the collection. </summary>
            [DataMember(EmitDefaultValue = false)]
            public List<OpcEventModel> OpcEvents { get; set; }
        }

        /// <summary>
        /// Class describing select clauses for an event filter.
        /// </summary>
        [DataContract]
        public class SelectClauseModel {
            /// <summary>
            /// The NodeId of the SimpleAttributeOperand.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public string TypeId;

            /// <summary>
            /// A list of QualifiedName's describing the field to be published.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public List<string> BrowsePaths;

            /// <summary>
            /// The Attribute of the identified node to be published. This is Value by default.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string AttributeId;

            /// <summary>
            /// The index range of the node values to be published.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string IndexRange;
        }

        /// <summary> WhereClauseElementModel </summary>
        [DataContract]
        public class WhereClauseElementModel {
            /// <summary>
            /// The Operator of the WhereClauseElement.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public string Operator;

            /// <summary>
            /// The Operands of the WhereClauseElement.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public List<WhereClauseOperandModel> Operands;
        }

        /// <summary> WhereClauseOperandModel </summary>
        [DataContract]
        public class WhereClauseOperandModel {
            /// <summary>
            /// Holds an element value.
            /// </summary>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore, NullValueHandling = NullValueHandling.Ignore)]
            public uint? Element;

            /// <summary>
            /// Holds an Literal value.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Literal;

            /// <summary>
            /// Holds an AttributeOperand value.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public FilterAttributeModel Attribute;

            /// <summary>
            /// Holds an SimpleAttributeOperand value.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public FilterSimpleAttributeModel SimpleAttribute;
        }

        /// <summary>
        /// Class to describe the SimpleAttributeOperand.
        /// </summary>
        [DataContract]
        public class FilterSimpleAttributeModel {
            /// <summary>
            /// The TypeId of the SimpleAttributeOperand.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public string TypeId;

            /// <summary>
            /// The browse path as a list of QualifiedName's of the SimpleAttributeOperand.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<string> BrowsePaths;

            /// <summary>
            /// The AttributeId of the SimpleAttributeOperand.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string AttributeId;

            /// <summary>
            /// The IndexRange of the SimpleAttributeOperand.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string IndexRange;
        }

        /// <summary>
        /// Class to describe the AttributeOperand.
        /// </summary>
        [DataContract]
        public class FilterAttributeModel {
            /// <summary>
            /// The NodeId of the AttributeOperand.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public string NodeId;

            /// <summary>
            /// The Alias of the AttributeOperand.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Alias;

            /// <summary>
            /// A RelativePath describing the browse path from NodeId of the AttributeOperand.
            /// </summary>
            [JsonProperty(Required = Required.Always)]
            public string BrowsePath;

            /// <summary>
            /// The AttibuteId of the AttributeOperand.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string AttributeId;


            /// <summary>
            /// The IndexRange of the AttributeOperand.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string IndexRange;
        }

        /// <summary>
        /// Enum that defines the authentication method
        /// </summary>
        [DataContract]
        public enum OpcAuthenticationMode {
            /// <summary> Anonymous authentication </summary>
            [EnumMember]
            Anonymous,
            /// <summary> Username/Password authentication </summary>
            [EnumMember]
            UsernamePassword
        }

        private readonly IEngineConfiguration _config;
        private readonly ISecureElement _cryptoProvider;
        private readonly IJsonSerializer _serializer;
        private readonly ILogger _logger;
    }
}
