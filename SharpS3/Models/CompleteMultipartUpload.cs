namespace SharpS3.Models
{
    using System;
    using System.Collections.Generic;
    using System.Xml.Serialization;

    /// <summary>
    /// Complete multipart upload request.
    /// </summary>
    [XmlRoot(ElementName = "CompleteMultipartUpload")]
    public class CompleteMultipartUpload
    {
        #region Public-Members

        /// <summary>
        /// Parts.
        /// </summary>
        [XmlElement(ElementName = "Part", IsNullable = false)]
        public List<Part> Parts { get; set; } = new();

        #endregion

        #region Private-Members

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CompleteMultipartUpload()
        {

        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="parts">Parts.</param>
        public CompleteMultipartUpload(List<Part>? parts)
        {
            if (parts != null)
                Parts = parts;
        }

        #endregion

        #region Public-Methods

        #endregion

        #region Private-Methods

        #endregion
    }
}
