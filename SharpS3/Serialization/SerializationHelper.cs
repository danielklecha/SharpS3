namespace SharpS3.Serialization;

using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using SharpS3.Models;

/// <summary>
/// Serialization helper.
/// Thread-safe; serializers are cached per type in concurrent dictionaries.
/// </summary>
public static class SerializationHelper
{
        #region Public-Members

        /// <summary>
        /// XML namespace Amazon S3 uses for response bodies: http://s3.amazonaws.com/doc/2006-03-01/.
        /// </summary>
        public const string S3XmlNamespace = "http://s3.amazonaws.com/doc/2006-03-01/";

        /// <summary>
        /// XML namespace Amazon S3 uses for bucket logging bodies (BucketLoggingStatus): http://doc.s3.amazonaws.com/2006-03-01.
        /// </summary>
        public const string S3LoggingXmlNamespace = "http://doc.s3.amazonaws.com/2006-03-01";

        #endregion

        #region Private-Members

        private static readonly string _ByteOrderMarkUtf8 = Encoding.UTF8.GetString(Encoding.UTF8.GetPreamble());

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy an object.
        /// </summary>
        /// <typeparam name="T">Type.</typeparam>
        /// <param name="source">Source object.</param>
        /// <returns>Copy.</returns>
        public static T CopyObject<T>(T source) where T : class
        {
            if (source == null) return default(T)!;

            try
            {
                string xml = SerializeXml(source, false);
                return DeserializeXml<T>(xml);
            }
            catch (Exception)
            {
                return default(T)!;
            }
        }

        /// <summary>
        /// Deserialize XML.
        /// </summary>
        /// <typeparam name="T">Type.</typeparam>
        /// <param name="bytes">XML data.</param>
        /// <returns>Instance.</returns>
        public static T DeserializeXml<T>(byte[] bytes) where T : class
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return DeserializeXml<T>(Encoding.UTF8.GetString(bytes));
        }

        /// <summary>
        /// Deserialize XML.
        /// </summary>
        /// <typeparam name="T">Type.</typeparam>
        /// <param name="xml">XML string.</param>
        /// <returns>Instance.</returns>
        public static T DeserializeXml<T>(string xml) where T : class
        {
            if (String.IsNullOrEmpty(xml)) throw new ArgumentNullException(nameof(xml));

            using (TelemetryStage stage = RequestTelemetry.StartCurrentStage(S3ServerTelemetryNames.StageDeserialize))
            {
                try
                {
                    return DeserializeXmlCore<T>(xml);
                }
                catch (Exception e)
                {
                    TelemetryStage.Fail(stage, e);
                    throw;
                }
            }
        }

        /// <summary>
        /// Serialize XML.
        /// Every element is placed in the Amazon S3 namespace (S3XmlNamespace), as Amazon S3 does for response bodies,
        /// with two exceptions that also match Amazon S3: a root Error is serialized with no namespace, and a root
        /// BucketLoggingStatus uses S3LoggingXmlNamespace.  Nested types (including an Error inside a DeleteResult)
        /// inherit the root namespace.
        /// </summary>
        /// <param name="obj">Object.  Cannot be null.</param>
        /// <param name="pretty">Pretty print.  Default is false.</param>
        /// <returns>XML string.</returns>
        /// <exception cref="ArgumentNullException">Thrown if obj is null.</exception>
        public static string SerializeXml(object obj, bool pretty = false)
        {
            return SerializeXml(obj, pretty, false);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Serializes an object into an XML string with formatting and URL-encoding options.
        /// </summary>
        internal static string SerializeXml(object obj, bool pretty, bool urlEncodeListingValues)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            using (TelemetryStage stage = RequestTelemetry.StartCurrentStage(S3ServerTelemetryNames.StageSerialize))
            {
                try
                {
                    return SerializeXmlCore(obj, pretty, urlEncodeListingValues);
                }
                catch (Exception e)
                {
                    TelemetryStage.Fail(stage, e);
                    throw;
                }
            }
        }

        #endregion

        #region Private-Methods

        private static T DeserializeXmlCore<T>(string xml) where T : class
        {
            // remove preamble if exists
            if (xml.StartsWith(_ByteOrderMarkUtf8, StringComparison.Ordinal))
            {
                xml = xml.TrimStart(_ByteOrderMarkUtf8.ToCharArray());
            }

            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = false,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 10 * 1024 * 1024
            };

            using (var stringReader = new StringReader(xml))
            using (var reader = XmlReader.Create(stringReader, settings))
            {
                return S3XmlGeneratedSerializer.Deserialize<T>(reader);
            }
        }

        private static string SerializeXmlCore(object obj, bool pretty, bool urlEncodeListingValues)
        {
            using (var stream = new MemoryStream())
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = Encoding.UTF8,
                    NewLineChars = Environment.NewLine,
                    ConformanceLevel = ConformanceLevel.Document,
                    Indent = pretty
                };

                using (var writer = XmlWriter.Create(stream, settings))
                {
                    S3XmlGeneratedSerializer.SerializeObject(writer, obj, urlEncodeListingValues);
                    writer.Flush();
                    byte[] bytes = stream.ToArray();
                    string ret = Encoding.UTF8.GetString(bytes, 0, bytes.Length);

                    // remove preamble if exists
                    if (ret.StartsWith(_ByteOrderMarkUtf8, StringComparison.Ordinal))
                    {
                        ret = ret.TrimStart(_ByteOrderMarkUtf8.ToCharArray());
                    }

                    return ret;
                }
            }
        }

        #endregion
    }
