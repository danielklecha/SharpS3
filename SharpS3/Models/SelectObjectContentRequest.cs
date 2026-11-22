namespace SharpS3.Models
{
    using System;
    using System.Collections.Generic;
    using System.Xml.Serialization;

    /// <summary>
    /// Select object content request.
    /// </summary>
    [XmlRoot(ElementName = "SelectObjectContentRequest")]
    public class SelectObjectContentRequest
    {
        #region Public-Members

        /// <summary>
        /// Expression.
        /// </summary>
        [XmlElement(ElementName = "Expression", IsNullable = false)]
        public string Expression { get; set; } = null!;

        /// <summary>
        /// Expression type.
        /// </summary>
        [XmlElement(ElementName = "ExpressionType", IsNullable = false)]
        public ExpressionTypeEnum ExpressionType { get; set; } = ExpressionTypeEnum.SQL;

        /// <summary>
        /// Request progress.
        /// </summary>
        [XmlElement(ElementName = "RequestProgress", IsNullable = true)]
        public RequestProgress? RequestProgress { get; set; } = new RequestProgress();

        /// <summary>
        /// Input serialization.
        /// </summary>
        [XmlElement(ElementName = "InputSerialization", IsNullable = false)]
        public InputSerialization InputSerialization { get; set; } = new InputSerialization();

        /// <summary>
        /// Output serialization.
        /// </summary>
        [XmlElement(ElementName = "OutputSerialization", IsNullable = false)]
        public OutputSerialization OutputSerialization { get; set; } = new OutputSerialization();

        /// <summary>
        /// Scan range.
        /// </summary>
        [XmlElement(ElementName = "ScanRange", IsNullable = true)]
        public ScanRange? ScanRange { get; set; } = new ScanRange();

        #endregion

        #region Private-Members

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public SelectObjectContentRequest()
        {

        }

        #endregion

        #region Public-Methods

        #endregion

        #region Private-Methods

        #endregion
    }
}
