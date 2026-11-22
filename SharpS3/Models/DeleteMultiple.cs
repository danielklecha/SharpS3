namespace SharpS3.Models
{
    using System;
    using System.Collections.Generic;
    using System.Xml.Serialization;

    /// <summary>
    /// Request to delete multiple resources.
    /// </summary>
    [XmlRoot(ElementName = "Delete", IsNullable = true)]
    public class DeleteMultiple
    {
        #region Public-Members

        /// <summary>
        /// Enable or disable quiet deletion.
        /// </summary>
        [XmlElement(ElementName = "Quiet")]
        public bool Quiet { get; set; } = false;

        /// <summary>
        /// List of objects to delete.
        /// </summary>
        [XmlElement(ElementName = "Object", IsNullable = true)]
        public List<Object> Objects { get; set; } = new();

        #endregion

        #region Private-Members

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public DeleteMultiple()
        {

        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="quiet">Quiet.</param>
        /// <param name="objects">Objects.</param>
        public DeleteMultiple(bool quiet, List<Object> objects)
        {
            Quiet = quiet;
            Objects = objects ?? throw new ArgumentNullException(nameof(objects));
        }

        #endregion

        #region Public-Methods

        #endregion

        #region Private-Methods

        #endregion
    }
}
