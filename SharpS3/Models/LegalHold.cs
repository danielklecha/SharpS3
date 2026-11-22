namespace SharpS3.Models
{
    using System;
    using System.Collections.Generic;
    using System.Xml.Serialization;

    /// <summary>
    /// Legal hold status of a resource.
    /// </summary>
    [XmlRoot(ElementName = "LegalHold", IsNullable = true)]
    public class LegalHold
    {
        #region Public-Members

        /// <summary>
        /// Legal hold status.
        /// Valid values are null, ON, OFF.
        /// </summary>
        [XmlElement(ElementName = "Status", IsNullable = true)]
        public string Status
        {
            get => _Status;
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    _Status = value;
                    return;
                }

                if (!_StatusValidValues.Contains(value))
                    throw new ArgumentException($"Unknown Status '{value}'.");

                _Status = value;
            }
        }

        #endregion

        #region Private-Members

        private string _Status = "OFF";
        private static readonly HashSet<string> _StatusValidValues = new(StringComparer.Ordinal)
        {
            "ON",
            "OFF"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public LegalHold()
        {

        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="status">Status.  Valid values are null, ON, OFF.</param>
        public LegalHold(string status)
        {
            Status = status;
        }

        #endregion

        #region Public-Methods

        #endregion

        #region Private-Methods

        #endregion
    }
}
