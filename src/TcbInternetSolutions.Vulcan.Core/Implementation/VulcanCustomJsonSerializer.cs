﻿using Elasticsearch.Net;
using EPiServer.Core;
using EPiServer.ServiceLocation;
using Nest;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TcbInternetSolutions.Vulcan.Core.Implementation
{
    /// <summary>
    /// Serializer for Vulcan content
    /// </summary>
    public class VulcanCustomJsonSerializer : JsonNetSerializer
    {
        /// <summary>
        /// Ignored property mapping types
        /// </summary>
        protected static Type[] IgnoredPropertyTypes =
        {
            typeof(PropertyDataCollection),
            typeof(ContentArea),
            typeof(CultureInfo),
            typeof(IEnumerable<CultureInfo>),
            typeof(EPiServer.DataAbstraction.PageType),
            typeof(EPiServer.Framework.Blobs.Blob)
        };

        private static readonly Type ContentRefType = typeof(ContentReference);
        private static readonly Type IgnoreType = typeof(VulcanIgnoreAttribute);
        private readonly IEnumerable<IVulcanIndexingModifier> _vulcanModifiers;

        /// <summary>
        /// DI Constructor
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="modifiers"></param>
        /// <param name="settingsModifier"></param>
        public VulcanCustomJsonSerializer
            (
                IConnectionSettingsValues settings,
                IEnumerable<IVulcanIndexingModifier> modifiers,
                Action<JsonSerializerSettings, IConnectionSettingsValues> settingsModifier
            ) : base(settings, settingsModifier)
        {
            _vulcanModifiers = modifiers;
        }

        /// <summary>
        /// Sets custom converters for serialization, currently only supports ContentReference properties
        /// </summary>
        protected override IList<Func<Type, JsonConverter>> ContractConverters { get; } = new List<Func<Type, JsonConverter>>
        {
            checkType => ContentRefType.IsAssignableFrom(checkType) ? new Converters.ContentReferenceConverter() : null
        };

        /// <summary>
        /// Creates property mapping
        /// </summary>
        /// <param name="memberInfo"></param>
        /// <returns></returns>
        public override IPropertyMapping CreatePropertyMapping(MemberInfo memberInfo)
        {
            var propertyType = (memberInfo as PropertyInfo)?.PropertyType;
            // have to use Attribute.GetCustomAttributes, if types are proxied checking memberinfo directly is always false
            var isIgnored = Attribute.GetCustomAttributes(memberInfo, IgnoreType, true).Length > 0;

            if
            (
                isIgnored ||
                memberInfo.Name.Equals("PageName", StringComparison.OrdinalIgnoreCase) ||
                memberInfo.Name.Contains(".") ||
                memberInfo.MemberType == MemberTypes.Property &&
                (
                    IsSubclassOfRawGeneric(typeof(Injected<>), propertyType) ||
                    IgnoredPropertyTypes.Contains(propertyType) ||
                    memberInfo.Name.Equals("DefaultMvcController", StringComparison.OrdinalIgnoreCase))
                )
            {
                return new PropertyMapping { Ignore = true };
            }

            return base.CreatePropertyMapping(memberInfo);
        }

        /// <summary>
        /// Serialize data 
        /// </summary>
        /// <param name="data"></param>
        /// <param name="writableStream"></param>
        /// <param name="formatting"></param>
        public override void Serialize(object data, Stream writableStream, SerializationFormatting formatting = SerializationFormatting.Indented)
        {
            // ReSharper disable once MergeCastWithTypeCheck
            if (data is IndexDescriptor<IContent> descriptedData)
            {
                // write all but ending }
                CopyDataToStream(data, writableStream, formatting);
                var content = ((IIndexRequest<IContent>)descriptedData).Document;

                if (content != null && _vulcanModifiers != null)
                {
                    // try to inspect to see if a pipeline was enabled
                    var requestAccessor = (IRequest<IndexRequestParameters>)data;
                    var pipelineId = requestAccessor.RequestParameters?.GetQueryStringValue<string>("pipeline"); // returns null if key not found                    
                    var args = new VulcanIndexingModifierArgs(content, pipelineId);

                    foreach (var indexingModifier in _vulcanModifiers)
                    {
                        try
                        {
                            indexingModifier.ProcessContent(args);
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"{indexingModifier.GetType().FullName} failed to process content ID {content.ContentLink.ID} with name {content.Name}!", e);
                        }
                    }

                    // add separator for additional items if any
                    if (args.AdditionalItems.Any())
                    {
                        WriteToStream(" , ", writableStream);
                    }

                    // copy all but starting {
                    CopyDataToStream(args.AdditionalItems, writableStream, formatting, false);
                }
                else
                {
                    WriteToStream("}", writableStream); // add back closing
                }
            }
            else
            {
                base.Serialize(data, writableStream, formatting);
            }
        }

        private static bool IsSubclassOfRawGeneric(Type generic, Type toCheck)
        {
            while (toCheck != null && toCheck != typeof(object))
            {
                var cur = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
                if (generic == cur)
                {
                    return true;
                }
                toCheck = toCheck.BaseType;
            }
            return false;
        }

        private static void WriteToStream(string data, Stream writeableStream)
        {
            var streamWriter = new StreamWriter(writeableStream);

            streamWriter.Write(data);

            streamWriter.Flush();
        }

        //copies all but first or last byte
        private void CopyDataToStream(object data, Stream writableStream, SerializationFormatting formatting, bool trimLast = true)
        {
            var stream = new MemoryStream();
            base.Serialize(data, stream, formatting);
            stream.Seek(trimLast ? 0 : 1, SeekOrigin.Begin);
            var bytes = Convert.ToInt32(stream.Length);
            var buffer = new byte[32768];
            int read;

            if (trimLast)
                bytes--;

            while (bytes > 0 &&
                   (read = stream.Read(buffer, 0, Math.Min(buffer.Length, bytes))) > 0)
            {
                writableStream.Write(buffer, 0, read);
                bytes -= read;
            }

            stream.Flush();
        }
    }
}