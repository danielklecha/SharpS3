namespace SharpS3.Models
{
    using System.Collections.Generic;
    using System.Xml.Serialization;

    /// <summary>
    /// Tag set.
    /// </summary>
    [XmlRoot(ElementName = "TagSet")]
    public class TagSet
    {
        #region Public-Members

        /// <summary>
        /// Tag.
        /// </summary>
        [XmlElement(ElementName = "Tag", IsNullable = true)]
        public List<Tag> Tags { get; set; } = new();

        #endregion

        #region Private-Members

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public TagSet()
        {
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="tags">Tags.</param>
        public TagSet(List<Tag>? tags = null)
        {
            if (tags != null)
                Tags = tags;
        }

        #endregion
    }
}
